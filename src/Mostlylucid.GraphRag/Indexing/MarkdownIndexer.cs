using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Mostlylucid.GraphRag.Services;
using Mostlylucid.GraphRag.Storage;

namespace Mostlylucid.GraphRag.Indexing;

/// <summary>
/// Indexes markdown files into the GraphRAG database.
/// Uses BERT embeddings from DocSummarizer.Core for vector search.
/// </summary>
public class MarkdownIndexer : IDisposable
{
    private readonly GraphRagDb _db;
    private readonly EmbeddingService _embedder;
    private readonly int _chunkSize;
    private readonly int _chunkOverlap;
    private readonly MarkdownPipeline _mdPipeline;

    public MarkdownIndexer(GraphRagDb db, EmbeddingService embedder, 
        int chunkSize = 512, int chunkOverlap = 50)
    {
        _db = db;
        _embedder = embedder;
        _chunkSize = chunkSize;
        _chunkOverlap = chunkOverlap;
        _mdPipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
    }

    /// <summary>
    /// Index all markdown files in a directory
    /// </summary>
    public async Task IndexDirectoryAsync(string path, IProgress<IndexProgress>? progress = null, 
        CancellationToken ct = default)
    {
        var files = Directory.GetFiles(path, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Contains("node_modules") && !f.Contains(".git"))
            .ToList();

        progress?.Report(new IndexProgress(0, files.Count, "Starting indexing..."));

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            
            var file = files[i];
            var fileName = Path.GetFileName(file);
            progress?.Report(new IndexProgress(i, files.Count, $"Indexing {fileName}"));

            await IndexFileAsync(file, ct);
        }

        progress?.Report(new IndexProgress(files.Count, files.Count, "Indexing complete"));
    }

    /// <summary>
    /// Index a single markdown file
    /// </summary>
    public async Task IndexFileAsync(string filePath, CancellationToken ct = default)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);
        var docId = ComputeDocumentId(filePath);
        var contentHash = ComputeContentHash(content);
        
        // Skip if document already indexed with same content
        if (await _db.DocumentExistsWithHashAsync(docId, contentHash))
            return;
        
        // Delete existing chunks (HNSW index doesn't support duplicate keys)
        await _db.DeleteDocumentChunksAsync(docId);
        
        // Extract title from frontmatter or first heading
        var title = ExtractTitle(content, filePath);
        
        // Store document
        await _db.UpsertDocumentAsync(docId, filePath, title, contentHash);

        // Chunk the content
        var chunks = ChunkMarkdown(content);
        
        // Generate embeddings in batch
        var embeddings = await _embedder.EmbedBatchAsync(chunks.Select(c => c.Text), ct);

        // Store chunks with embeddings
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var chunkId = $"{docId}_{i}";
            await _db.InsertChunkAsync(chunkId, docId, i, chunk.Text, embeddings[i], chunk.TokenCount);
        }
    }
    
    private static string ComputeContentHash(string content)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private List<ChunkInfo> ChunkMarkdown(string content)
    {
        var chunks = new List<ChunkInfo>();
        
        // Strip frontmatter
        content = StripFrontmatter(content);
        
        // Convert to plain text for chunking
        var plainText = Markdown.ToPlainText(content, _mdPipeline);
        
        // Split into sentences/paragraphs
        var paragraphs = SplitIntoParagraphs(plainText);
        
        var currentChunk = new StringBuilder();
        var currentTokens = 0;

        foreach (var para in paragraphs)
        {
            var paraTokens = EstimateTokens(para);
            
            if (currentTokens + paraTokens > _chunkSize && currentChunk.Length > 0)
            {
                // Save current chunk
                chunks.Add(new ChunkInfo(currentChunk.ToString().Trim(), currentTokens));
                
                // Start new chunk with overlap
                currentChunk.Clear();
                currentTokens = 0;
                
                // Add overlap from previous paragraphs if needed
                // (simplified: just start fresh for now)
            }
            
            currentChunk.AppendLine(para);
            currentTokens += paraTokens;
        }

        // Don't forget the last chunk
        if (currentChunk.Length > 0)
        {
            chunks.Add(new ChunkInfo(currentChunk.ToString().Trim(), currentTokens));
        }

        return chunks;
    }

    private static List<string> SplitIntoParagraphs(string text)
    {
        return text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
    }

    private static string StripFrontmatter(string content)
    {
        // Remove YAML frontmatter
        if (content.StartsWith("---"))
        {
            var endIndex = content.IndexOf("---", 3);
            if (endIndex > 0)
            {
                content = content.Substring(endIndex + 3).TrimStart();
            }
        }
        return content;
    }

    private static string ExtractTitle(string content, string filePath)
    {
        // Try to get title from YAML frontmatter
        var titleMatch = Regex.Match(content, @"^---\s*\n.*?title:\s*[""']?(.+?)[""']?\s*\n", 
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (titleMatch.Success)
            return titleMatch.Groups[1].Value.Trim();

        // Try to get from first H1
        var h1Match = Regex.Match(content, @"^#\s+(.+)$", RegexOptions.Multiline);
        if (h1Match.Success)
            return h1Match.Groups[1].Value.Trim();

        // Fall back to filename
        return Path.GetFileNameWithoutExtension(filePath);
    }

    private static int EstimateTokens(string text)
    {
        // Rough estimate: ~4 chars per token for English
        return text.Length / 4;
    }

    private static string ComputeDocumentId(string filePath)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(filePath));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_embedder is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

public record ChunkInfo(string Text, int TokenCount);

public record IndexProgress(int Current, int Total, string Message)
{
    public double Percentage => Total > 0 ? (double)Current / Total * 100 : 0;
}
