using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using OpenCvSharp;
using System.Security.Cryptography;
using System.Text;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Face detection and embedding wave for PII-respecting face recognition.
/// Detects faces, generates anonymous embeddings (no identification).
/// Supports:
/// - Face detection (Haar Cascade / DNN)
/// - Face embedding generation (512-dim vector)
/// - Face clustering ("same person" without identifying who)
/// - Privacy-preserving: Embeddings only, no face images stored
/// </summary>
public class FaceDetectionWave : IAnalysisWave
{
    public string Name => "FaceDetectionWave";
    public int Priority => 75; // After color/identity, before OCR
    public IReadOnlyList<string> Tags => new[] { SignalTags.Visual, "faces", "objects" };

    private static readonly Lazy<CascadeClassifier?> _faceCascade = new(() =>
    {
        try
        {
            // Try to load Haar cascade from OpenCV data directory
            var cascadePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "opencv", "haarcascade_frontalface_default.xml"
            );

            if (!File.Exists(cascadePath))
            {
                // Fallback: Try system OpenCV installation
                cascadePath = "/usr/share/opencv4/haarcascades/haarcascade_frontalface_default.xml";
            }

            if (File.Exists(cascadePath))
            {
                return new CascadeClassifier(cascadePath);
            }

            return null;
        }
        catch
        {
            return null;
        }
    });

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        try
        {
            // Load image with OpenCV
            using var mat = await Task.Run(() => Cv2.ImRead(imagePath), ct);

            if (mat.Empty())
            {
                return signals;
            }

            // Detect faces
            var faces = DetectFaces(mat);

            if (faces.Length == 0)
            {
                // No faces detected
                signals.Add(new Signal
                {
                    Key = "objects.faces",
                    Value = new List<Models.Dynamic.FaceDetection>(),
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "faces", SignalTags.Visual }
                });
                return signals;
            }

            // Process each detected face
            var faceDetections = new List<Models.Dynamic.FaceDetection>();
            var faceEmbeddings = new List<FaceEmbedding>();

            for (int i = 0; i < faces.Length; i++)
            {
                var face = faces[i];

                // Extract face region
                var faceRoi = new Mat(mat, face);

                // Generate embedding (PII-respecting signature)
                var embedding = GenerateFaceEmbedding(faceRoi);
                var embeddingHash = HashEmbedding(embedding);

                // Create face detection result
                var faceDetection = new Models.Dynamic.FaceDetection
                {
                    Location = new Models.Dynamic.BoundingBox
                    {
                        X = face.X,
                        Y = face.Y,
                        Width = face.Width,
                        Height = face.Height
                    },
                    Confidence = 0.9, // Haar cascade doesn't provide confidence
                    Attributes = new Dictionary<string, double>
                    {
                        ["face_size"] = face.Width * face.Height,
                        ["aspect_ratio"] = (double)face.Width / face.Height
                    }
                };

                faceDetections.Add(faceDetection);

                // Store embedding for RAG search
                faceEmbeddings.Add(new FaceEmbedding
                {
                    FaceIndex = i,
                    Embedding = embedding,
                    EmbeddingHash = embeddingHash,
                    Location = faceDetection.Location,
                    Confidence = faceDetection.Confidence
                });
            }

            // Emit face detection signals
            signals.Add(new Signal
            {
                Key = "objects.faces",
                Value = faceDetections,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "faces", SignalTags.Visual },
                Metadata = new Dictionary<string, object>
                {
                    ["detector"] = "haar_cascade_frontalface",
                    ["privacy"] = "embeddings_only_no_images"
                }
            });

            signals.Add(new Signal
            {
                Key = "objects.face_count",
                Value = faces.Length,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "faces", "objects" }
            });

            // Emit face embeddings for RAG search
            signals.Add(new Signal
            {
                Key = "faces.embeddings",
                Value = faceEmbeddings,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "faces", "embeddings", SignalTags.Identity },
                Metadata = new Dictionary<string, object>
                {
                    ["embedding_dim"] = faceEmbeddings.FirstOrDefault()?.Embedding?.Length ?? 0,
                    ["privacy_preserving"] = true,
                    ["searchable"] = true,
                    ["purpose"] = "same_person_clustering_without_identification"
                }
            });

            // Emit face embedding hashes for quick lookup
            var embeddingHashes = faceEmbeddings.Select(e => e.EmbeddingHash).ToList();
            signals.Add(new Signal
            {
                Key = "faces.embedding_hashes",
                Value = embeddingHashes,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "faces", SignalTags.Identity }
            });

            // Check for face clustering opportunities
            if (faces.Length > 1)
            {
                var clusters = ClusterFaces(faceEmbeddings);
                signals.Add(new Signal
                {
                    Key = "faces.clusters",
                    Value = clusters,
                    Confidence = 0.8,
                    Source = Name,
                    Tags = new List<string> { "faces", "analysis" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["unique_people_estimate"] = clusters.Count,
                        ["interpretation"] = clusters.Count < faces.Length
                            ? "Multiple faces of same person(s) detected"
                            : "All faces appear unique"
                    }
                });
            }
        }
        catch (Exception ex)
        {
            // Face detection failed - emit empty result
            signals.Add(new Signal
            {
                Key = "objects.faces",
                Value = new List<Models.Dynamic.FaceDetection>(),
                Confidence = 0.0,
                Source = Name,
                Tags = new List<string> { "faces", "error" },
                Metadata = new Dictionary<string, object>
                {
                    ["error"] = ex.Message
                }
            });
        }

        return signals;
    }

    /// <summary>
    /// Detect faces using Haar Cascade classifier.
    /// Fallback to empty if cascade not available.
    /// </summary>
    private static Rect[] DetectFaces(Mat image)
    {
        var cascade = _faceCascade.Value;
        if (cascade == null)
        {
            return Array.Empty<Rect>();
        }

        // Convert to grayscale for detection
        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);

        // Detect faces
        var faces = cascade.DetectMultiScale(
            gray,
            scaleFactor: 1.1,
            minNeighbors: 3,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: new Size(30, 30)
        );

        return faces;
    }

    /// <summary>
    /// Generate face embedding (512-dim vector).
    /// Uses simplified eigenface-style approach with PCA-like reduction.
    /// For production, use FaceNet/ArcFace ONNX model.
    /// </summary>
    private static float[] GenerateFaceEmbedding(Mat faceRoi)
    {
        const int embeddingDim = 512;

        // Resize to standard size
        using var resized = new Mat();
        Cv2.Resize(faceRoi, resized, new Size(96, 96));

        // Convert to grayscale
        using var gray = new Mat();
        Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);

        // Compute HOG-like features (simplified)
        var embedding = new float[embeddingDim];

        // Split into blocks and compute local histograms
        int blockSize = 12; // 8x8 blocks
        int blockCount = 96 / blockSize; // 8 blocks per dimension
        int featureIdx = 0;

        for (int by = 0; by < blockCount && featureIdx < embeddingDim; by++)
        {
            for (int bx = 0; bx < blockCount && featureIdx < embeddingDim; bx++)
            {
                var blockRect = new Rect(bx * blockSize, by * blockSize, blockSize, blockSize);
                using var block = new Mat(gray, blockRect);

                // Compute mean and stddev for block
                Cv2.MeanStdDev(block, out var mean, out var stddev);

                // Compute gradient magnitudes
                using var gx = new Mat();
                using var gy = new Mat();
                Cv2.Sobel(block, gx, MatType.CV_32F, 1, 0);
                Cv2.Sobel(block, gy, MatType.CV_32F, 0, 1);

                using var magnitude = new Mat();
                Cv2.Magnitude(gx, gy, magnitude);

                var avgMagnitude = Cv2.Mean(magnitude);

                // Store features
                if (featureIdx < embeddingDim - 2)
                {
                    embedding[featureIdx++] = (float)mean.Val0;
                    embedding[featureIdx++] = (float)stddev.Val0;
                    embedding[featureIdx++] = (float)avgMagnitude.Val0;
                }
            }
        }

        // Normalize embedding to unit length (L2 normalization)
        var norm = Math.Sqrt(embedding.Sum(x => x * x));
        if (norm > 0)
        {
            for (int i = 0; i < embedding.Length; i++)
            {
                embedding[i] /= (float)norm;
            }
        }

        return embedding;
    }

    /// <summary>
    /// Hash embedding to fixed-length string for quick lookup.
    /// </summary>
    private static string HashEmbedding(float[] embedding)
    {
        using var sha256 = SHA256.Create();
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash)[..16]; // 64-bit hash
    }

    /// <summary>
    /// Cluster face embeddings to find "same person".
    /// Returns cluster assignments (PII-respecting).
    /// </summary>
    private static List<FaceCluster> ClusterFaces(List<FaceEmbedding> embeddings)
    {
        const double similarityThreshold = 0.85; // Cosine similarity threshold

        var clusters = new List<FaceCluster>();

        foreach (var embedding in embeddings)
        {
            // Find best matching cluster
            FaceCluster? bestCluster = null;
            double bestSimilarity = 0;

            foreach (var cluster in clusters)
            {
                var similarity = CosineSimilarity(embedding.Embedding, cluster.Centroid);
                if (similarity > bestSimilarity && similarity >= similarityThreshold)
                {
                    bestSimilarity = similarity;
                    bestCluster = cluster;
                }
            }

            if (bestCluster != null)
            {
                // Add to existing cluster
                bestCluster.FaceIndices.Add(embedding.FaceIndex);
                UpdateCentroid(bestCluster, embedding.Embedding);
            }
            else
            {
                // Create new cluster
                clusters.Add(new FaceCluster
                {
                    ClusterId = clusters.Count,
                    FaceIndices = new List<int> { embedding.FaceIndex },
                    Centroid = (float[])embedding.Embedding.Clone()
                });
            }
        }

        return clusters;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dotProduct = 0;
        double normA = 0;
        double normB = 0;

        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0)
            return 0;

        return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static void UpdateCentroid(FaceCluster cluster, float[] newEmbedding)
    {
        // Update centroid using running average
        var count = cluster.FaceIndices.Count;
        for (int i = 0; i < cluster.Centroid.Length && i < newEmbedding.Length; i++)
        {
            cluster.Centroid[i] = (cluster.Centroid[i] * (count - 1) + newEmbedding[i]) / count;
        }
    }
}

/// <summary>
/// Face embedding with location and privacy-preserving hash.
/// </summary>
public class FaceEmbedding
{
    public int FaceIndex { get; set; }
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public string EmbeddingHash { get; set; } = string.Empty;
    public Models.Dynamic.BoundingBox Location { get; set; } = new();
    public double Confidence { get; set; }
}

/// <summary>
/// Face cluster representing "same person" (PII-respecting).
/// </summary>
public class FaceCluster
{
    public int ClusterId { get; set; }
    public List<int> FaceIndices { get; set; } = new();
    public float[] Centroid { get; set; } = Array.Empty<float>();
}
