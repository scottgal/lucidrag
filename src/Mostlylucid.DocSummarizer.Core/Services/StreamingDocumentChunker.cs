using System.Text;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Memory-efficient document chunker that processes markdown line-by-line
/// without loading the entire document into memory.
/// </summary>
public class StreamingDocumentChunker
{
    private const int CharsPerToken = 4;
    
    private readonly int _maxHeadingLevel;
    private readonly int _targetChunkTokens;
    private readonly int _minChunkTokens;

    public StreamingDocumentChunker(
        int maxHeadingLevel = 2,
        int targetChunkTokens = 4000,
        int minChunkTokens = 500)
    {
        _maxHeadingLevel = Math.Clamp(maxHeadingLevel, 1, 6);
        _targetChunkTokens = targetChunkTokens;
        _minChunkTokens = minChunkTokens;
    }

    /// <summary>
    /// Process a markdown file and stream chunks directly to a store
    /// </summary>
    public async Task ChunkFileToStoreAsync(
        string filePath,
        DiskBackedChunkStore store,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        await ChunkStreamToStoreAsync(reader, store, cancellationToken);
    }

    /// <summary>
    /// Process markdown from a stream and add chunks directly to store
    /// </summary>
    public async Task ChunkStreamToStoreAsync(
        TextReader reader,
        DiskBackedChunkStore store,
        CancellationToken cancellationToken = default)
    {
        var currentHeading = "";
        var currentLevel = 0;
        var currentContent = new StringBuilder();
        var currentTokens = 0;
        var chunkIndex = 0;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var headingLevel = GetHeadingLevel(line);

            if (headingLevel > 0 && headingLevel <= _maxHeadingLevel)
            {
                // New section - flush previous if it has content
                if (currentContent.Length > 0 || !string.IsNullOrEmpty(currentHeading))
                {
                    var chunk = CreateChunk(chunkIndex++, currentHeading, currentLevel, currentContent.ToString().Trim());
                    if (!string.IsNullOrWhiteSpace(chunk.Content))
                    {
                        store.Add(chunk);
                    }
                    currentContent.Clear();
                    currentTokens = 0;
                }

                currentHeading = line.TrimStart('#', ' ');
                currentLevel = headingLevel;
            }
            else
            {
                // Check if adding this line would exceed target
                var lineTokens = EstimateTokens(line);
                
                if (currentContent.Length > 0 && 
                    currentTokens + lineTokens > _targetChunkTokens &&
                    currentTokens >= _minChunkTokens)
                {
                    // Flush current chunk and start new one (keeping same heading context)
                    var chunk = CreateChunk(chunkIndex++, currentHeading, currentLevel, currentContent.ToString().Trim());
                    if (!string.IsNullOrWhiteSpace(chunk.Content))
                    {
                        store.Add(chunk);
                    }
                    currentContent.Clear();
                    currentTokens = 0;
                    
                    // Continue with same heading but mark as continuation
                    if (!string.IsNullOrEmpty(currentHeading) && !currentHeading.EndsWith(" (cont.)"))
                    {
                        currentHeading += " (cont.)";
                    }
                }

                currentContent.AppendLine(line);
                currentTokens += lineTokens;
            }
        }

        // Flush final chunk
        if (currentContent.Length > 0 || !string.IsNullOrEmpty(currentHeading))
        {
            var finalChunk = CreateChunk(chunkIndex, currentHeading, currentLevel, currentContent.ToString().Trim());
            if (!string.IsNullOrWhiteSpace(finalChunk.Content))
            {
                store.Add(finalChunk);
            }
        }
    }

    /// <summary>
    /// Process markdown string in a streaming fashion (reads line by line)
    /// </summary>
    public async Task ChunkStringToStoreAsync(
        string markdown,
        DiskBackedChunkStore store,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StringReader(markdown);
        await ChunkStreamToStoreAsync(reader, store, cancellationToken);
    }

    /// <summary>
    /// Process large markdown by writing to temp file first, then streaming
    /// This is useful when the markdown string is very large and we want to
    /// release the string memory before processing
    /// </summary>
    public async Task ChunkLargeStringToStoreAsync(
        string markdown,
        DiskBackedChunkStore store,
        CancellationToken cancellationToken = default)
    {
        // Write to temp file to release the string from memory
        var tempPath = Path.Combine(Path.GetTempPath(), $"docsummarizer_temp_{Guid.NewGuid():N}.md");
        
        try
        {
            await File.WriteAllTextAsync(tempPath, markdown, cancellationToken);
            
            // Release the string reference - caller should set their reference to null
            // The GC can now reclaim the string memory
            
            // Process from file
            await ChunkFileToStoreAsync(tempPath, store, cancellationToken);
        }
        finally
        {
            // Clean up temp file
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Estimate total chunks without creating them (for progress reporting)
    /// </summary>
    public int EstimateChunkCount(long fileSizeBytes)
    {
        // Rough estimate: file size / (target tokens * chars per token)
        var targetBytesPerChunk = _targetChunkTokens * CharsPerToken;
        return Math.Max(1, (int)Math.Ceiling((double)fileSizeBytes / targetBytesPerChunk));
    }

    private static DocumentChunk CreateChunk(int order, string heading, int level, string content)
    {
        return new DocumentChunk(
            order,
            heading,
            level,
            content,
            HashHelper.ComputeHash(content));
    }

    private static int GetHeadingLevel(string line)
    {
        if (string.IsNullOrEmpty(line) || !line.StartsWith('#'))
            return 0;

        var level = 0;
        foreach (var c in line)
        {
            if (c == '#') level++;
            else break;
        }

        // Must have space after # marks to be a valid heading
        return line.Length > level && line[level] == ' ' ? level : 0;
    }

    private int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Length / CharsPerToken;
    }
}

/// <summary>
/// Helper for computing content hashes
/// </summary>
public static class HashHelper
{
    public static string ComputeHash(string content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
