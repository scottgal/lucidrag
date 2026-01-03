using System.Text.RegularExpressions;
using Mostlylucid.GraphRag.Services;
using Mostlylucid.GraphRag.Storage;

namespace Mostlylucid.GraphRag.Extraction;

/// <summary>
/// Entity extraction using BERT embeddings + IDF statistics + structural signals.
/// 
/// No hardcoded entity lists. Instead, we detect entities through:
/// 1. IDF-based term importance (rare terms across corpus = likely entities)
/// 2. Semantic clustering (BERT embeddings to find coherent concepts)
/// 3. Structural signals (markdown headings, code blocks, links)
/// 4. Co-occurrence patterns (terms that frequently appear together)
/// 
/// See also: Mostlylucid.DocSummarizer for BM25Scorer and embedding infrastructure.
/// </summary>
public sealed class EntityExtractor : IEntityExtractor
{
    private readonly GraphRagDb _db;
    private readonly EmbeddingService _embedder;
    private readonly OllamaClient? _llm;
    private readonly ExtractionMode _mode;
    private int _llmCallCount;
    
    // Structural patterns - what the author marked as important
    private static readonly Regex HeadingRx = new(@"^#{1,3}\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex InlineCodeRx = new(@"`([^`]{2,50})`", RegexOptions.Compiled);
    private static readonly Regex InternalLinkRx = new(@"\[([^\]]+)\]\(/blog/([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex ExternalLinkRx = new(@"\[([^\]]+)\]\((https?://[^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex TokenRx = new(@"\b[A-Za-z][A-Za-z0-9_\.#\+\-]*[A-Za-z0-9]\b", RegexOptions.Compiled);
    
    // Minimum thresholds - tuned to reduce noise
    private const double MinIdfThreshold = 3.5;  // Terms must be quite rare (log(N/df) > 3.5)
    private const int MinMentionCount = 3;       // Must appear 3+ times to be significant
    private const int MinTermLength = 3;         // Skip very short terms
    private const double MinEmbeddingSimilarity = 0.85; // For deduplication
    private const int MaxCandidatesForEmbedding = 500; // Limit embedding batch size

    public EntityExtractor(GraphRagDb db, EmbeddingService embedder, OllamaClient? llm = null, 
        ExtractionMode mode = ExtractionMode.Heuristic)
    {
        _db = db;
        _embedder = embedder;
        _llm = llm;
        _mode = mode;
    }

    public async Task<ExtractionResult> ExtractAsync(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _llmCallCount = 0;
        
        var chunks = await _db.GetAllChunksAsync();
        var stats = new ExtractionStats();
        if (chunks.Count == 0) 
            return new ExtractionResult { Mode = _mode };

        // ═══════════════════════════════════════════════════════════════════
        // Phase 1: Build corpus IDF statistics
        // High IDF = term is rare in corpus = more likely to be an entity
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, 1, "Computing corpus statistics (IDF)..."));
        var (termIdf, termDocFreq) = ComputeIdfStats(chunks);
        
        // ═══════════════════════════════════════════════════════════════════
        // Phase 2: Extract candidates using IDF + structural signals
        // ═══════════════════════════════════════════════════════════════════
        var candidates = new Dictionary<string, EntityCandidate>(StringComparer.OrdinalIgnoreCase);
        var coOccurrences = new Dictionary<(string, string), int>();
        var linkRelationships = new List<(string Source, string Target, string Type, string[] ChunkIds)>();

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = chunks[i];
            
            // Extract from this chunk using multiple signals
            var chunkCandidates = ExtractFromChunk(chunk.Text, termIdf);
            
            foreach (var c in chunkCandidates)
            {
                if (candidates.TryGetValue(c.Name, out var existing))
                {
                    existing.ChunkIds.Add(chunk.Id);
                    existing.MentionCount++;
                    existing.Confidence = Math.Max(existing.Confidence, c.Confidence);
                    foreach (var s in c.Signals) existing.Signals.Add(s);
                }
                else
                {
                    c.ChunkIds.Add(chunk.Id);
                    candidates[c.Name] = c;
                }
            }
            
            // Extract explicit relationships from links
            foreach (var link in ExtractLinks(chunk.Text, chunk.Id))
                linkRelationships.Add(link);
            
            // Track co-occurrences for relationship detection
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

            if (i % 50 == 0)
                progress?.Report(new ProgressInfo(i, chunks.Count, $"Extracting: {candidates.Count} candidates"));
        }

        stats.CandidatesFound = candidates.Count;
        stats.LinksFound = linkRelationships.Count;

        // ═══════════════════════════════════════════════════════════════════
        // Phase 3: Filter by significance (must have multiple signals or high confidence)
        // ═══════════════════════════════════════════════════════════════════
        var significant = candidates.Values
            .Where(c => c.MentionCount >= MinMentionCount || 
                       (c.Confidence >= 0.8 && c.Signals.Count >= 2))
            .OrderByDescending(c => c.MentionCount * c.Confidence)
            .Take(MaxCandidatesForEmbedding) // Limit for performance
            .ToList();

        // ═══════════════════════════════════════════════════════════════════
        // Phase 4: Deduplicate using BERT embeddings
        // "Docker" and "docker" merge; "ASP.NET Core" and "ASP.NET" may merge
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, 1, "Deduplicating with BERT embeddings..."));
        var deduped = await DeduplicateWithEmbeddingsAsync(significant, ct);
        stats.EntitiesAfterDedup = deduped.Count;

        // ═══════════════════════════════════════════════════════════════════
        // Phase 5: Classify entity types (LLM or structural heuristics)
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, 1, "Classifying entities..."));
        
        var llmAvailable = _llm != null && await _llm.IsAvailableAsync(ct);
        var usedLlm = false;
        
        if (_mode == ExtractionMode.Llm)
        {
            // LLM mode: require LLM for classification
            if (!llmAvailable)
                throw new InvalidOperationException("ExtractionMode.Llm requires an available Ollama model.");
            
            await ClassifyWithLlmAsync(deduped, ct);
            usedLlm = true;
        }
        else
        {
            // Heuristic mode: use LLM if available for better classification, otherwise heuristics
            if (llmAvailable)
            {
                await ClassifyWithLlmAsync(deduped, ct);
                usedLlm = true;
            }
            else
            {
                ClassifyBySignals(deduped);
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 6: Store entities and relationships
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, deduped.Count, "Storing entities..."));
        foreach (var e in deduped)
            await _db.UpsertEntityAsync(EntityId(e.Name), e.Name, e.Type, e.Description, e.ChunkIds.ToArray());
        stats.EntitiesStored = deduped.Count;

        progress?.Report(new ProgressInfo(0, 1, "Storing relationships..."));
        stats.LinkRelationshipsStored = await StoreLinkRelationshipsAsync(linkRelationships, deduped);
        stats.CoOccurrenceRelationshipsStored = await StoreCoOccurrenceRelationshipsAsync(coOccurrences, deduped);

        sw.Stop();
        
        return new ExtractionResult
        {
            EntitiesExtracted = stats.EntitiesStored,
            RelationshipsExtracted = stats.TotalRelationships,
            LlmCallCount = _llmCallCount,
            Duration = sw.Elapsed,
            Mode = usedLlm ? ExtractionMode.Llm : ExtractionMode.Heuristic
        };
    }

    /// <summary>
    /// Compute IDF (Inverse Document Frequency) for all terms in corpus.
    /// High IDF = term appears in few documents = likely an entity.
    /// Low IDF = term appears everywhere = likely a common word.
    /// </summary>
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
        
        // IDF = log(N / df) where N = total docs, df = docs containing term
        var idf = docFreq.ToDictionary(
            kv => kv.Key,
            kv => Math.Log((double)chunks.Count / kv.Value),
            StringComparer.OrdinalIgnoreCase);
        
        return (idf, docFreq);
    }

    /// <summary>
    /// Extract entity candidates from a chunk using multiple signals:
    /// - High IDF terms (rare in corpus)
    /// - Structural markers (headings, code, bold)
    /// - Syntactic patterns (PascalCase, dotted.names)
    /// </summary>
    private List<EntityCandidate> ExtractFromChunk(string text, Dictionary<string, double> termIdf)
    {
        var candidates = new Dictionary<string, EntityCandidate>(StringComparer.OrdinalIgnoreCase);

        // Signal 1: Terms with high IDF (rare = important)
        foreach (Match m in TokenRx.Matches(text))
        {
            var term = m.Value;
            if (term.Length < MinTermLength) continue;
            
            if (termIdf.TryGetValue(term, out var idf) && idf >= MinIdfThreshold)
            {
                // Confidence scales with IDF (capped at 0.9)
                var confidence = Math.Min(0.9, 0.4 + idf * 0.05);
                AddCandidate(candidates, term, "concept", confidence, "high_idf");
            }
        }

        // Signal 2: Headings (author marked as important)
        foreach (Match m in HeadingRx.Matches(text))
        {
            var heading = m.Groups[1].Value.Trim();
            // Extract significant terms from heading
            foreach (Match tm in TokenRx.Matches(heading))
            {
                var term = tm.Value;
                if (term.Length >= 3)
                    AddCandidate(candidates, term, "concept", 0.85, "heading");
            }
        }

        // Signal 3: Inline code (technical terms, identifiers)
        foreach (Match m in InlineCodeRx.Matches(text))
        {
            var code = m.Groups[1].Value.Trim();
            if (IsIdentifier(code))
                AddCandidate(candidates, code, "code", 0.9, "inline_code");
        }

        // Signal 4: Link text (what author chose to reference)
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

    private static void AddCandidate(Dictionary<string, EntityCandidate> dict, string name, string type, double confidence, string signal)
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

    /// <summary>
    /// Use BERT embeddings to find and merge similar entities.
    /// "Docker" ≈ "docker"; "Entity Framework" ≈ "EF Core" (semantically)
    /// </summary>
    private async Task<List<EntityCandidate>> DeduplicateWithEmbeddingsAsync(List<EntityCandidate> candidates, CancellationToken ct)
    {
        if (candidates.Count <= 1) return candidates;

        // Embed all entity names
        var embeddings = await _embedder.EmbedBatchAsync(candidates.Select(c => c.Name), ct);
        
        var merged = new List<EntityCandidate>();
        var used = new HashSet<int>();

        for (int i = 0; i < candidates.Count; i++)
        {
            if (used.Contains(i)) continue;
            
            var canonical = candidates[i];
            used.Add(i);

            // Find similar entities and merge them
            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (used.Contains(j)) continue;
                
                var similarity = CosineSimilarity(embeddings[i], embeddings[j]);
                var stringSim = NormalizedLevenshtein(canonical.Name, candidates[j].Name);
                
                if (similarity >= MinEmbeddingSimilarity || stringSim >= 0.8)
                {
                    // Merge: combine counts, chunks, signals
                    canonical.MentionCount += candidates[j].MentionCount;
                    canonical.ChunkIds.UnionWith(candidates[j].ChunkIds);
                    foreach (var s in candidates[j].Signals) canonical.Signals.Add(s);
                    used.Add(j);
                }
            }
            
            merged.Add(canonical);
        }
        
        return merged;
    }

    private IEnumerable<(string Source, string Target, string Type, string[] ChunkIds)> ExtractLinks(string text, string chunkId)
    {
        // Internal links provide explicit "references" relationships
        foreach (Match m in InternalLinkRx.Matches(text))
        {
            var linkText = m.Groups[1].Value;
            var slug = m.Groups[2].Value;
            if (linkText.Length >= 3)
                yield return (linkText, $"blog:{slug}", "references", [chunkId]);
        }

        // External links create entities for domains/repos
        foreach (Match m in ExternalLinkRx.Matches(text))
        {
            var linkText = m.Groups[1].Value;
            if (Uri.TryCreate(m.Groups[2].Value, UriKind.Absolute, out var uri))
            {
                var domain = uri.Host.Replace("www.", "");
                if (domain == "github.com" && uri.Segments.Length >= 3)
                {
                    var repo = $"{uri.Segments[1].TrimEnd('/')}/{uri.Segments[2].TrimEnd('/')}";
                    yield return (linkText, $"github:{repo}", "links_to", [chunkId]);
                }
                else if (linkText.Length >= 3)
                {
                    yield return (linkText, $"site:{domain}", "links_to", [chunkId]);
                }
            }
        }
    }

    private async Task ClassifyWithLlmAsync(List<EntityCandidate> entities, CancellationToken ct)
    {
        // Process in batches of 50 to avoid prompt size issues
        const int batchSize = 50;
        var lookup = entities.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        
        for (int i = 0; i < entities.Count; i += batchSize)
        {
            var batch = entities.Skip(i).Take(batchSize).ToList();
            var list = string.Join("\n", batch.Select(e => $"- {e.Name}"));

            const string schema = """{"name":"...","type":"...","desc":"..."}""";
            var response = await _llm!.GenerateAsync($"""
                Classify these technical entities. Return JSONL only (one JSON object per line).
                No markdown, no commentary.
                
                Schema: {schema}
                Types: technology, framework, library, language, tool, database, service, concept
                
                Entities:
                {list}
                """, 0.2, ct);
            
            _llmCallCount++;

            // Parse JSONL response (tolerant of LLM quirks)
            foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith('{')) continue;
                
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                    var root = doc.RootElement;
                    
                    if (root.TryGetProperty("name", out var nameProp))
                    {
                        var name = nameProp.GetString();
                        if (name != null && lookup.TryGetValue(name, out var e))
                        {
                            if (root.TryGetProperty("type", out var typeProp))
                                e.Type = typeProp.GetString()?.ToLowerInvariant() ?? e.Type;
                            if (root.TryGetProperty("desc", out var descProp))
                                e.Description = descProp.GetString();
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Fallback: try pipe-delimited format
                    var parts = trimmed.Split('|');
                    if (parts.Length >= 2)
                    {
                        var name = parts[0].Trim().TrimStart('-', '*', ' ', '"');
                        if (lookup.TryGetValue(name, out var e))
                        {
                            e.Type = parts[1].Trim().ToLowerInvariant();
                            if (parts.Length >= 3) e.Description = parts[2].Trim();
                        }
                    }
                }
            }
        }
    }

    private static void ClassifyBySignals(List<EntityCandidate> entities)
    {
        foreach (var e in entities)
        {
            if (e.Signals.Contains("inline_code"))
                e.Type = "code";
            else if (e.Signals.Contains("heading"))
                e.Type = "concept";
            else if (e.Signals.Contains("high_idf"))
                e.Type = "technology";
        }
    }

    private async Task<int> StoreLinkRelationshipsAsync(
        List<(string Source, string Target, string Type, string[] ChunkIds)> links,
        List<EntityCandidate> entities)
    {
        var entityNames = entities.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (var g in links.GroupBy(l => (l.Source, l.Target, l.Type)))
        {
            var (source, target, relType) = g.Key;
            var chunkIds = g.SelectMany(x => x.ChunkIds).Distinct().ToArray();

            // Create target entity for external references
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

    private async Task<int> StoreCoOccurrenceRelationshipsAsync(
        Dictionary<(string, string), int> coOccur,
        List<EntityCandidate> entities)
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

            // Honest: we detected co-occurrence, not semantic relationship
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

    private static double NormalizedLevenshtein(string a, string b)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        var m = a.Length; var n = b.Length;
        var d = new int[m + 1, n + 1];
        for (int i = 0; i <= m; i++) d[i, 0] = i;
        for (int j = 0; j <= n; j++) d[0, j] = j;
        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1.0 - (double)d[m, n] / Math.Max(m, n);
    }
}

public class EntityCandidate
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "concept";
    public string? Description { get; set; }
    public double Confidence { get; set; }
    public int MentionCount { get; set; } = 1;
    public HashSet<string> ChunkIds { get; set; } = [];
    public HashSet<string> Signals { get; set; } = [];
}

public record ExtractionStats
{
    public int CandidatesFound { get; set; }
    public int LinksFound { get; set; }
    public int EntitiesAfterDedup { get; set; }
    public int EntitiesStored { get; set; }
    public int LinkRelationshipsStored { get; set; }
    public int CoOccurrenceRelationshipsStored { get; set; }
    public int TotalRelationships => LinkRelationshipsStored + CoOccurrenceRelationshipsStored;
}
