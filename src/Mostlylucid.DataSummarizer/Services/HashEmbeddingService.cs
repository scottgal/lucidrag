using System.Security.Cryptography;
using System.Text;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Hash-based embedding service for fallback when ONNX is not available or disabled.
/// Uses locality-sensitive hashing to create approximate embeddings.
/// Not suitable for semantic similarity but works for basic retrieval.
/// </summary>
public class HashEmbeddingService : IEmbeddingService
{
    private const int Dimension = 128;

    public int EmbeddingDimension => Dimension;

    public Task InitializeAsync(CancellationToken ct = default)
    {
        // No initialization needed for hash-based embeddings
        return Task.CompletedTask;
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        return Task.FromResult(EmbedText(text));
    }

    public Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var results = texts.Select(EmbedText).ToArray();
        return Task.FromResult(results);
    }

    private static float[] EmbedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new float[Dimension];

        var vector = new float[Dimension];
        var tokens = Tokenize(text);

        foreach (var token in tokens)
        {
            var idx = HashToIndex(token, Dimension);
            vector[idx] += 1.0f;
        }

        Normalize(vector);
        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static int HashToIndex(string token, int mod)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(token));
        var val = BitConverter.ToUInt32(bytes, 0);
        return (int)(val % (uint)mod);
    }

    private static void Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm <= 0) return;
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }
    }
}
