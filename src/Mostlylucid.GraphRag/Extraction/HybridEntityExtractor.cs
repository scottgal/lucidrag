using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mostlylucid.GraphRag.Services;
using Mostlylucid.GraphRag.Storage;

namespace Mostlylucid.GraphRag.Extraction;

/// <summary>
/// Hybrid entity extraction: heuristic candidate detection + LLM enhancement per document.
/// 
/// Strategy:
/// 1. Use IDF + structural signals to find entity candidates (deterministic, fast)
/// 2. Group candidates by document
/// 3. One LLM call per document to:
///    - Validate/filter candidates
///    - Add descriptions
///    - Extract semantic relationships between candidates
/// 
/// This gives you:
/// - Deterministic entity coverage (heuristics find what's there)
/// - LLM-quality relationships (semantic, not just co-occurrence)
/// - ~N LLM calls (documents) instead of 2N (chunks) for MSFT approach
/// </summary>
public sealed class HybridEntityExtractor : IEntityExtractor
{
    private readonly GraphRagDb _db;
    private readonly EmbeddingService _embedder;
    private readonly OllamaClient _llm;
    private int _llmCallCount;

    // Reuse structural patterns from EntityExtractor
    private static readonly Regex HeadingRx = new(@"^#{1,3}\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex InlineCodeRx = new(@"`([^`]{2,50})`", RegexOptions.Compiled);
    private static readonly Regex InternalLinkRx = new(@"\[([^\]]+)\]\(/blog/([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex ExternalLinkRx = new(@"\[([^\]]+)\]\((https?://[^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex TokenRx = new(@"\b[A-Za-z][A-Za-z0-9_\.#\+\-]*[A-Za-z0-9]\b", RegexOptions.Compiled);

    private const double MinIdfThreshold = 3.5;
    private const int MinMentionCount = 2;
    private const int MinTermLength = 3;
    private const double MinEmbeddingSimilarity = 0.85;
    private const int MaxCandidatesPerDocument = 30;

    public HybridEntityExtractor(GraphRagDb db, EmbeddingService embedder, OllamaClient llm)
    {
        _db = db;
        _embedder = embedder;
        _llm = llm;
    }

    public async Task<ExtractionResult> ExtractAsync(IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _llmCallCount = 0;

        var chunks = await _db.GetAllChunksAsync();
        if (chunks.Count == 0)
            return new ExtractionResult { Mode = ExtractionMode.Hybrid };

        // ═══════════════════════════════════════════════════════════════════
        // Phase 1: Heuristic extraction (same as EntityExtractor)
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, 1, "Computing corpus statistics (IDF)..."));
        var (termIdf, _) = ComputeIdfStats(chunks);

        // Group chunks by document
        var documentChunks = chunks.GroupBy(c => c.DocumentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var allEntities = new Dictionary<string, HybridEntity>(StringComparer.OrdinalIgnoreCase);
        var allRelationships = new List<HybridRelationship>();
        var linkRelationships = new List<(string Source, string Target, string Type, string[] ChunkIds)>();
        var coOccurrences = new Dictionary<(string, string), int>();

        int docIndex = 0;
        int totalDocs = documentChunks.Count;

        foreach (var (docId, docChunks) in documentChunks)
        {
            ct.ThrowIfCancellationRequested();
            docIndex++;

            // ═══════════════════════════════════════════════════════════════
            // Phase 2a: Extract candidates from this document's chunks
            // ═══════════════════════════════════════════════════════════════
            var docCandidates = new Dictionary<string, EntityCandidate>(StringComparer.OrdinalIgnoreCase);
            var docText = new System.Text.StringBuilder();

            foreach (var chunk in docChunks)
            {
                docText.AppendLine(chunk.Text);
                
                var chunkCandidates = ExtractFromChunk(chunk.Text, termIdf);
                foreach (var c in chunkCandidates)
                {
                    if (docCandidates.TryGetValue(c.Name, out var existing))
                    {
                        existing.ChunkIds.Add(chunk.Id);
                        existing.MentionCount++;
                        existing.Confidence = Math.Max(existing.Confidence, c.Confidence);
                        foreach (var s in c.Signals) existing.Signals.Add(s);
                    }
                    else
                    {
                        c.ChunkIds.Add(chunk.Id);
                        docCandidates[c.Name] = c;
                    }
                }

                // Extract explicit link relationships
                foreach (var link in ExtractLinks(chunk.Text, chunk.Id))
                    linkRelationships.Add(link);
                    
                // Track co-occurrences within chunks (fallback for relationship detection)
                var chunkTerms = chunkCandidates.Select(c => c.Name).Distinct().ToList();
                for (int j = 0; j < chunkTerms.Count; j++)
                {
                    for (int k = j + 1; k < chunkTerms.Count; k++)
                    {
                        var pair = string.Compare(chunkTerms[j], chunkTerms[k], StringComparison.OrdinalIgnoreCase) < 0
                            ? (chunkTerms[j], chunkTerms[k]) : (chunkTerms[k], chunkTerms[j]);
                        coOccurrences[pair] = coOccurrences.GetValueOrDefault(pair) + 1;
                    }
                }
            }

            // Filter to significant candidates for this document
            var significantCandidates = docCandidates.Values
                .Where(c => c.MentionCount >= MinMentionCount ||
                           (c.Confidence >= 0.7 && c.Signals.Count >= 2))
                .OrderByDescending(c => c.MentionCount * c.Confidence)
                .Take(MaxCandidatesPerDocument)
                .ToList();

            if (significantCandidates.Count == 0)
            {
                progress?.Report(new ProgressInfo(docIndex, totalDocs, $"Doc {docIndex}/{totalDocs}: no candidates"));
                continue;
            }

            // ═══════════════════════════════════════════════════════════════
            // Phase 2b: LLM enhancement - one call per document
            // Validate entities and extract semantic relationships
            // ═══════════════════════════════════════════════════════════════
            progress?.Report(new ProgressInfo(docIndex, totalDocs, 
                $"Doc {docIndex}/{totalDocs}: enhancing {significantCandidates.Count} candidates with LLM..."));

            var (enhancedEntities, docRelationships) = await EnhanceWithLlmAsync(
                significantCandidates, docText.ToString(), docId, ct);

            // Merge into global collections
            foreach (var e in enhancedEntities)
            {
                if (allEntities.TryGetValue(e.Name, out var existing))
                {
                    existing.ChunkIds.UnionWith(e.ChunkIds);
                    existing.MentionCount += e.MentionCount;
                    if (string.IsNullOrEmpty(existing.Description) && !string.IsNullOrEmpty(e.Description))
                        existing.Description = e.Description;
                }
                else
                {
                    allEntities[e.Name] = e;
                }
            }

            allRelationships.AddRange(docRelationships);
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 3: Deduplicate entities using BERT embeddings
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, 1, "Deduplicating with BERT embeddings..."));
        var deduped = await DeduplicateWithEmbeddingsAsync(allEntities.Values.ToList(), ct);

        // ═══════════════════════════════════════════════════════════════════
        // Phase 4: Store entities and relationships
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, deduped.Count, "Storing entities..."));
        foreach (var e in deduped)
            await _db.UpsertEntityAsync(EntityId(e.Name), e.Name, e.Type, e.Description, e.ChunkIds.ToArray());

        progress?.Report(new ProgressInfo(0, 1, "Storing relationships..."));
        var linkRelsStored = await StoreLinkRelationshipsAsync(linkRelationships, deduped);
        var semanticRelsStored = await StoreSemanticRelationshipsAsync(allRelationships, deduped);
        var coOccurRelsStored = await StoreCoOccurrenceRelationshipsAsync(coOccurrences, deduped);

        sw.Stop();

        return new ExtractionResult
        {
            EntitiesExtracted = deduped.Count,
            RelationshipsExtracted = linkRelsStored + semanticRelsStored + coOccurRelsStored,
            LlmCallCount = _llmCallCount,
            Duration = sw.Elapsed,
            Mode = ExtractionMode.Hybrid
        };
    }

    /// <summary>
    /// Single LLM call per document: validate entities and extract relationships.
    /// </summary>
    private async Task<(List<HybridEntity> Entities, List<HybridRelationship> Relationships)> EnhanceWithLlmAsync(
        List<EntityCandidate> candidates, string docText, string docId, CancellationToken ct)
    {
        var candidateList = string.Join("\n", candidates.Select(c => $"- {c.Name}"));
        var truncatedText = docText.Length > 3000 ? docText[..3000] + "..." : docText;

        const string entitySchema = """{"name":"...","type":"technology|framework|library|language|tool|database|service|concept","desc":"brief description"}""";
        const string relSchema = """{"src":"entity1","rel":"uses|implements|configures|extends|part_of|related_to","tgt":"entity2"}""";
        
        var prompt = $"""
            Given this technical document and these entity candidates extracted from it:

            CANDIDATES:
            {candidateList}

            DOCUMENT TEXT:
            {truncatedText}

            For each valid entity, output a JSON object on its own line:
            {entitySchema}

            Then output relationships between entities (one per line):
            {relSchema}

            Rules:
            - Only include entities that are genuinely technical concepts (not common words)
            - Only include relationships that are clearly implied by the document
            - Keep descriptions under 20 words
            - Output JSONL only, no markdown or commentary
            """;

        var response = await _llm.GenerateAsync(prompt, 0.2, ct);
        _llmCallCount++;

        var entities = new List<HybridEntity>();
        var relationships = new List<HybridRelationship>();
        var candidateLookup = candidates.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{')) continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                // Check if it's an entity or relationship
                if (root.TryGetProperty("name", out var nameProp))
                {
                    // Entity
                    var name = nameProp.GetString();
                    if (name != null && candidateLookup.TryGetValue(name, out var original))
                    {
                        entities.Add(new HybridEntity
                        {
                            Name = name,
                            Type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "concept" : "concept",
                            Description = root.TryGetProperty("desc", out var d) ? d.GetString() : null,
                            MentionCount = original.MentionCount,
                            ChunkIds = original.ChunkIds
                        });
                    }
                }
                else if (root.TryGetProperty("src", out var srcProp))
                {
                    // Relationship
                    var src = srcProp.GetString();
                    var tgt = root.TryGetProperty("tgt", out var tgtProp) ? tgtProp.GetString() : null;
                    var rel = root.TryGetProperty("rel", out var relProp) ? relProp.GetString() : "related_to";

                    if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt))
                    {
                        // Find chunk IDs where both entities appear
                        var srcChunks = candidateLookup.TryGetValue(src, out var s) ? s.ChunkIds : new HashSet<string>();
                        var tgtChunks = candidateLookup.TryGetValue(tgt, out var t) ? t.ChunkIds : new HashSet<string>();
                        var sharedChunks = srcChunks.Intersect(tgtChunks).ToArray();

                        relationships.Add(new HybridRelationship
                        {
                            Source = src,
                            Target = tgt,
                            Type = rel ?? "related_to",
                            ChunkIds = sharedChunks.Length > 0 ? sharedChunks : srcChunks.Take(1).ToArray()
                        });
                    }
                }
            }
            catch (JsonException)
            {
                // Skip malformed JSON
            }
        }

        // If LLM didn't validate some candidates, include them with heuristic types
        foreach (var c in candidates)
        {
            if (!entities.Any(e => e.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
            {
                // Include high-confidence candidates that LLM missed
                if (c.Confidence >= 0.8 || c.MentionCount >= 3)
                {
                    entities.Add(new HybridEntity
                    {
                        Name = c.Name,
                        Type = ClassifyBySignals(c),
                        MentionCount = c.MentionCount,
                        ChunkIds = c.ChunkIds
                    });
                }
            }
        }

        return (entities, relationships);
    }

    private static string ClassifyBySignals(EntityCandidate c)
    {
        if (c.Signals.Contains("inline_code")) return "code";
        if (c.Signals.Contains("heading")) return "concept";
        if (c.Signals.Contains("high_idf")) return "technology";
        return "concept";
    }

    #region Heuristic extraction (shared with EntityExtractor)

    private (Dictionary<string, double> Idf, Dictionary<string, int> DocFreq) ComputeIdfStats(List<ChunkResult> chunks)
    {
        var docFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in chunks)
        {
            var terms = TokenRx.Matches(chunk.Text)
                .Select(m => m.Value)
                .Where(t => t.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var term in terms)
                docFreq[term] = docFreq.GetValueOrDefault(term) + 1;
        }

        var idf = docFreq.ToDictionary(
            kv => kv.Key,
            kv => Math.Log((double)chunks.Count / kv.Value),
            StringComparer.OrdinalIgnoreCase);

        return (idf, docFreq);
    }

    private List<EntityCandidate> ExtractFromChunk(string text, Dictionary<string, double> termIdf)
    {
        var candidates = new Dictionary<string, EntityCandidate>(StringComparer.OrdinalIgnoreCase);

        // High IDF terms
        foreach (Match m in TokenRx.Matches(text))
        {
            var term = m.Value;
            if (term.Length < MinTermLength) continue;

            if (termIdf.TryGetValue(term, out var idf) && idf >= MinIdfThreshold)
            {
                var confidence = Math.Min(0.9, 0.4 + idf * 0.05);
                AddCandidate(candidates, term, "concept", confidence, "high_idf");
            }
        }

        // Headings
        foreach (Match m in HeadingRx.Matches(text))
        {
            var heading = m.Groups[1].Value.Trim();
            foreach (Match tm in TokenRx.Matches(heading))
            {
                var term = tm.Value;
                if (term.Length >= 3)
                    AddCandidate(candidates, term, "concept", 0.85, "heading");
            }
        }

        // Inline code
        foreach (Match m in InlineCodeRx.Matches(text))
        {
            var code = m.Groups[1].Value.Trim();
            if (IsIdentifier(code))
                AddCandidate(candidates, code, "code", 0.9, "inline_code");
        }

        // Link text
        foreach (Match m in InternalLinkRx.Matches(text))
        {
            var linkText = m.Groups[1].Value.Trim();
            if (linkText.Length >= 3 && linkText.Length <= 50)
                AddCandidate(candidates, linkText, "concept", 0.75, "link_text");
        }

        return candidates.Values.ToList();
    }

    private static bool IsIdentifier(string s) =>
        s.Length >= 2 && s.Length <= 50 &&
        !s.Contains('(') && !s.Contains('{') && !s.Contains('=') && !s.Contains(';') &&
        Regex.IsMatch(s, @"^[A-Za-z_][A-Za-z0-9_\.\-\#\+]*$");

    private static void AddCandidate(Dictionary<string, EntityCandidate> dict, string name, string type,
        double confidence, string signal)
    {
        if (dict.TryGetValue(name, out var existing))
        {
            if (confidence > existing.Confidence)
            {
                existing.Type = type;
                existing.Confidence = confidence;
            }
            existing.Signals.Add(signal);
        }
        else
        {
            dict[name] = new EntityCandidate
            {
                Name = name,
                Type = type,
                Confidence = confidence,
                Signals = [signal]
            };
        }
    }

    private IEnumerable<(string Source, string Target, string Type, string[] ChunkIds)> ExtractLinks(
        string text, string chunkId)
    {
        foreach (Match m in InternalLinkRx.Matches(text))
        {
            var linkText = m.Groups[1].Value;
            var slug = m.Groups[2].Value;
            if (linkText.Length >= 3)
                yield return (linkText, $"blog:{slug}", "references", new[] { chunkId });
        }

        foreach (Match m in ExternalLinkRx.Matches(text))
        {
            var linkText = m.Groups[1].Value;
            if (Uri.TryCreate(m.Groups[2].Value, UriKind.Absolute, out var uri))
            {
                var domain = uri.Host.Replace("www.", "");
                if (domain == "github.com" && uri.Segments.Length >= 3)
                {
                    var repo = $"{uri.Segments[1].TrimEnd('/')}/{uri.Segments[2].TrimEnd('/')}";
                    yield return (linkText, $"github:{repo}", "links_to", new[] { chunkId });
                }
                else if (linkText.Length >= 3)
                {
                    yield return (linkText, $"site:{domain}", "links_to", new[] { chunkId });
                }
            }
        }
    }

    #endregion

    #region Deduplication and storage

    private async Task<List<HybridEntity>> DeduplicateWithEmbeddingsAsync(List<HybridEntity> entities,
        CancellationToken ct)
    {
        if (entities.Count <= 1) return entities;

        var sorted = entities.OrderByDescending(e => e.MentionCount).Take(500).ToList();
        var embeddings = await _embedder.EmbedBatchAsync(sorted.Select(e => e.Name), ct);

        var merged = new List<HybridEntity>();
        var used = new HashSet<int>();

        for (int i = 0; i < sorted.Count; i++)
        {
            if (used.Contains(i)) continue;
            var canonical = sorted[i];
            used.Add(i);

            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (used.Contains(j)) continue;
                var similarity = CosineSimilarity(embeddings[i], embeddings[j]);
                if (similarity >= MinEmbeddingSimilarity)
                {
                    canonical.MentionCount += sorted[j].MentionCount;
                    canonical.ChunkIds.UnionWith(sorted[j].ChunkIds);
                    if (string.IsNullOrEmpty(canonical.Description) && !string.IsNullOrEmpty(sorted[j].Description))
                        canonical.Description = sorted[j].Description;
                    used.Add(j);
                }
            }
            merged.Add(canonical);
        }

        // Add remaining entities
        var remaining = entities.Skip(500).Where(e =>
            !merged.Any(m => m.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase)));
        merged.AddRange(remaining);

        return merged;
    }

    private async Task<int> StoreLinkRelationshipsAsync(
        List<(string Source, string Target, string Type, string[] ChunkIds)> links,
        List<HybridEntity> entities)
    {
        var entityNames = entities.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (var g in links.GroupBy(l => (l.Source, l.Target, l.Type)))
        {
            var (source, target, relType) = g.Key;
            var chunkIds = g.SelectMany(x => x.ChunkIds).Distinct().ToArray();

            string targetId;
            if (target.StartsWith("blog:") || target.StartsWith("github:") || target.StartsWith("site:"))
            {
                targetId = EntityId(target);
                var targetType = target.StartsWith("blog:") ? "document"
                    : target.StartsWith("github:") ? "repository" : "website";
                await _db.UpsertEntityAsync(targetId, target, targetType, null, chunkIds);
            }
            else if (entityNames.Contains(target))
                targetId = EntityId(target);
            else
                continue;

            var sourceId = EntityId(source);
            if (!entityNames.Contains(source))
                await _db.UpsertEntityAsync(sourceId, source, "concept", null, chunkIds);

            await _db.UpsertRelationshipAsync($"r_{sourceId}_{targetId}", sourceId, targetId, relType, null, chunkIds);
            count++;
        }
        return count;
    }

    private async Task<int> StoreSemanticRelationshipsAsync(List<HybridRelationship> relationships,
        List<HybridEntity> entities)
    {
        var lookup = entities.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (var g in relationships.GroupBy(r => (r.Source, r.Target, r.Type)))
        {
            if (!lookup.ContainsKey(g.Key.Source) || !lookup.ContainsKey(g.Key.Target))
                continue;

            var srcId = EntityId(g.Key.Source);
            var tgtId = EntityId(g.Key.Target);
            var chunkIds = g.SelectMany(r => r.ChunkIds).Distinct().ToArray();

            await _db.UpsertRelationshipAsync($"r_{srcId}_{tgtId}_{g.Key.Type}",
                srcId, tgtId, g.Key.Type, null, chunkIds);
            count++;
        }
        return count;
    }

    private async Task<int> StoreCoOccurrenceRelationshipsAsync(
        Dictionary<(string, string), int> coOccur,
        List<HybridEntity> entities)
    {
        var lookup = entities.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var count = 0;

        // Only significant co-occurrences (appear together 2+ times)
        foreach (var ((a, b), occurrences) in coOccur.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).Take(500))
        {
            if (!lookup.TryGetValue(a, out var ea) || !lookup.TryGetValue(b, out var eb))
                continue;

            var srcId = EntityId(a);
            var tgtId = EntityId(b);
            var chunkIds = ea.ChunkIds.Intersect(eb.ChunkIds).ToArray();

            await _db.UpsertRelationshipAsync($"r_{srcId}_{tgtId}", srcId, tgtId, "co_occurs_with", null, chunkIds);
            count++;
        }
        return count;
    }

    private static string EntityId(string name) =>
        $"e_{Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]", "_")}";

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb) + 1e-10f);
    }

    #endregion
}

internal class HybridEntity
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "concept";
    public string? Description { get; set; }
    public int MentionCount { get; set; } = 1;
    public HashSet<string> ChunkIds { get; set; } = [];
}

internal class HybridRelationship
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Type { get; set; } = "related_to";
    public string[] ChunkIds { get; set; } = [];
}
