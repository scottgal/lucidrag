using System.Text.RegularExpressions;
using Mostlylucid.GraphRag.Services;
using Mostlylucid.GraphRag.Storage;

namespace Mostlylucid.GraphRag.Extraction;

/// <summary>
/// Profile-aware entity extractor that uses:
/// 1. ONNX NER model for span detection (when available)
/// 2. Heuristic extraction as fallback
/// 3. Profile-based type classification
///
/// The profile determines which entity types to extract based on content type:
/// - Technical docs: technology, framework, library, api, pattern
/// - Legal docs: party, clause, term, obligation
/// - Code: class, function, variable, namespace
/// </summary>
public sealed class ProfileAwareEntityExtractor : IEntityExtractor
{
    private readonly GraphRagDb _db;
    private readonly EmbeddingService _embedder;
    private readonly OllamaClient? _llm;
    private readonly ExtractionMode _mode;
    private readonly OnnxNerService? _nerService;
    private readonly EntityProfile _profile;
    private int _llmCallCount;

    // Structural patterns
    private static readonly Regex HeadingRx = new(@"^#{1,3}\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex InlineCodeRx = new(@"`([^`]{2,50})`", RegexOptions.Compiled);
    private static readonly Regex InternalLinkRx = new(@"\[([^\]]+)\]\(/blog/([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex TokenRx = new(@"\b[A-Za-z][A-Za-z0-9_\.#\+\-]*[A-Za-z0-9]\b", RegexOptions.Compiled);

    // Minimum thresholds - adjusted by profile
    private const int MaxCandidatesForEmbedding = 500;
    private const double MinEmbeddingSimilarity = 0.85;

    public ProfileAwareEntityExtractor(
        GraphRagDb db,
        EmbeddingService embedder,
        EntityProfile? profile = null,
        OllamaClient? llm = null,
        OnnxNerService? nerService = null,
        ExtractionMode mode = ExtractionMode.Heuristic)
    {
        _db = db;
        _embedder = embedder;
        _profile = profile ?? EntityTypeProfiles.Technical;
        _llm = llm;
        _nerService = nerService;
        _mode = mode;
    }

    public async Task<ExtractionResult> ExtractAsync(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _llmCallCount = 0;

        var chunks = await _db.GetAllChunksAsync();
        if (chunks.Count == 0)
            return new ExtractionResult { Mode = _mode };

        var candidates = new Dictionary<string, EntityCandidate>(StringComparer.OrdinalIgnoreCase);
        var coOccurrences = new Dictionary<(string, string), int>();
        var linkRelationships = new List<(string Source, string Target, string Type, string[] ChunkIds)>();

        // ═══════════════════════════════════════════════════════════════════
        // Phase 1: Build corpus IDF statistics (for heuristic backup)
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, 1, $"Extracting entities with {_profile.DisplayName} profile..."));
        var (termIdf, _) = ComputeIdfStats(chunks);

        // ═══════════════════════════════════════════════════════════════════
        // Phase 2: Extract candidates using ONNX NER or heuristics
        // ═══════════════════════════════════════════════════════════════════
        var useOnnxNer = _nerService != null;

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = chunks[i];

            List<EntityCandidate> chunkCandidates;

            if (useOnnxNer)
            {
                // Use ONNX NER for span detection + profile-based type mapping
                try
                {
                    chunkCandidates = await _nerService!.ExtractWithProfileAsync(chunk.Text, _profile, ct);
                }
                catch (Exception)
                {
                    // Fallback to heuristics if NER fails
                    chunkCandidates = ExtractFromChunkHeuristic(chunk.Text, termIdf);
                }
            }
            else
            {
                // Heuristic extraction
                chunkCandidates = ExtractFromChunkHeuristic(chunk.Text, termIdf);
            }

            // Merge candidates
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

            // Track co-occurrences
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

        // ═══════════════════════════════════════════════════════════════════
        // Phase 3: Filter by profile-specific thresholds
        // ═══════════════════════════════════════════════════════════════════
        var minMentions = _profile.MinMentionCount;
        var significant = candidates.Values
            .Where(c => c.MentionCount >= minMentions ||
                       (c.Confidence >= 0.8 && c.Signals.Count >= 2) ||
                       c.Signals.Contains("onnx_ner")) // ONNX NER results are pre-validated
            .OrderByDescending(c => c.MentionCount * c.Confidence)
            .Take(MaxCandidatesForEmbedding)
            .ToList();

        // ═══════════════════════════════════════════════════════════════════
        // Phase 4: Deduplicate using BERT embeddings
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, 1, "Deduplicating with BERT embeddings..."));
        var deduped = await DeduplicateWithEmbeddingsAsync(significant, ct);

        // ═══════════════════════════════════════════════════════════════════
        // Phase 5: Classify/normalize entity types using profile
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, 1, "Classifying entities..."));

        var llmAvailable = _llm != null && await _llm.IsAvailableAsync(ct);

        if (_mode == ExtractionMode.Llm && llmAvailable)
        {
            await ClassifyWithLlmAsync(deduped, ct);
        }
        else if (!useOnnxNer)
        {
            // Only classify by signals if we didn't use ONNX NER
            ClassifyByProfile(deduped);
        }

        // Normalize all types to profile canonical forms
        foreach (var e in deduped)
        {
            e.Type = EntityTypeDefinition.Normalize(e.Type, _profile);
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 6: Store entities and relationships
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, deduped.Count, "Storing entities..."));
        foreach (var e in deduped)
            await _db.UpsertEntityAsync(EntityId(e.Name), e.Name, e.Type, e.Description, e.ChunkIds.ToArray());

        progress?.Report(new ProgressInfo(0, 1, "Storing relationships..."));
        var linkRelCount = await StoreLinkRelationshipsAsync(linkRelationships, deduped);
        var coOccurCount = await StoreCoOccurrenceRelationshipsAsync(coOccurrences, deduped);

        sw.Stop();

        return new ExtractionResult
        {
            EntitiesExtracted = deduped.Count,
            RelationshipsExtracted = linkRelCount + coOccurCount,
            LlmCallCount = _llmCallCount,
            Duration = sw.Elapsed,
            Mode = useOnnxNer ? ExtractionMode.Hybrid : _mode
        };
    }

    /// <summary>
    /// Heuristic extraction using IDF + structural signals.
    /// </summary>
    private List<EntityCandidate> ExtractFromChunkHeuristic(string text, Dictionary<string, double> termIdf)
    {
        var candidates = new Dictionary<string, EntityCandidate>(StringComparer.OrdinalIgnoreCase);
        var minIdf = _profile.MinIdfThreshold;

        // Signal 1: Terms with high IDF
        foreach (Match m in TokenRx.Matches(text))
        {
            var term = m.Value;
            if (term.Length < 3) continue;

            if (termIdf.TryGetValue(term, out var idf) && idf >= minIdf)
            {
                var confidence = Math.Min(0.9, 0.4 + idf * 0.05);
                AddCandidate(candidates, term, "concept", confidence, "high_idf");
            }
        }

        // Signal 2: Headings (if relevant for profile)
        if (_profile.IsRelevantSignal("heading"))
        {
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
        }

        // Signal 3: Inline code (if relevant for profile)
        if (_profile.IsRelevantSignal("inline_code"))
        {
            foreach (Match m in InlineCodeRx.Matches(text))
            {
                var code = m.Groups[1].Value.Trim();
                if (IsIdentifier(code))
                    AddCandidate(candidates, code, "code", 0.9, "inline_code");
            }
        }

        // Signal 4: Link text
        if (_profile.IsRelevantSignal("link_text"))
        {
            foreach (Match m in InternalLinkRx.Matches(text))
            {
                var linkText = m.Groups[1].Value.Trim();
                if (linkText.Length >= 3 && linkText.Length <= 50)
                    AddCandidate(candidates, linkText, "concept", 0.75, "link_text");
            }
        }

        return candidates.Values.ToList();
    }

    private void ClassifyByProfile(List<EntityCandidate> entities)
    {
        foreach (var e in entities)
        {
            // Map signals to profile-appropriate types
            e.Type = e.Signals.FirstOrDefault() switch
            {
                "inline_code" => FindTypeInProfile("code", "technology"),
                "heading" => FindTypeInProfile("concept", "topic"),
                "high_idf" => FindTypeInProfile("concept", "technology"),
                "link_text" => FindTypeInProfile("concept"),
                _ => FindTypeInProfile("concept")
            };
        }
    }

    private string FindTypeInProfile(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = _profile.EntityTypes.FirstOrDefault(t =>
                t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match.Name;
        }
        return _profile.EntityTypes.FirstOrDefault()?.Name ?? "concept";
    }

    private async Task ClassifyWithLlmAsync(List<EntityCandidate> entities, CancellationToken ct)
    {
        const int batchSize = 50;
        var lookup = entities.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < entities.Count; i += batchSize)
        {
            var batch = entities.Skip(i).Take(batchSize).ToList();
            var list = string.Join("\n", batch.Select(e => $"- {e.Name}"));

            const string schema = """{"name":"...","type":"...","desc":"..."}""";
            var response = await _llm!.GenerateAsync($"""
                Classify these entities for a {_profile.DisplayName} context.
                Return JSONL only (one JSON object per line). No markdown, no commentary.

                Schema: {schema}
                Types: {_profile.TypeListForPrompt}

                Entities:
                {list}
                """, 0.2, ct);

            _llmCallCount++;

            // Parse JSONL response
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
                    // Ignore malformed JSON
                }
            }
        }
    }

    #region Helper Methods

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

    private async Task<List<EntityCandidate>> DeduplicateWithEmbeddingsAsync(List<EntityCandidate> candidates, CancellationToken ct)
    {
        if (candidates.Count <= 1) return candidates;

        var embeddings = await _embedder.EmbedBatchAsync(candidates.Select(c => c.Name), ct);

        var merged = new List<EntityCandidate>();
        var used = new HashSet<int>();

        for (int i = 0; i < candidates.Count; i++)
        {
            if (used.Contains(i)) continue;

            var canonical = candidates[i];
            used.Add(i);

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (used.Contains(j)) continue;

                var similarity = CosineSimilarity(embeddings[i], embeddings[j]);
                var stringSim = NormalizedLevenshtein(canonical.Name, candidates[j].Name);

                if (similarity >= MinEmbeddingSimilarity || stringSim >= 0.8)
                {
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
        foreach (Match m in InternalLinkRx.Matches(text))
        {
            var linkText = m.Groups[1].Value;
            var slug = m.Groups[2].Value;
            if (linkText.Length >= 3)
                yield return (linkText, $"blog:{slug}", "references", [chunkId]);
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

            string targetId;
            if (target.StartsWith("blog:"))
            {
                targetId = EntityId(target);
                await _db.UpsertEntityAsync(targetId, target, "document", null, chunkIds);
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

    #endregion
}
