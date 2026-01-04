using Microsoft.Extensions.Logging;

namespace Mostlylucid.RAG.Services;

/// <summary>
/// Multi-vector store that maintains multiple embedding types per document
/// Supports self-specializing search by tracking which vector type works best
/// </summary>
public class MultiVectorStore
{
    private readonly ILogger<MultiVectorStore>? _logger;
    private readonly Dictionary<string, DocumentVectors> _documents = new();
    private readonly Dictionary<VectorType, List<VectorEntry>> _vectorIndices = new();
    private readonly UsageTracker _usageTracker = new();
    private readonly object _lock = new();

    public MultiVectorStore(ILogger<MultiVectorStore>? logger = null)
    {
        _logger = logger;

        // Initialize indices for each vector type
        foreach (VectorType type in Enum.GetValues<VectorType>())
        {
            _vectorIndices[type] = new List<VectorEntry>();
        }
    }

    /// <summary>
    /// Store document with all available vector types
    /// </summary>
    public void StoreDocument(string documentId, MultiVectorDocument doc)
    {
        lock (_lock)
        {
            var vectors = new DocumentVectors
            {
                DocumentId = documentId,
                TextEmbedding = doc.TextEmbedding,
                ImageEmbedding = doc.ImageEmbedding,
                FaceEmbeddings = doc.FaceEmbeddings ?? new(),
                ColorHash = doc.ColorHash,
                PerceptualHash = doc.PerceptualHash,
                Metadata = doc.Metadata ?? new(),
                StoredAt = DateTime.UtcNow
            };

            _documents[documentId] = vectors;

            // Add to appropriate indices
            if (doc.TextEmbedding != null)
            {
                _vectorIndices[VectorType.Text].Add(new VectorEntry
                {
                    DocumentId = documentId,
                    Vector = doc.TextEmbedding
                });
            }

            if (doc.ImageEmbedding != null)
            {
                _vectorIndices[VectorType.Image].Add(new VectorEntry
                {
                    DocumentId = documentId,
                    Vector = doc.ImageEmbedding
                });
            }

            if (doc.FaceEmbeddings?.Any() == true)
            {
                foreach (var face in doc.FaceEmbeddings)
                {
                    _vectorIndices[VectorType.Face].Add(new VectorEntry
                    {
                        DocumentId = documentId,
                        Vector = face.Embedding
                    });
                }
            }

            if (!string.IsNullOrEmpty(doc.ColorHash))
            {
                _vectorIndices[VectorType.ColorHash].Add(new VectorEntry
                {
                    DocumentId = documentId,
                    Hash = doc.ColorHash
                });
            }

            if (!string.IsNullOrEmpty(doc.PerceptualHash))
            {
                _vectorIndices[VectorType.PerceptualHash].Add(new VectorEntry
                {
                    DocumentId = documentId,
                    Hash = doc.PerceptualHash
                });
            }

            _logger?.LogInformation(
                "Stored document {DocumentId} with {VectorTypes} vector types",
                documentId,
                GetAvailableVectorTypes(vectors).Count());
        }
    }

    /// <summary>
    /// Search using self-specializing logic
    /// Automatically selects best vector type based on query and past performance
    /// </summary>
    public MultiVectorSearchResult SearchAdaptive(
        MultiVectorQuery query,
        int topK = 10,
        double threshold = 0.7)
    {
        // Determine which vector types are available for this query
        var availableTypes = GetQueryVectorTypes(query);

        if (!availableTypes.Any())
        {
            _logger?.LogWarning("No vector types available for query");
            return new MultiVectorSearchResult { Results = new(), VectorTypeUsed = VectorType.None };
        }

        // Get recommended vector type based on past performance
        var recommendedType = _usageTracker.GetRecommendedVectorType(query, availableTypes);

        // Try recommended type first
        var results = SearchByVectorType(query, recommendedType, topK, threshold);

        // If results are poor, try other vector types
        if (results.Results.Count < topK / 2 || results.AverageScore < threshold)
        {
            _logger?.LogInformation(
                "Primary vector type {Type} yielded poor results, trying alternatives",
                recommendedType);

            foreach (var altType in availableTypes.Where(t => t != recommendedType))
            {
                var altResults = SearchByVectorType(query, altType, topK, threshold);

                if (altResults.AverageScore > results.AverageScore)
                {
                    results = altResults;
                    _logger?.LogInformation(
                        "Alternative vector type {Type} performed better (score: {Score:F3})",
                        altType,
                        altResults.AverageScore);
                }
            }
        }

        // Track usage for future optimization
        _usageTracker.RecordSearch(query, results.VectorTypeUsed, results.AverageScore, results.Results.Count);

        return results;
    }

    /// <summary>
    /// Search using specific vector type
    /// </summary>
    public MultiVectorSearchResult SearchByVectorType(
        MultiVectorQuery query,
        VectorType type,
        int topK = 10,
        double threshold = 0.7)
    {
        var results = new List<ScoredResult>();

        switch (type)
        {
            case VectorType.Text:
                if (query.TextEmbedding != null)
                {
                    results = SearchVectorIndex(VectorType.Text, query.TextEmbedding, topK, threshold);
                }
                break;

            case VectorType.Image:
                if (query.ImageEmbedding != null)
                {
                    results = SearchVectorIndex(VectorType.Image, query.ImageEmbedding, topK, threshold);
                }
                break;

            case VectorType.Face:
                if (query.FaceEmbedding != null)
                {
                    results = SearchVectorIndex(VectorType.Face, query.FaceEmbedding, topK, threshold);
                }
                break;

            case VectorType.ColorHash:
                if (!string.IsNullOrEmpty(query.ColorHash))
                {
                    results = SearchHashIndex(VectorType.ColorHash, query.ColorHash, topK);
                }
                break;

            case VectorType.PerceptualHash:
                if (!string.IsNullOrEmpty(query.PerceptualHash))
                {
                    results = SearchHashIndex(VectorType.PerceptualHash, query.PerceptualHash, topK);
                }
                break;
        }

        var avgScore = results.Any() ? results.Average(r => r.Score) : 0;

        return new MultiVectorSearchResult
        {
            Results = results,
            VectorTypeUsed = type,
            AverageScore = avgScore,
            TotalResults = results.Count
        };
    }

    /// <summary>
    /// Search vector index using cosine similarity
    /// </summary>
    private List<ScoredResult> SearchVectorIndex(
        VectorType type,
        float[] queryVector,
        int topK,
        double threshold)
    {
        lock (_lock)
        {
            if (!_vectorIndices.TryGetValue(type, out var index))
            {
                return new List<ScoredResult>();
            }

            var scored = new List<(string DocumentId, double Score)>();

            foreach (var entry in index)
            {
                if (entry.Vector == null) continue;

                var similarity = CosineSimilarity(queryVector, entry.Vector);

                if (similarity >= threshold)
                {
                    scored.Add((entry.DocumentId, similarity));
                }
            }

            return scored
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .Select(x => new ScoredResult
                {
                    DocumentId = x.DocumentId,
                    Score = x.Score,
                    Metadata = _documents.TryGetValue(x.DocumentId, out var doc) ? doc.Metadata : new()
                })
                .ToList();
        }
    }

    /// <summary>
    /// Search hash index (exact or fuzzy match)
    /// </summary>
    private List<ScoredResult> SearchHashIndex(
        VectorType type,
        string queryHash,
        int topK)
    {
        lock (_lock)
        {
            if (!_vectorIndices.TryGetValue(type, out var index))
            {
                return new List<ScoredResult>();
            }

            var scored = new List<(string DocumentId, double Score)>();

            foreach (var entry in index)
            {
                if (string.IsNullOrEmpty(entry.Hash)) continue;

                // Exact match
                if (entry.Hash == queryHash)
                {
                    scored.Add((entry.DocumentId, 1.0));
                }
                // Fuzzy match (Hamming distance for perceptual hashes)
                else if (type == VectorType.PerceptualHash)
                {
                    var similarity = 1.0 - (HammingDistance(queryHash, entry.Hash) / (double)Math.Min(queryHash.Length, entry.Hash.Length));
                    if (similarity >= 0.85) // 85% similarity threshold
                    {
                        scored.Add((entry.DocumentId, similarity));
                    }
                }
            }

            return scored
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .Select(x => new ScoredResult
                {
                    DocumentId = x.DocumentId,
                    Score = x.Score,
                    Metadata = _documents.TryGetValue(x.DocumentId, out var doc) ? doc.Metadata : new()
                })
                .ToList();
        }
    }

    /// <summary>
    /// Cosine similarity between two vectors
    /// </summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dot = 0, magA = 0, magB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var denominator = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denominator > 0 ? dot / denominator : 0;
    }

    /// <summary>
    /// Hamming distance between two hash strings
    /// </summary>
    private static int HammingDistance(string a, string b)
    {
        int distance = 0;
        int len = Math.Min(a.Length, b.Length);

        for (int i = 0; i < len; i++)
        {
            if (a[i] != b[i]) distance++;
        }

        // Add difference in length
        distance += Math.Abs(a.Length - b.Length);

        return distance;
    }

    private IEnumerable<VectorType> GetQueryVectorTypes(MultiVectorQuery query)
    {
        var types = new List<VectorType>();

        if (query.TextEmbedding != null) types.Add(VectorType.Text);
        if (query.ImageEmbedding != null) types.Add(VectorType.Image);
        if (query.FaceEmbedding != null) types.Add(VectorType.Face);
        if (!string.IsNullOrEmpty(query.ColorHash)) types.Add(VectorType.ColorHash);
        if (!string.IsNullOrEmpty(query.PerceptualHash)) types.Add(VectorType.PerceptualHash);

        return types;
    }

    private IEnumerable<VectorType> GetAvailableVectorTypes(DocumentVectors doc)
    {
        var types = new List<VectorType>();

        if (doc.TextEmbedding != null) types.Add(VectorType.Text);
        if (doc.ImageEmbedding != null) types.Add(VectorType.Image);
        if (doc.FaceEmbeddings.Any()) types.Add(VectorType.Face);
        if (!string.IsNullOrEmpty(doc.ColorHash)) types.Add(VectorType.ColorHash);
        if (!string.IsNullOrEmpty(doc.PerceptualHash)) types.Add(VectorType.PerceptualHash);

        return types;
    }

    /// <summary>
    /// Get usage statistics for self-specializing analysis
    /// </summary>
    public UsageStatistics GetStatistics()
    {
        return _usageTracker.GetStatistics();
    }
}

/// <summary>
/// Vector type enumeration
/// </summary>
public enum VectorType
{
    None,
    Text,          // OCR text embeddings
    Image,         // CLIP image embeddings
    Face,          // Face embeddings (PII-respecting)
    ColorHash,     // Color signature hashes
    PerceptualHash // Perceptual image hashes
}

/// <summary>
/// Document with multiple vector representations
/// </summary>
public class MultiVectorDocument
{
    public float[]? TextEmbedding { get; set; }
    public float[]? ImageEmbedding { get; set; }
    public List<FaceEmbeddingEntry>? FaceEmbeddings { get; set; }
    public string? ColorHash { get; set; }
    public string? PerceptualHash { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Query with multiple vector types
/// </summary>
public class MultiVectorQuery
{
    public float[]? TextEmbedding { get; set; }
    public float[]? ImageEmbedding { get; set; }
    public float[]? FaceEmbedding { get; set; }
    public string? ColorHash { get; set; }
    public string? PerceptualHash { get; set; }
    public string? QueryIntent { get; set; } // For usage tracking
}

/// <summary>
/// Search result with vector type used
/// </summary>
public class MultiVectorSearchResult
{
    public List<ScoredResult> Results { get; set; } = new();
    public VectorType VectorTypeUsed { get; set; }
    public double AverageScore { get; set; }
    public int TotalResults { get; set; }
}

public class ScoredResult
{
    public string DocumentId { get; set; } = string.Empty;
    public double Score { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

internal class DocumentVectors
{
    public required string DocumentId { get; set; }
    public float[]? TextEmbedding { get; set; }
    public float[]? ImageEmbedding { get; set; }
    public List<FaceEmbeddingEntry> FaceEmbeddings { get; set; } = new();
    public string? ColorHash { get; set; }
    public string? PerceptualHash { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public DateTime StoredAt { get; set; }
}

public class FaceEmbeddingEntry
{
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public string? EmbeddingHash { get; set; }
}

internal class VectorEntry
{
    public required string DocumentId { get; set; }
    public float[]? Vector { get; set; }
    public string? Hash { get; set; }
}

/// <summary>
/// Tracks usage patterns for self-specializing RAG
/// </summary>
internal class UsageTracker
{
    private readonly Dictionary<string, List<UsageEntry>> _usageByIntent = new();
    private readonly Dictionary<VectorType, PerformanceMetrics> _performanceByType = new();
    private readonly object _lock = new();

    public void RecordSearch(MultiVectorQuery query, VectorType typeUsed, double avgScore, int resultCount)
    {
        lock (_lock)
        {
            var intent = query.QueryIntent ?? "unknown";

            if (!_usageByIntent.ContainsKey(intent))
            {
                _usageByIntent[intent] = new List<UsageEntry>();
            }

            _usageByIntent[intent].Add(new UsageEntry
            {
                VectorType = typeUsed,
                Score = avgScore,
                ResultCount = resultCount,
                Timestamp = DateTime.UtcNow
            });

            // Update performance metrics
            if (!_performanceByType.ContainsKey(typeUsed))
            {
                _performanceByType[typeUsed] = new PerformanceMetrics();
            }

            var metrics = _performanceByType[typeUsed];
            metrics.TotalSearches++;
            metrics.TotalScore += avgScore;
            metrics.AverageScore = metrics.TotalScore / metrics.TotalSearches;

            if (resultCount > 0)
            {
                metrics.SuccessfulSearches++;
            }

            metrics.SuccessRate = metrics.SuccessfulSearches / (double)metrics.TotalSearches;
        }
    }

    public VectorType GetRecommendedVectorType(MultiVectorQuery query, IEnumerable<VectorType> availableTypes)
    {
        lock (_lock)
        {
            var intent = query.QueryIntent ?? "unknown";

            // If we have history for this intent, use it
            if (_usageByIntent.TryGetValue(intent, out var history) && history.Any())
            {
                // Get best performing vector type for this intent
                var bestForIntent = history
                    .GroupBy(e => e.VectorType)
                    .Select(g => new
                    {
                        Type = g.Key,
                        AvgScore = g.Average(e => e.Score),
                        SuccessRate = g.Count(e => e.ResultCount > 0) / (double)g.Count()
                    })
                    .Where(x => availableTypes.Contains(x.Type))
                    .OrderByDescending(x => x.AvgScore * x.SuccessRate)
                    .FirstOrDefault();

                if (bestForIntent != null)
                {
                    return bestForIntent.Type;
                }
            }

            // Fall back to global best performer
            var globalBest = _performanceByType
                .Where(kvp => availableTypes.Contains(kvp.Key))
                .OrderByDescending(kvp => kvp.Value.AverageScore * kvp.Value.SuccessRate)
                .FirstOrDefault();

            if (globalBest.Key != VectorType.None)
            {
                return globalBest.Key;
            }

            // Default fallback priority: Image > Text > Face > ColorHash > PerceptualHash
            var fallbackPriority = new[] {
                VectorType.Image,
                VectorType.Text,
                VectorType.Face,
                VectorType.ColorHash,
                VectorType.PerceptualHash
            };

            return fallbackPriority.FirstOrDefault(t => availableTypes.Contains(t), VectorType.None);
        }
    }

    public UsageStatistics GetStatistics()
    {
        lock (_lock)
        {
            return new UsageStatistics
            {
                PerformanceByType = new Dictionary<VectorType, PerformanceMetrics>(_performanceByType),
                TotalSearches = _performanceByType.Values.Sum(m => m.TotalSearches),
                IntentCount = _usageByIntent.Count
            };
        }
    }
}

internal class UsageEntry
{
    public VectorType VectorType { get; set; }
    public double Score { get; set; }
    public int ResultCount { get; set; }
    public DateTime Timestamp { get; set; }
}

public class PerformanceMetrics
{
    public int TotalSearches { get; set; }
    public int SuccessfulSearches { get; set; }
    public double TotalScore { get; set; }
    public double AverageScore { get; set; }
    public double SuccessRate { get; set; }
}

public class UsageStatistics
{
    public Dictionary<VectorType, PerformanceMetrics> PerformanceByType { get; set; } = new();
    public int TotalSearches { get; set; }
    public int IntentCount { get; set; }
}
