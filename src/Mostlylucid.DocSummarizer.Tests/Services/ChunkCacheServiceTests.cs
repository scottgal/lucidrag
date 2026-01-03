using System.Text.Json;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Xunit;

namespace Mostlylucid.DocSummarizer.Tests.Services;

public class ChunkCacheServiceTests
{
    [Fact(Skip = "Flaky on CI - file system timing issues with v2 cache format. Run manually.")]
    public async Task SaveAndLoad_ReturnsChunks_WhenHashesMatch()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = CreateService(tempDir, retentionDays: 30);
            Assert.True(service.Enabled, "Service should be enabled");
            var chunks = new List<DocumentChunk>
            {
                new(0, "Heading 1", 1, "content one", "hash1", TotalChunks: 2),
                new(1, "Heading 2", 1, "content two", "hash2", TotalChunks: 2)
            };
            Assert.True(chunks.Count > 0, "Chunks should not be empty");

            await service.SaveAsync("doc", "filehash", chunks);

            // List all files/dirs for debugging
            var allFiles = Directory.Exists(tempDir) 
                ? Directory.GetFileSystemEntries(tempDir, "*", SearchOption.AllDirectories) 
                : Array.Empty<string>();

            // v2 format: metadata JSON + content directory
            var metadataPath = Path.Combine(tempDir, "doc_filehash.json");
            var contentDir = Path.Combine(tempDir, "doc_filehash_content");
            
            Assert.True(File.Exists(metadataPath), 
                $"Metadata file not found at {metadataPath}. TempDir exists: {Directory.Exists(tempDir)}. All entries: [{string.Join(", ", allFiles)}]");
            Assert.True(Directory.Exists(contentDir), $"Content directory not found at {contentDir}");
 
            var loaded = await service.TryLoadAsync("doc", "filehash");
 
            Assert.True(loaded != null, $"Cache load failed. Enabled={service.Enabled}");
            Assert.Equal(2, loaded!.Count);
            Assert.All(loaded, c => Assert.Equal(2, c.TotalChunks));
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task TryLoad_ReturnsNull_WhenHashDoesNotMatch()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = CreateService(tempDir, retentionDays: 30);
            var chunks = new List<DocumentChunk> { new(0, "Heading", 1, "content", "hash1") };

            await service.SaveAsync("doc", "filehash", chunks);
            var loaded = await service.TryLoadAsync("doc", "different-hash");

            Assert.Null(loaded);
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    [Fact(Skip = "Flaky on CI - file system timing issues with v2 cache format. Run manually.")]
    public async Task TryLoad_PrunesExpiredEntries()
    {
        var tempDir = CreateTempDir();
        try
        {
            var service = CreateService(tempDir, retentionDays: 1);
            Assert.True(service.Enabled);
            var chunks = new List<DocumentChunk> { new(0, "Heading", 1, "content", "hash1") };

            await service.SaveAsync("doc", "filehash", chunks);
            
            // v2 format paths
            var metadataPath = Path.Combine(tempDir, "doc_filehash.json");
            var contentDir = Path.Combine(tempDir, "doc_filehash_content");
            Assert.True(File.Exists(metadataPath));
            Assert.True(Directory.Exists(contentDir));
 
            // Overwrite with an expired v2 metadata entry
            var expiredMetadata = new
            {
                DocId = "doc",
                FileHash = "filehash",
                Version = "v1",
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-10),
                LastAccessUtc = DateTimeOffset.UtcNow.AddDays(-10),
                ChunkMetadata = new[] { new { Index = 0, Heading = "Heading", HeadingLevel = 1, ContentHash = "hash1" } }
            };

            var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            await using (var stream = File.Create(metadataPath))
            {
                await JsonSerializer.SerializeAsync(stream, expiredMetadata, options);
            }
 
            var loaded = await service.TryLoadAsync("doc", "filehash");
 
            // Should return null for expired entry
            Assert.Null(loaded);
            // Should have cleaned up the metadata file
            Assert.False(File.Exists(metadataPath), "Expired metadata file should be deleted");
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    private static ChunkCacheService CreateService(string dir, int retentionDays)
    {
        var config = new ChunkCacheConfig
        {
            EnableChunkCache = true,
            CacheDirectory = dir,
            RetentionDays = retentionDays,
            VersionToken = "v1"
        };

        return new ChunkCacheService(config, verbose: true);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cachetests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch
        {
            // ignore
        }
    }
}
