using System.Collections.Concurrent;
using System.Text.Json;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services;

/// <summary>
/// Learns and stores salience patterns based on image fingerprints AND multi-vector embeddings.
/// Uses both deterministic hashes (PDQ, block, color) and vector similarity (CLIP visual, color, motion)
/// to identify similar images and accumulate which signals proved most useful.
/// This is the "adaptive" pipeline - starts with defaults and learns from feedback.
/// </summary>
public class SalienceLearner
{
    // In-memory cache of fingerprint -> signal weights
    // In production, this would be backed by a persistent store
    private readonly ConcurrentDictionary<string, SalienceProfile> _profileCache = new();

    // Multi-vector embeddings for similarity search
    // Keys are image IDs, values contain embeddings for different modalities
    private readonly ConcurrentDictionary<string, ImageEmbeddingSet> _embeddingCache = new();

    // Default profiles by image type (learned or preset)
    private readonly Dictionary<string, SalienceProfile> _typeProfiles = new()
    {
        ["photo"] = new SalienceProfile
        {
            ImageType = "photo",
            Weights = new Dictionary<string, double>
            {
                ["subjects"] = 1.0,
                ["entities"] = 0.9,
                ["scene"] = 0.8,
                ["colors"] = 0.3,
                ["motion"] = 0.0,
                ["text"] = 0.2,
                ["quality"] = 0.2,
            }
        },
        ["screenshot"] = new SalienceProfile
        {
            ImageType = "screenshot",
            Weights = new Dictionary<string, double>
            {
                ["subjects"] = 0.3,
                ["entities"] = 0.4,
                ["scene"] = 0.2,
                ["colors"] = 0.1,
                ["motion"] = 0.0,
                ["text"] = 1.0,  // Text is most important for screenshots
                ["quality"] = 0.1,
            }
        },
        ["meme"] = new SalienceProfile
        {
            ImageType = "meme",
            Weights = new Dictionary<string, double>
            {
                ["subjects"] = 0.7,
                ["entities"] = 0.6,
                ["scene"] = 0.3,
                ["colors"] = 0.2,
                ["motion"] = 0.5,
                ["text"] = 1.0,  // Text is critical for memes
                ["quality"] = 0.1,
            }
        },
        ["animated"] = new SalienceProfile
        {
            ImageType = "animated",
            Weights = new Dictionary<string, double>
            {
                ["subjects"] = 0.9,
                ["entities"] = 0.8,
                ["scene"] = 0.5,
                ["colors"] = 0.2,
                ["motion"] = 1.0,  // Motion is critical for animations
                ["text"] = 0.7,
                ["quality"] = 0.1,
            }
        },
        ["diagram"] = new SalienceProfile
        {
            ImageType = "diagram",
            Weights = new Dictionary<string, double>
            {
                ["subjects"] = 0.3,
                ["entities"] = 0.5,
                ["scene"] = 0.1,
                ["colors"] = 0.4,
                ["motion"] = 0.0,
                ["text"] = 1.0,
                ["quality"] = 0.3,
            }
        }
    };

    /// <summary>
    /// Get salience weights for an image based on its fingerprints and type.
    /// Uses fingerprint similarity to find learned patterns, falls back to type-based defaults.
    /// </summary>
    public Dictionary<string, double> GetWeights(DynamicImageProfile profile, string purpose = "caption")
    {
        // Try to find by exact fingerprint match first
        var pdqHash = profile.GetValue<string>("fingerprint.pdq_hash");
        var blockHash = profile.GetValue<string>("fingerprint.block_hash");
        var colorHash = profile.GetValue<string>("fingerprint.color_hash");

        // Check for exact PDQ match (most specific)
        if (!string.IsNullOrEmpty(pdqHash) && _profileCache.TryGetValue($"pdq:{pdqHash}", out var pdqProfile))
        {
            return MergeWithPurpose(pdqProfile.Weights, purpose);
        }

        // Check for similar block hash (perceptual similarity)
        if (!string.IsNullOrEmpty(blockHash) && _profileCache.TryGetValue($"block:{blockHash}", out var blockProfile))
        {
            return MergeWithPurpose(blockProfile.Weights, purpose);
        }

        // Fall back to type-based profile
        var imageType = DetectImageType(profile);
        if (_typeProfiles.TryGetValue(imageType, out var typeProfile))
        {
            return MergeWithPurpose(typeProfile.Weights, purpose);
        }

        // Default weights
        return GetDefaultWeights(purpose);
    }

    /// <summary>
    /// Record that certain signals were useful for an image (for learning).
    /// Call this with feedback about which signals helped produce good output.
    /// </summary>
    public void RecordFeedback(DynamicImageProfile profile, Dictionary<string, double> usefulSignals)
    {
        var pdqHash = profile.GetValue<string>("fingerprint.pdq_hash");
        if (string.IsNullOrEmpty(pdqHash))
            return;

        var key = $"pdq:{pdqHash}";

        _profileCache.AddOrUpdate(
            key,
            _ => new SalienceProfile
            {
                ImageType = DetectImageType(profile),
                Weights = usefulSignals,
                SampleCount = 1
            },
            (_, existing) =>
            {
                // Exponential moving average to blend new feedback with existing
                var alpha = 0.3; // Learning rate
                foreach (var (signal, weight) in usefulSignals)
                {
                    if (existing.Weights.TryGetValue(signal, out var oldWeight))
                    {
                        existing.Weights[signal] = oldWeight * (1 - alpha) + weight * alpha;
                    }
                    else
                    {
                        existing.Weights[signal] = weight;
                    }
                }
                existing.SampleCount++;
                return existing;
            });
    }

    /// <summary>
    /// Find similar images by fingerprint and return their salience profiles.
    /// </summary>
    public IEnumerable<(string Hash, SalienceProfile Profile, double Similarity)> FindSimilar(
        DynamicImageProfile profile,
        int maxResults = 5)
    {
        var pdqHash = profile.GetValue<string>("fingerprint.pdq_hash");
        if (string.IsNullOrEmpty(pdqHash))
            yield break;

        // Find profiles with similar PDQ hashes (hamming distance)
        foreach (var (key, cachedProfile) in _profileCache)
        {
            if (!key.StartsWith("pdq:"))
                continue;

            var cachedPdq = key.Substring(4);
            var similarity = CalculatePdqSimilarity(pdqHash, cachedPdq);

            if (similarity > 0.7) // 70% similarity threshold
            {
                yield return (cachedPdq, cachedProfile, similarity);
            }
        }
    }

    /// <summary>
    /// Find similar images using multi-vector embeddings.
    /// Combines visual, color, and motion similarity with configurable weights.
    /// </summary>
    public IEnumerable<(string Id, SalienceProfile Profile, double Similarity)> FindSimilarByEmbeddings(
        ImageEmbeddingSet queryEmbeddings,
        int maxResults = 5,
        double threshold = 0.6)
    {
        var results = new List<(string Id, SalienceProfile Profile, double Similarity)>();

        foreach (var (id, cachedEmbeddings) in _embeddingCache)
        {
            if (!_profileCache.TryGetValue(id, out var profile))
                continue;

            var similarity = CalculateMultiVectorSimilarity(queryEmbeddings, cachedEmbeddings);
            if (similarity >= threshold)
            {
                results.Add((id, profile, similarity));
            }
        }

        return results.OrderByDescending(r => r.Similarity).Take(maxResults);
    }

    /// <summary>
    /// Get weights using multi-vector similarity search.
    /// Falls back to hash-based matching, then type-based defaults.
    /// </summary>
    public Dictionary<string, double> GetWeightsAdaptive(
        DynamicImageProfile profile,
        ImageEmbeddingSet? embeddings,
        string purpose = "caption")
    {
        // First try exact hash match (fastest)
        var pdqHash = profile.GetValue<string>("fingerprint.pdq_hash");
        if (!string.IsNullOrEmpty(pdqHash) && _profileCache.TryGetValue($"pdq:{pdqHash}", out var exactMatch))
        {
            return MergeWithPurpose(exactMatch.Weights, purpose);
        }

        // Try multi-vector similarity if embeddings available
        if (embeddings != null && (embeddings.VisualEmbedding != null || embeddings.ColorEmbedding != null))
        {
            var similar = FindSimilarByEmbeddings(embeddings, maxResults: 3, threshold: 0.7).ToList();
            if (similar.Any())
            {
                // Blend weights from similar images, weighted by similarity
                var blendedWeights = BlendWeights(similar);
                return MergeWithPurpose(blendedWeights, purpose);
            }
        }

        // Fall back to standard hash + type matching
        return GetWeights(profile, purpose);
    }

    /// <summary>
    /// Record feedback with embeddings for future similarity matching.
    /// </summary>
    public void RecordFeedbackWithEmbeddings(
        DynamicImageProfile profile,
        ImageEmbeddingSet embeddings,
        Dictionary<string, double> usefulSignals)
    {
        // Record standard feedback
        RecordFeedback(profile, usefulSignals);

        // Store embeddings for future similarity search
        var id = profile.GetValue<string>("identity.sha256")
                 ?? profile.GetValue<string>("fingerprint.composite")
                 ?? Guid.NewGuid().ToString();

        _embeddingCache.AddOrUpdate(id, embeddings, (_, _) => embeddings);
    }

    /// <summary>
    /// Get statistics about learned profiles.
    /// </summary>
    public LearnerStatistics GetStatistics()
    {
        return new LearnerStatistics
        {
            TotalProfiles = _profileCache.Count,
            TotalEmbeddings = _embeddingCache.Count,
            ProfilesByType = _profileCache.Values
                .Where(p => p.ImageType != null)
                .GroupBy(p => p.ImageType!)
                .ToDictionary(g => g.Key, g => g.Count()),
            TotalSamples = _profileCache.Values.Sum(p => p.SampleCount),
            AverageWeightsPerProfile = _profileCache.Values.Any()
                ? _profileCache.Values.Average(p => p.Weights.Count)
                : 0
        };
    }

    /// <summary>
    /// Save learned data to disk for persistence.
    /// </summary>
    public async Task SaveAsync(string filePath)
    {
        var data = new LearnedData
        {
            Profiles = _profileCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Embeddings = _embeddingCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            SavedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(filePath, json);
    }

    /// <summary>
    /// Load learned data from disk.
    /// </summary>
    public async Task LoadAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var data = JsonSerializer.Deserialize<LearnedData>(json);

            if (data?.Profiles != null)
            {
                foreach (var (key, profile) in data.Profiles)
                {
                    _profileCache.TryAdd(key, profile);
                }
            }

            if (data?.Embeddings != null)
            {
                foreach (var (key, embedding) in data.Embeddings)
                {
                    _embeddingCache.TryAdd(key, embedding);
                }
            }
        }
        catch
        {
            // If loading fails, start fresh
        }
    }

    /// <summary>
    /// Clear all learned data.
    /// </summary>
    public void Clear()
    {
        _profileCache.Clear();
        _embeddingCache.Clear();
    }

    /// <summary>
    /// Blend weights from multiple similar images based on similarity scores.
    /// Uses weighted average where weight = similarity^2 (emphasizes closer matches).
    /// </summary>
    private Dictionary<string, double> BlendWeights(
        List<(string Id, SalienceProfile Profile, double Similarity)> similar)
    {
        var blended = new Dictionary<string, double>();
        var totalWeight = 0.0;

        foreach (var (_, profile, similarity) in similar)
        {
            // Square similarity to emphasize closer matches
            var weight = similarity * similarity;
            totalWeight += weight;

            foreach (var (signal, value) in profile.Weights)
            {
                if (blended.TryGetValue(signal, out var existing))
                {
                    blended[signal] = existing + value * weight;
                }
                else
                {
                    blended[signal] = value * weight;
                }
            }
        }

        // Normalize
        if (totalWeight > 0)
        {
            foreach (var key in blended.Keys.ToList())
            {
                blended[key] /= totalWeight;
            }
        }

        return blended;
    }

    /// <summary>
    /// Calculate multi-vector similarity using weighted combination of modalities.
    /// </summary>
    private double CalculateMultiVectorSimilarity(ImageEmbeddingSet a, ImageEmbeddingSet b)
    {
        var similarities = new List<(double Weight, double Similarity)>();

        // Visual embedding (CLIP) - most important
        if (a.VisualEmbedding != null && b.VisualEmbedding != null)
        {
            var sim = CosineSimilarity(a.VisualEmbedding, b.VisualEmbedding);
            similarities.Add((0.5, sim)); // 50% weight
        }

        // Color embedding
        if (a.ColorEmbedding != null && b.ColorEmbedding != null)
        {
            var sim = CosineSimilarity(a.ColorEmbedding, b.ColorEmbedding);
            similarities.Add((0.25, sim)); // 25% weight
        }

        // Motion embedding
        if (a.MotionEmbedding != null && b.MotionEmbedding != null)
        {
            var sim = CosineSimilarity(a.MotionEmbedding, b.MotionEmbedding);
            similarities.Add((0.25, sim)); // 25% weight
        }

        if (!similarities.Any())
            return 0;

        // Weighted average, normalized
        var totalWeight = similarities.Sum(s => s.Weight);
        return similarities.Sum(s => s.Weight * s.Similarity) / totalWeight;
    }

    /// <summary>
    /// Cosine similarity between two vectors.
    /// </summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0;

        double dotProduct = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator > 0 ? dotProduct / denominator : 0;
    }

    /// <summary>
    /// Detect image type from profile signals.
    /// </summary>
    private string DetectImageType(DynamicImageProfile profile)
    {
        var isAnimated = profile.GetValue<bool>("identity.is_animated");
        if (isAnimated)
            return "animated";

        var detectedType = profile.GetValue<string>("content.type")?.ToLowerInvariant();
        if (!string.IsNullOrEmpty(detectedType))
        {
            return detectedType switch
            {
                "screenshot" => "screenshot",
                "diagram" or "chart" => "diagram",
                "meme" => "meme",
                "photo" or "photograph" => "photo",
                _ => "photo" // Default
            };
        }

        // Heuristics
        var textLikeliness = profile.GetValue<double>("content.text_likeliness");
        if (textLikeliness > 0.7)
            return "screenshot";

        return "photo";
    }

    /// <summary>
    /// Merge learned weights with purpose-specific adjustments.
    /// </summary>
    private Dictionary<string, double> MergeWithPurpose(Dictionary<string, double> baseWeights, string purpose)
    {
        var purposeMultipliers = purpose.ToLowerInvariant() switch
        {
            "alttext" => new Dictionary<string, double>
            {
                ["subjects"] = 1.2,
                ["motion"] = 1.1,
                ["text"] = 1.0,
                ["colors"] = 0.3,
                ["quality"] = 0.0,
                ["identity"] = 0.0,
            },
            "verbose" => new Dictionary<string, double>
            {
                ["subjects"] = 1.0,
                ["motion"] = 1.0,
                ["text"] = 1.0,
                ["colors"] = 1.0,
                ["quality"] = 1.0,
                ["identity"] = 1.0,
            },
            "technical" => new Dictionary<string, double>
            {
                ["subjects"] = 0.5,
                ["motion"] = 0.8,
                ["text"] = 1.0,
                ["colors"] = 1.2,
                ["quality"] = 1.5,
                ["identity"] = 1.5,
            },
            _ => new Dictionary<string, double>() // No adjustment
        };

        var result = new Dictionary<string, double>(baseWeights);
        foreach (var (signal, multiplier) in purposeMultipliers)
        {
            if (result.TryGetValue(signal, out var weight))
            {
                result[signal] = weight * multiplier;
            }
        }

        return result;
    }

    /// <summary>
    /// Default weights when no profile found.
    /// </summary>
    private Dictionary<string, double> GetDefaultWeights(string purpose)
    {
        return purpose.ToLowerInvariant() switch
        {
            "alttext" => new Dictionary<string, double>
            {
                ["subjects"] = 1.0,
                ["entities"] = 0.9,
                ["motion"] = 0.85,
                ["text"] = 0.7,
                ["scene"] = 0.5,
                ["colors"] = 0.1,
                ["quality"] = 0.0,
                ["identity"] = 0.0,
            },
            "verbose" => new Dictionary<string, double>
            {
                ["subjects"] = 1.0,
                ["entities"] = 0.9,
                ["motion"] = 0.85,
                ["text"] = 0.8,
                ["scene"] = 0.75,
                ["colors"] = 0.6,
                ["quality"] = 0.5,
                ["identity"] = 0.7,
            },
            _ => new Dictionary<string, double>
            {
                ["subjects"] = 0.9,
                ["entities"] = 0.8,
                ["motion"] = 0.8,
                ["text"] = 0.7,
                ["scene"] = 0.6,
                ["colors"] = 0.4,
                ["quality"] = 0.3,
                ["identity"] = 0.3,
            }
        };
    }

    /// <summary>
    /// Calculate similarity between two PDQ hashes (normalized hamming distance).
    /// </summary>
    private double CalculatePdqSimilarity(string hash1, string hash2)
    {
        if (hash1.Length != hash2.Length)
            return 0;

        try
        {
            // PDQ hash is 64 hex chars = 256 bits
            var bytes1 = Convert.FromHexString(hash1);
            var bytes2 = Convert.FromHexString(hash2);

            var hammingDistance = 0;
            for (var i = 0; i < bytes1.Length; i++)
            {
                var xor = (byte)(bytes1[i] ^ bytes2[i]);
                hammingDistance += BitCount(xor);
            }

            // Convert to similarity (0-1)
            var maxDistance = bytes1.Length * 8;
            return 1.0 - (double)hammingDistance / maxDistance;
        }
        catch
        {
            return 0;
        }
    }

    private static int BitCount(byte b)
    {
        var count = 0;
        while (b != 0)
        {
            count += b & 1;
            b >>= 1;
        }
        return count;
    }
}

/// <summary>
/// Stored salience profile for an image or image type.
/// </summary>
public class SalienceProfile
{
    public string? ImageType { get; set; }
    public Dictionary<string, double> Weights { get; set; } = new();
    public int SampleCount { get; set; } = 0;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Multi-vector embedding set for an image.
/// Used for similarity search across different modalities.
/// </summary>
public class ImageEmbeddingSet
{
    /// <summary>
    /// Image identifier (SHA256 or composite fingerprint)
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// CLIP visual embedding (typically 512 or 768 dimensions)
    /// </summary>
    public float[]? VisualEmbedding { get; set; }

    /// <summary>
    /// Color palette embedding (compact representation)
    /// </summary>
    public float[]? ColorEmbedding { get; set; }

    /// <summary>
    /// Motion signature for animated images
    /// </summary>
    public float[]? MotionEmbedding { get; set; }

    /// <summary>
    /// Text embedding from OCR content
    /// </summary>
    public float[]? TextEmbedding { get; set; }

    /// <summary>
    /// Image type detected
    /// </summary>
    public string? ImageType { get; set; }

    /// <summary>
    /// Create from a dynamic profile (extracts any available embeddings)
    /// </summary>
    public static ImageEmbeddingSet FromProfile(DynamicImageProfile profile)
    {
        return new ImageEmbeddingSet
        {
            Id = profile.GetValue<string>("identity.sha256")
                 ?? profile.GetValue<string>("fingerprint.composite"),
            VisualEmbedding = profile.GetValue<float[]>("embedding.visual"),
            ColorEmbedding = profile.GetValue<float[]>("embedding.color"),
            MotionEmbedding = profile.GetValue<float[]>("embedding.motion"),
            TextEmbedding = profile.GetValue<float[]>("embedding.text"),
            ImageType = profile.GetValue<string>("content.type")
        };
    }
}

/// <summary>
/// Statistics about the salience learner's current state.
/// </summary>
public record LearnerStatistics
{
    public int TotalProfiles { get; init; }
    public int TotalEmbeddings { get; init; }
    public Dictionary<string, int> ProfilesByType { get; init; } = new();
    public int TotalSamples { get; init; }
    public double AverageWeightsPerProfile { get; init; }
}

/// <summary>
/// Serializable container for persisting learned data.
/// </summary>
public class LearnedData
{
    public Dictionary<string, SalienceProfile> Profiles { get; set; } = new();
    public Dictionary<string, ImageEmbeddingSet> Embeddings { get; set; } = new();
    public DateTime SavedAt { get; set; }
}
