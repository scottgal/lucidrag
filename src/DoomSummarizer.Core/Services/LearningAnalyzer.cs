using System.Diagnostics;
using DoomSummarizer.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DoomSummarizer.Services;

/// <summary>
///     Analyzes sentinel disagreements from the learning log to propose new exemplars.
///     Groups disagreements by (topic, type), clusters by embedding similarity,
///     picks representative queries closest to cluster centroids, and generates
///     exemplar proposals that can be validated against the test matrix.
/// </summary>
public class LearningAnalyzer
{
    private readonly StorageService _storage;
    private readonly int _minClusterSize;
    private readonly float _clusterSimilarityThreshold;

    public LearningAnalyzer(StorageService storage, int minClusterSize = 3,
        float clusterSimilarityThreshold = 0.7f)
    {
        _storage = storage;
        _minClusterSize = minClusterSize;
        _clusterSimilarityThreshold = clusterSimilarityThreshold;
    }

    /// <summary>
    ///     Analyze unpromoted disagreements and propose new exemplars.
    ///     Returns proposed exemplars grouped by the gap they fill.
    /// </summary>
    public async Task<LearningAnalysisResult> AnalyzeAsync(CancellationToken ct = default)
    {
        var entries = await _storage.GetUnpromotedDisagreementsAsync();
        if (entries.Count == 0)
            return new LearningAnalysisResult();

        var result = new LearningAnalysisResult
        {
            TotalDisagreements = entries.Count,
            AnalyzedAt = DateTimeOffset.UtcNow
        };

        // Group by (sentinel_topic, sentinel_type) — these represent the "correct" classification
        var groups = entries
            .GroupBy(e => (Topic: e.SentinelTopic ?? "unknown", Type: e.SentinelType))
            .Where(g => g.Count() >= _minClusterSize)
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in groups)
        {
            var gap = new ClassificationGap
            {
                SentinelTopic = group.Key.Topic,
                SentinelType = group.Key.Type,
                DisagreementCount = group.Count(),
                TopicDisagreeCount = group.Count(e => e.TopicDisagreement),
                TypeDisagreeCount = group.Count(e => e.TypeDisagreement),
                VibeDisagreeCount = group.Count(e => e.VibeDisagreement),
                AvgEmbeddingScore = group.Average(e => e.EmbBestScore)
            };

            // Cluster entries with embeddings by similarity
            var withEmbeddings = group.Where(e => e.QueryEmbedding != null).ToList();
            if (withEmbeddings.Count == 0)
            {
                // No embeddings — just pick the first entry as representative
                gap.ProposedExemplars.Add(BuildProposal(group.First()));
                gap.EntryIds.AddRange(group.Select(e => e.Id));
                result.Gaps.Add(gap);
                continue;
            }

            var clusters = ClusterByEmbedding(withEmbeddings);

            foreach (var cluster in clusters)
            {
                if (cluster.Count == 0) continue;

                // Pick query closest to centroid as representative
                var representative = FindCentroidRepresentative(cluster);
                gap.ProposedExemplars.Add(BuildProposal(representative));
            }

            gap.EntryIds.AddRange(group.Select(e => e.Id));
            result.Gaps.Add(gap);
        }

        // Also track small groups that didn't meet threshold
        var smallGroups = entries
            .GroupBy(e => (Topic: e.SentinelTopic ?? "unknown", Type: e.SentinelType))
            .Where(g => g.Count() < _minClusterSize && g.Count() > 0)
            .ToList();

        result.SmallGroupCount = smallGroups.Count;
        result.SmallGroupQueries = smallGroups
            .SelectMany(g => g.Select(e => e.QueryText))
            .Take(20)
            .ToList();

        return result;
    }

    /// <summary>
    ///     Serialize proposed exemplars to YAML format matching the ExemplarDocument schema.
    /// </summary>
    public static string ProposalsToYaml(List<ExemplarProposal> proposals)
    {
        var exemplars = proposals.Select(p => new QueryExemplar
        {
            Question = p.Question,
            Topic = p.Topic,
            Type = p.Type,
            Vibe = p.Vibe,
            Complexity = p.Complexity
        }).ToList();

        var doc = new ExemplarDocument { Exemplars = exemplars };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        return "# Auto-learned exemplars from sentinel disagreement analysis\n"
               + $"# Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n"
               + $"# Proposals: {proposals.Count}\n\n"
               + serializer.Serialize(doc);
    }

    /// <summary>
    ///     Validate proposed exemplars against the test matrix.
    ///     Returns (passed, failed, regressions) — regressions are previously-passing tests that now fail.
    /// </summary>
    public static async Task<ExemplarValidationResult> ValidateProposalsAsync(
        List<ExemplarProposal> proposals,
        Mostlylucid.DocSummarizer.Services.IEmbeddingService embedding,
        Func<List<(string Query, string? ExpectedTopic, string? ExpectedType, string? ExpectedVibe,
            bool? ExpectedComposite, bool? ExpectedComplex)>> buildTestMatrix,
        CancellationToken ct = default)
    {
        // Load current exemplars
        var currentExemplars = QueryClassifier.LoadAllExemplars();

        // Run baseline (current exemplars only)
        var baselineClassifier = new QueryClassifier();
        await baselineClassifier.InitializeAsync(embedding, ct);
        var testCases = buildTestMatrix();
        var baselineResults = await RunTestMatrixAsync(baselineClassifier, testCases, ct);

        // Add proposed exemplars
        var proposedExemplars = proposals.Select(p => new QueryExemplar
        {
            Question = p.Question,
            Topic = p.Topic,
            Type = p.Type,
            Vibe = p.Vibe,
            Complexity = p.Complexity
        }).ToList();

        currentExemplars.AddRange(proposedExemplars);

        // Run with proposals
        var proposalClassifier = new QueryClassifier();
        // We need to embed the combined set — use the same embedding service
        await proposalClassifier.InitializeWithExemplarsAsync(embedding, currentExemplars, ct);
        var proposalResults = await RunTestMatrixAsync(proposalClassifier, testCases, ct);

        // Compare results
        var result = new ExemplarValidationResult
        {
            BaselinePassed = baselineResults.Count(r => r.Passed),
            BaselineTotal = baselineResults.Count,
            ProposalPassed = proposalResults.Count(r => r.Passed),
            ProposalTotal = proposalResults.Count
        };

        for (var i = 0; i < testCases.Count; i++)
        {
            if (baselineResults[i].Passed && !proposalResults[i].Passed)
            {
                result.Regressions.Add(new TestRegression
                {
                    Query = testCases[i].Query,
                    Reason = proposalResults[i].FailReason ?? "unknown"
                });
            }
            else if (!baselineResults[i].Passed && proposalResults[i].Passed)
            {
                result.Improvements.Add(testCases[i].Query);
            }
        }

        return result;
    }

    private static async Task<List<TestResult>> RunTestMatrixAsync(
        QueryClassifier classifier,
        List<(string Query, string? ExpectedTopic, string? ExpectedType, string? ExpectedVibe,
            bool? ExpectedComposite, bool? ExpectedComplex)> testCases,
        CancellationToken ct)
    {
        var results = new List<TestResult>();

        foreach (var test in testCases)
        {
            var classification = await classifier.ClassifyAsync(test.Query, ct);
            var topTopics = classification.Categories
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .Select(kv => kv.Key)
                .ToList();

            var passed = true;
            var reasons = new List<string>();

            if (test.ExpectedTopic != null &&
                !topTopics.Contains(test.ExpectedTopic, StringComparer.OrdinalIgnoreCase))
            {
                passed = false;
                reasons.Add($"topic: expected={test.ExpectedTopic}, got=[{string.Join(",", topTopics)}]");
            }

            if (test.ExpectedType != null &&
                !classification.QueryType.Equals(test.ExpectedType, StringComparison.OrdinalIgnoreCase))
            {
                passed = false;
                reasons.Add($"type: expected={test.ExpectedType}, got={classification.QueryType}");
            }

            if (test.ExpectedVibe != null)
            {
                var expectNone = test.ExpectedVibe.Equals("none", StringComparison.OrdinalIgnoreCase);
                var vibeMatch = expectNone
                    ? classification.Vibe == null
                    : string.Equals(classification.Vibe, test.ExpectedVibe, StringComparison.OrdinalIgnoreCase);
                if (!vibeMatch)
                {
                    passed = false;
                    reasons.Add($"vibe: expected={test.ExpectedVibe}, got={classification.Vibe ?? "null"}");
                }
            }

            if (test.ExpectedComposite != null && classification.IsComposite != test.ExpectedComposite.Value)
            {
                passed = false;
                reasons.Add($"composite: expected={test.ExpectedComposite}, got={classification.IsComposite}");
            }

            if (test.ExpectedComplex != null && classification.IsComplex != test.ExpectedComplex.Value)
            {
                passed = false;
                reasons.Add($"complex: expected={test.ExpectedComplex}, got={classification.IsComplex}");
            }

            results.Add(new TestResult { Passed = passed, FailReason = string.Join("; ", reasons) });
        }

        return results;
    }

    private List<List<LearningLogEntry>> ClusterByEmbedding(List<LearningLogEntry> entries)
    {
        // Simple greedy clustering: assign each entry to the first cluster whose centroid
        // is above the similarity threshold, or create a new cluster.
        var clusters = new List<List<LearningLogEntry>>();
        var clusterCentroids = new List<float[]>();

        foreach (var entry in entries)
        {
            var assigned = false;
            for (var i = 0; i < clusters.Count; i++)
            {
                var sim = VectorMath.CosineSimilarity(entry.QueryEmbedding!, clusterCentroids[i]);
                if (sim >= _clusterSimilarityThreshold)
                {
                    clusters[i].Add(entry);
                    // Update centroid
                    clusterCentroids[i] = VectorMath.ComputeCentroid(
                        clusters[i].Select(e => e.QueryEmbedding!));
                    assigned = true;
                    break;
                }
            }

            if (!assigned)
            {
                clusters.Add([entry]);
                clusterCentroids.Add(entry.QueryEmbedding!);
            }
        }

        return clusters;
    }

    private static LearningLogEntry FindCentroidRepresentative(List<LearningLogEntry> cluster)
    {
        if (cluster.Count == 1) return cluster[0];

        var centroid = VectorMath.ComputeCentroid(cluster.Select(e => e.QueryEmbedding!));
        return cluster
            .OrderByDescending(e => VectorMath.CosineSimilarity(e.QueryEmbedding!, centroid))
            .First();
    }

    private static ExemplarProposal BuildProposal(LearningLogEntry entry)
    {
        return new ExemplarProposal
        {
            Question = entry.QueryText,
            Topic = entry.SentinelTopic ?? "unknown",
            Type = entry.SentinelType,
            Vibe = entry.SentinelVibe,
            Complexity = entry.EmbIsComplex ? "complex" : null,
            SourceEntryId = entry.Id,
            EmbeddingScore = entry.EmbBestScore
        };
    }

    private record TestResult
    {
        public bool Passed { get; init; }
        public string? FailReason { get; init; }
    }
}

/// <summary>
///     Result of analyzing the learning log for classification gaps.
/// </summary>
public class LearningAnalysisResult
{
    public int TotalDisagreements { get; init; }
    public DateTimeOffset AnalyzedAt { get; init; }
    public List<ClassificationGap> Gaps { get; init; } = [];
    public int SmallGroupCount { get; set; }
    public List<string> SmallGroupQueries { get; set; } = [];

    public int TotalProposals => Gaps.Sum(g => g.ProposedExemplars.Count);
}

/// <summary>
///     A gap in the classifier's coverage — a (topic, type) bucket where
///     the embedding classifier consistently disagrees with the sentinel.
/// </summary>
public class ClassificationGap
{
    public string SentinelTopic { get; init; } = "";
    public string SentinelType { get; init; } = "";
    public int DisagreementCount { get; init; }
    public int TopicDisagreeCount { get; init; }
    public int TypeDisagreeCount { get; init; }
    public int VibeDisagreeCount { get; init; }
    public double AvgEmbeddingScore { get; init; }
    public List<ExemplarProposal> ProposedExemplars { get; init; } = [];
    public List<long> EntryIds { get; init; } = [];
}

/// <summary>
///     A proposed exemplar derived from learning log analysis.
/// </summary>
public class ExemplarProposal
{
    public string Question { get; init; } = "";
    public string Topic { get; init; } = "";
    public string Type { get; init; } = "";
    public string? Vibe { get; init; }
    public string? Complexity { get; init; }
    public long SourceEntryId { get; init; }
    public double EmbeddingScore { get; init; }
}

/// <summary>
///     Result of validating proposed exemplars against the test matrix.
/// </summary>
public class ExemplarValidationResult
{
    public int BaselinePassed { get; init; }
    public int BaselineTotal { get; init; }
    public int ProposalPassed { get; init; }
    public int ProposalTotal { get; init; }
    public List<TestRegression> Regressions { get; init; } = [];
    public List<string> Improvements { get; init; } = [];

    public bool HasRegressions => Regressions.Count > 0;
    public int NetImprovement => Improvements.Count - Regressions.Count;
}

public class TestRegression
{
    public string Query { get; init; } = "";
    public string Reason { get; init; } = "";
}
