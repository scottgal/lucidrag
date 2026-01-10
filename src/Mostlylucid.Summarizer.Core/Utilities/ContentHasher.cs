using System.IO.Hashing;
using System.Text;

namespace Mostlylucid.Summarizer.Core.Utilities;

/// <summary>
/// Unified content hashing utility using XxHash64 for fast, consistent hashing.
/// </summary>
public static class ContentHasher
{
    /// <summary>
    /// Compute a fast content hash suitable for deduplication and cache keys.
    /// Returns a 16-character lowercase hex string.
    /// </summary>
    /// <param name="content">The content to hash.</param>
    /// <returns>16-character lowercase hex hash, or empty string for null/empty content.</returns>
    public static string ComputeHash(string content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var hash = XxHash64.Hash(Encoding.UTF8.GetBytes(content));
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(hash);
#else
        return Convert.ToHexString(hash).ToLowerInvariant();
#endif
    }

    /// <summary>
    /// Compute a hash from a stream (for file content).
    /// </summary>
    public static string ComputeHash(Stream stream)
    {
        if (stream == null || stream.Length == 0)
            return string.Empty;

        var position = stream.Position;
        try
        {
            var hasher = new XxHash64();
            var buffer = new byte[8192];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, bytesRead));
            }
            var hash = hasher.GetHashAndReset();
#if NET9_0_OR_GREATER
            return Convert.ToHexStringLower(hash);
#else
            return Convert.ToHexString(hash).ToLowerInvariant();
#endif
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = position;
        }
    }

    /// <summary>
    /// Compute hash from raw bytes.
    /// </summary>
    public static string ComputeHash(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return string.Empty;

        var hash = XxHash64.Hash(bytes);
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(hash);
#else
        return Convert.ToHexString(hash).ToLowerInvariant();
#endif
    }

    /// <summary>
    /// Compute hash as UInt64 (useful for numeric IDs).
    /// </summary>
    public static ulong ComputeHashUInt64(string content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;

        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// Compute hash as UInt64 from bytes.
    /// </summary>
    public static ulong ComputeHashUInt64(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return 0;

        return XxHash64.HashToUInt64(bytes);
    }

    /// <summary>
    /// Generate a stable Guid from content string.
    /// Useful for creating deterministic IDs from content.
    /// </summary>
    public static Guid ComputeGuid(string content)
    {
        if (string.IsNullOrEmpty(content))
            return Guid.Empty;

        var bytes = Encoding.UTF8.GetBytes(content);
        // Compute two hashes with different seeds to get 16 bytes
        var hash1 = XxHash64.HashToUInt64(bytes, 0);
        var hash2 = XxHash64.HashToUInt64(bytes, 1);

        var guidBytes = new byte[16];
        BitConverter.GetBytes(hash1).CopyTo(guidBytes, 0);
        BitConverter.GetBytes(hash2).CopyTo(guidBytes, 8);

        return new Guid(guidBytes);
    }
}
