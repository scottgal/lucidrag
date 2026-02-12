using System.Diagnostics;
using System.Reflection;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DoomSummarizer.Services;

/// <summary>
///     Embedding-based deterministic query classifier.
///     Pre-embeds representative questions per topic/type at startup (single ONNX batch call),
///     then classifies incoming queries via cosine similarity — no LLM needed.
///     Uses IDF-weighted multi-match voting: max_sim + CountBoost * log2(count) * idf(label).
///     IDF (Inverse Document Frequency) automatically corrects for label frequency imbalance —
///     rare types like "howto" get stronger count boosts than dominant types like "roundup".
///     Exemplars are loaded from embedded defaults + user YAML files in ~/.doomsummarizer/exemplars/.
///     All thresholds are configurable via <see cref="ClassifierConfig" /> (config YAML: classifier section).
/// </summary>
public class QueryClassifier
{
    private readonly ClassifierConfig _config;

    /// <summary>Flat list of all exemplar embeddings (scored once per query).</summary>
    private List<ExemplarEmbedding>? _allExemplars;

    /// <summary>IDF weights per topic label — rare topics get higher weight in count boost.</summary>
    private Dictionary<string, double>? _topicIdf;

    /// <summary>IDF weights per type label — rare types (howto, comparison) get higher weight.</summary>
    private Dictionary<string, double>? _typeIdf;

    private IEmbeddingService? _embedding;

    public QueryClassifier() : this(new ClassifierConfig()) { }

    public QueryClassifier(ClassifierConfig config)
    {
        _config = config ?? new ClassifierConfig();
    }

    /// <summary>Whether the classifier has been initialized with embeddings.</summary>
    public bool IsInitialized => _allExemplars != null;

    /// <summary>Number of exemplars loaded.</summary>
    public int ExemplarCount => _allExemplars?.Count ?? 0;

    /// <summary>
    ///     Load all exemplar YAML files (embedded defaults + user overrides) and
    ///     batch-embed them in a single ONNX call.
    /// </summary>
    public async Task InitializeAsync(IEmbeddingService embedding, CancellationToken ct = default)
    {
        var exemplars = LoadAllExemplars();
        await InitializeWithExemplarsAsync(embedding, exemplars, ct);
    }

    /// <summary>
    ///     Initialize with a specific exemplar list (for testing with proposed exemplars).
    ///     Batch-embeds and builds the IDF-weighted voting tables.
    /// </summary>
    public async Task InitializeWithExemplarsAsync(IEmbeddingService embedding,
        List<QueryExemplar> exemplars, CancellationToken ct = default)
    {
        _embedding = embedding;

        if (exemplars.Count == 0)
        {
            Debug.WriteLine("QueryClassifier: no exemplars found");
            return;
        }

        // Batch-embed all exemplar questions in one ONNX call
        var questions = exemplars.Select(e => e.Question).ToList();
        var sw = Stopwatch.StartNew();
        var embeddings = await embedding.EmbedBatchAsync(questions, ct);
        sw.Stop();
        Debug.WriteLine($"QueryClassifier: embedded {questions.Count} exemplars in {sw.ElapsedMilliseconds}ms");

        // Build flat list — scored linearly per query (SIMD cosine sim is fast enough)
        _allExemplars = new List<ExemplarEmbedding>(exemplars.Count);
        for (var i = 0; i < exemplars.Count; i++)
            _allExemplars.Add(new ExemplarEmbedding(exemplars[i], embeddings[i]));

        // Compute IDF weights per label dimension (inverse document frequency)
        // idf(label) = log2(1 + total / count_for_label) — rare labels get higher weight
        _topicIdf = ComputeIdf(_allExemplars, e => e.Exemplar.Topic);
        _typeIdf = ComputeIdf(_allExemplars, e => e.Exemplar.Type);

        var topicCount = _allExemplars.Select(e => e.Exemplar.Topic).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var typeCount = _allExemplars.Select(e => e.Exemplar.Type).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Debug.WriteLine(
            $"QueryClassifier: {topicCount} topics, {typeCount} types, {_allExemplars.Count} total exemplars");

        // Log IDF weights for transparency
        foreach (var (type, idf) in _typeIdf.OrderByDescending(kv => kv.Value))
            Debug.WriteLine($"  IDF({type}) = {idf:F2}");
    }

    /// <summary>
    ///     Classify a query using inline IDF-weighted multi-match voting.
    ///     Single pass over all exemplars: scores, votes, vibe/composite/complex and top-5
    ///     are all accumulated in one loop. No intermediate list allocations.
    /// </summary>
    public async Task<QueryClassification> ClassifyAsync(string query, CancellationToken ct = default)
    {
        if (_allExemplars == null || _embedding == null)
            return new QueryClassification();

        // 0. Feature extraction (< 0.02ms, pre-compiled regexes)
        var features = QueryFeatures.Extract(query);
        var embeddingInput = _config.SynonymExpansionEnabled
            ? QueryFeatures.ExpandSynonyms(query, _config.ShortQueryMaxWords + 1)
            : query;
        var queryEmbedding = await _embedding.EmbedAsync(embeddingInput, ct);

        // 1. Single pass: score all exemplars + accumulate votes inline.
        //    Eliminates the `scored` list and separate WeightedVote iterations.
        //    Tracks: topic/type max+count, best match, vibe, composite top-2, complex, top-5.

        // Voting accumulators (inline — no second pass needed)
        var topicMax = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var topicCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var typeMax = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var typeCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Best/vibe/composite/complex trackers
        ExemplarEmbedding? bestExemplar = null;
        var bestScore = 0.0;
        ExemplarEmbedding? bestVibeExemplar = null;
        var bestVibeScore = 0.0;
        double compositeTop1 = 0, compositeTop2 = 0;
        var bestComplexScore = 0.0;
        var candidateCount = 0;

        // Top-5 bounded insert (avoid LINQ sort over full candidate list)
        var top5 = new (ExemplarEmbedding Exemplar, double Score)[5];
        var top5Count = 0;

        foreach (var exemplar in _allExemplars)
        {
            var sim = (double)VectorMath.CosineSimilarity(queryEmbedding, exemplar.Embedding);
            if (sim < _config.MinCandidateThreshold)
                continue;

            candidateCount++;
            var topic = exemplar.Exemplar.Topic;
            var type = exemplar.Exemplar.Type;

            // Inline topic voting: track max + count
            if (!topicMax.TryGetValue(topic, out var curTopicMax) || sim > curTopicMax)
                topicMax[topic] = sim;
            topicCount.TryGetValue(topic, out var tc);
            topicCount[topic] = tc + 1;

            // Inline type voting: track max + count
            if (!typeMax.TryGetValue(type, out var curTypeMax) || sim > curTypeMax)
                typeMax[type] = sim;
            typeCount.TryGetValue(type, out var tyc);
            typeCount[type] = tyc + 1;

            // Best overall
            if (sim > bestScore)
            {
                bestScore = sim;
                bestExemplar = exemplar;
            }

            // Vibe: best vibe-tagged match
            if (exemplar.Exemplar.Vibe != null && sim > bestVibeScore)
            {
                bestVibeScore = sim;
                bestVibeExemplar = exemplar;
            }

            // Composite: top-2 consensus
            if (type.Equals("composite", StringComparison.OrdinalIgnoreCase))
            {
                if (sim > compositeTop1) { compositeTop2 = compositeTop1; compositeTop1 = sim; }
                else if (sim > compositeTop2) { compositeTop2 = sim; }
            }

            // Complex: track best complex score (ratio checked post-loop)
            if (exemplar.Exemplar.Complexity == "complex" && sim > bestComplexScore)
                bestComplexScore = sim;

            // Top-5 bounded insert (insertion sort into small fixed array)
            if (top5Count < 5)
            {
                top5[top5Count++] = (exemplar, sim);
            }
            else if (sim > top5[4].Score)
            {
                top5[4] = (exemplar, sim);
                // Bubble up to maintain sorted order (at most 4 swaps)
                for (var j = 3; j >= 0 && top5[j + 1].Score > top5[j].Score; j--)
                    (top5[j], top5[j + 1]) = (top5[j + 1], top5[j]);
            }
        }

        if (candidateCount == 0)
            return new QueryClassification { QueryType = "roundup", QueryTypeConfidence = 0 };

        // 2. Compute IDF-weighted scores from accumulated max+count
        var filteredTopics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (topic, maxSim) in topicMax)
        {
            var idf = _topicIdf?.GetValueOrDefault(topic, 1.0) ?? 1.0;
            var score = maxSim + _config.CountBoost * Math.Log2(topicCount[topic]) * idf;
            if (score >= _config.MinTopicThreshold)
                filteredTopics[topic] = score;
        }

        var typeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, maxSim) in typeMax)
        {
            var idf = _typeIdf?.GetValueOrDefault(type, 1.0) ?? 1.0;
            typeScores[type] = maxSim + _config.CountBoost * Math.Log2(typeCount[type]) * idf;
        }

        // 3. Feature-based type adjustments for short queries
        var isShort = features.IsShortQuery(_config.ShortQueryMaxWords);
        if (isShort)
        {
            if (features.HasHowtoMarker)
            {
                typeScores.TryGetValue("howto", out var s);
                typeScores["howto"] = s + _config.HowtoFeatureBoost;
            }
            if (features.HasComparisonMarker)
            {
                typeScores.TryGetValue("comparison", out var s);
                typeScores["comparison"] = s + _config.ComparisonFeatureBoost;
            }
            if (features.HasQaMarker && !features.HasSearchOnlyMarker)
            {
                typeScores.TryGetValue("qa", out var s);
                typeScores["qa"] = s + _config.QaFeatureBoost;
            }
            if (!features.HasQuestionWord && !features.HasComparisonMarker
                && !features.HasHowtoMarker && !features.HasSearchOnlyMarker
                && !features.HasQaMarker)
            {
                typeScores.TryGetValue("roundup", out var s);
                typeScores["roundup"] = s + _config.DefaultRoundupBoost;
            }
        }

        // Search-only fast path (all query lengths)
        var forceSearchOnly = false;
        if (features.HasSearchOnlyMarker)
        {
            var topType = bestExemplar?.Exemplar.Type;
            if (bestExemplar == null || bestScore < 0.85
                || topType == "search_only" || topType == "qa")
                forceSearchOnly = true;
        }

        // 4. Best type (exclude composite — handled by IsComposite flag)
        var bestType = "roundup";
        var bestTypeScore = 0.0;
        foreach (var (type, score) in typeScores)
        {
            if (type.Equals("composite", StringComparison.OrdinalIgnoreCase)) continue;
            if (score > bestTypeScore && score >= _config.MinTypeThreshold)
            {
                bestTypeScore = score;
                bestType = type;
            }
        }

        if (forceSearchOnly)
        {
            bestType = "search_only";
            bestTypeScore = Math.Max(bestTypeScore, _config.SearchOnlyFeatureThreshold);
        }

        // 5. Vibe — require both absolute threshold AND proximity to best overall match.
        //    Prevents false vibes when a neutral query matches non-vibe exemplars much better.
        string? vibe = null;
        var vibeConfidence = 0.0;
        if (bestVibeExemplar != null && bestVibeScore > _config.VibeThreshold
            && bestVibeScore >= bestScore * _config.VibeMinRatio)
        {
            vibe = bestVibeExemplar.Exemplar.Vibe;
            vibeConfidence = bestVibeScore;
        }

        // 5b. Complex — same ratio guard as vibe. Prevents false escalation when a simple
        //     query shares topic semantic space with complex exemplars.
        var isComplex = bestComplexScore > _config.ComplexThreshold
                        && bestComplexScore >= bestScore * _config.ComplexMinRatio;

        // 6. Composite consensus (exemplar-based + multi-topic heuristic)
        var compositeThreshold = _config.CompositeRawThreshold;
        if (features.HasCompositeConjunction && features.WordCount >= 5)
            compositeThreshold *= 0.85;
        var isComposite = compositeTop2 > 0
                          && compositeTop1 > compositeThreshold
                          && compositeTop2 > compositeThreshold * 0.85;

        // Multi-topic heuristic: 2+ strong topics + punctuation separator → likely composite.
        // Catches "AI news; politics; ukraine" without needing composite exemplars.
        if (!isComposite && features.HasCompositeConjunction)
        {
            var strongTopicCount = filteredTopics.Count(kv => kv.Value >= 0.70);
            if (strongTopicCount >= 2)
                isComposite = true;
        }

        // 7. Build top matches from bounded top-5 array (already sorted by insertion)
        // Sort the filled portion (insertion sort may leave unsorted initial fills)
        var filled = top5Count;
        for (var i = 1; i < filled; i++)
            for (var j = i; j > 0 && top5[j].Score > top5[j - 1].Score; j--)
                (top5[j], top5[j - 1]) = (top5[j - 1], top5[j]);

        var topMatches = new List<ScoredExemplar>(filled);
        for (var i = 0; i < filled; i++)
        {
            var (ex, sc) = top5[i];
            topMatches.Add(new ScoredExemplar(ex.Exemplar.Question, ex.Exemplar.Topic, ex.Exemplar.Type, sc));
        }

        // 8. Short-query confidence scaling
        if (isShort)
            bestTypeScore *= _config.ShortQueryConfidenceScale;

        return new QueryClassification
        {
            Categories = filteredTopics,
            QueryType = bestType,
            QueryTypeConfidence = bestTypeScore,
            Vibe = vibe,
            VibeConfidence = vibeConfidence,
            IsComposite = isComposite,
            IsComplex = isComplex,
            SourceHints = bestExemplar?.Exemplar.Sources,
            BestMatch = bestExemplar?.Exemplar.Question,
            BestMatchScore = bestScore,
            TopMatches = topMatches,
            Features = isShort ? features : null
        };
    }

    /// <summary>
    ///     Compute IDF (Inverse Document Frequency) weights for each label value.
    ///     idf(label) = log2(1 + total / count_for_label)
    ///     Rare labels get higher weight, frequent labels get lower weight.
    /// </summary>
    private static Dictionary<string, double> ComputeIdf(
        List<ExemplarEmbedding> exemplars,
        Func<ExemplarEmbedding, string> labelSelector)
    {
        var total = (double)exemplars.Count;
        return exemplars
            .GroupBy(e => labelSelector(e), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => Math.Log2(1.0 + total / g.Count()),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Load exemplars from embedded defaults + user YAML files.
    ///     User files in ~/.doomsummarizer/exemplars/ are loaded after defaults.
    ///     If a user file contains exemplars with the same question text, they override the default.
    /// </summary>
    internal static List<QueryExemplar> LoadAllExemplars()
    {
        var exemplars = new List<QueryExemplar>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Load embedded defaults
        var embedded = LoadEmbeddedExemplars();
        foreach (var e in embedded)
        {
            if (seen.Add(e.Question))
                exemplars.Add(e);
        }

        // 2. Load user exemplars from ~/.doomsummarizer/exemplars/
        var userDir = Path.Combine(ConfigService.GetConfigDir(), "exemplars");
        if (Directory.Exists(userDir))
        {
            var userFiles = Directory.GetFiles(userDir, "*.yaml")
                .Concat(Directory.GetFiles(userDir, "*.yml"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (var file in userFiles)
            {
                try
                {
                    var userExemplars = LoadExemplarsFromFile(file);
                    foreach (var e in userExemplars)
                    {
                        if (seen.Add(e.Question))
                        {
                            exemplars.Add(e);
                        }
                        else
                        {
                            // Override: replace existing with user's version
                            var idx = exemplars.FindIndex(x =>
                                x.Question.Equals(e.Question, StringComparison.OrdinalIgnoreCase));
                            if (idx >= 0)
                                exemplars[idx] = e;
                        }
                    }

                    Debug.WriteLine($"QueryClassifier: loaded {userExemplars.Count} exemplars from {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"QueryClassifier: failed to load {file}: {ex.Message}");
                }
            }
        }

        return exemplars;
    }

    private static List<QueryExemplar> LoadEmbeddedExemplars()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("exemplars") && n.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var all = new List<QueryExemplar>();
        var deserializer = CreateDeserializer();

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            var yaml = reader.ReadToEnd();

            try
            {
                var doc = deserializer.Deserialize<ExemplarDocument>(yaml);
                if (doc?.Exemplars != null)
                    all.AddRange(doc.Exemplars);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"QueryClassifier: failed to parse embedded {resourceName}: {ex.Message}");
            }
        }

        return all;
    }

    internal static List<QueryExemplar> LoadExemplarsFromFile(string path)
    {
        var yaml = File.ReadAllText(path);
        var deserializer = CreateDeserializer();
        var doc = deserializer.Deserialize<ExemplarDocument>(yaml);
        return doc?.Exemplars ?? [];
    }

    private static IDeserializer CreateDeserializer()
    {
        return new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    private record ExemplarEmbedding(QueryExemplar Exemplar, float[] Embedding);
}
