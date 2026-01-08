using System.Text;
using Mostlylucid.DataSummarizer.Models;
using Mostlylucid.DataSummarizer.Services;
using Xunit;

namespace Mostlylucid.DataSummarizer.Tests;

/// <summary>
/// Tests for VectorStoreService functionality
/// </summary>
public class VectorStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _tempDir;

    public VectorStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ds-vss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.duckdb");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }

    [Fact]
    public async Task Initialize_CreatesDatabase()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        Assert.True(store.IsAvailable);
    }

    [Fact]
    public async Task UpsertProfile_StoresProfile()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        var profile = CreateTestProfile();
        await store.UpsertProfileAsync(profile);

        // Should not throw
        Assert.True(store.IsAvailable);
    }

    [Fact]
    public async Task UpsertEmbeddings_StoresEmbeddings()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        var profile = CreateTestProfile();
        await store.UpsertProfileAsync(profile);
        await store.UpsertEmbeddingsAsync(profile);

        // Should be able to search
        var hits = await store.SearchAsync("test data", topK: 5);
        Assert.NotNull(hits);
    }

    [Fact]
    public async Task Search_ReturnsRelevantResults()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        var profile = CreateTestProfile();
        await store.UpsertProfileAsync(profile);
        await store.UpsertEmbeddingsAsync(profile);

        var hits = await store.SearchAsync("age salary", topK: 5);

        Assert.NotEmpty(hits);
    }

    [Fact]
    public async Task Search_ReturnsEmptyForNoData()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        var hits = await store.SearchAsync("test query", topK: 5);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task AppendConversationTurn_StoresTurn()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        var sessionId = Guid.NewGuid().ToString();
        await store.AppendConversationTurnAsync(sessionId, "user", "Hello world");
        await store.AppendConversationTurnAsync(sessionId, "assistant", "Hi there!");

        // Should be retrievable
        var context = await store.GetConversationContextAsync(sessionId, "greeting", topK: 5);
        Assert.NotEmpty(context);
    }

    [Fact]
    public async Task GetConversationContext_FiltersbySession()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        var session1 = "session-1";
        var session2 = "session-2";

        await store.AppendConversationTurnAsync(session1, "user", "Session 1 message");
        await store.AppendConversationTurnAsync(session2, "user", "Session 2 message");

        var context1 = await store.GetConversationContextAsync(session1, "message", topK: 5);
        var context2 = await store.GetConversationContextAsync(session2, "message", topK: 5);

        Assert.All(context1, c => Assert.Contains("Session 1", c.Content));
        Assert.All(context2, c => Assert.Contains("Session 2", c.Content));
    }

    [Fact]
    public async Task UpsertNovelPattern_StoresPattern()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        var pattern = new NovelPatternRecord
        {
            PatternName = "Email Pattern",
            ColumnName = "contact_email",
            FilePath = "/test/file.csv",
            PatternType = "Email",
            DetectedRegex = @"[\w.-]+@[\w.-]+\.\w+",
            Description = "Standard email format",
            Examples = new List<string> { "test@example.com", "user@domain.org" },
            MatchPercent = 95.5,
            IsIdentifier = false,
            IsSensitive = true
        };

        await store.UpsertNovelPatternAsync(pattern);

        // Should be searchable
        var hits = await store.SearchPatternsAsync("email format", topK: 5);
        Assert.NotEmpty(hits);
    }

    [Fact]
    public async Task SearchPatterns_FindsSimilarPatterns()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        var emailPattern = new NovelPatternRecord
        {
            PatternName = "Email Pattern",
            ColumnName = "email",
            FilePath = "/test/file.csv",
            PatternType = "Email",
            Description = "Email addresses",
            Examples = new List<string> { "test@example.com" }
        };

        var phonePattern = new NovelPatternRecord
        {
            PatternName = "Phone Pattern",
            ColumnName = "phone",
            FilePath = "/test/file.csv",
            PatternType = "Phone",
            Description = "Phone numbers",
            Examples = new List<string> { "555-123-4567" }
        };

        await store.UpsertNovelPatternAsync(emailPattern);
        await store.UpsertNovelPatternAsync(phonePattern);

        var emailHits = await store.SearchPatternsAsync("email address format", topK: 5);
        var phoneHits = await store.SearchPatternsAsync("telephone number", topK: 5);

        Assert.NotEmpty(emailHits);
        Assert.NotEmpty(phoneHits);
    }

    [Fact]
    public async Task GetPatternsForFile_ReturnsFilePatterns()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        var pattern1 = new NovelPatternRecord
        {
            PatternName = "Pattern 1",
            ColumnName = "col1",
            FilePath = "/test/file1.csv",
            PatternType = "Custom"
        };

        var pattern2 = new NovelPatternRecord
        {
            PatternName = "Pattern 2",
            ColumnName = "col2",
            FilePath = "/test/file1.csv",
            PatternType = "Custom"
        };

        var pattern3 = new NovelPatternRecord
        {
            PatternName = "Pattern 3",
            ColumnName = "col1",
            FilePath = "/test/file2.csv",
            PatternType = "Custom"
        };

        await store.UpsertNovelPatternAsync(pattern1);
        await store.UpsertNovelPatternAsync(pattern2);
        await store.UpsertNovelPatternAsync(pattern3);

        var file1Patterns = await store.GetPatternsForFileAsync("/test/file1.csv");
        var file2Patterns = await store.GetPatternsForFileAsync("/test/file2.csv");

        Assert.Equal(2, file1Patterns.Count);
        Assert.Single(file2Patterns);
    }

    [Fact]
    public async Task UpsertNovelPattern_UpdatesExistingPattern()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        var pattern = new NovelPatternRecord
        {
            PatternName = "Original Name",
            ColumnName = "test_col",
            FilePath = "/test/file.csv",
            PatternType = "Custom",
            Description = "Original description"
        };

        await store.UpsertNovelPatternAsync(pattern);

        // Update same column/file with new description
        var updatedPattern = new NovelPatternRecord
        {
            PatternName = "Updated Name",
            ColumnName = "test_col",
            FilePath = "/test/file.csv",
            PatternType = "Custom",
            Description = "Updated description"
        };

        await store.UpsertNovelPatternAsync(updatedPattern);

        var patterns = await store.GetPatternsForFileAsync("/test/file.csv");
        Assert.Single(patterns);
        Assert.Equal("Updated Name", patterns[0].PatternName);
    }

    [Fact]
    public async Task EmbeddingDimension_ReturnsCorrectValue()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        // Default hash-based embeddings use 128 dimensions
        Assert.Equal(128, store.EmbeddingDimension);
    }

    [Fact]
    public async Task MultipleProfiles_AllSearchable()
    {
        using var store = new VectorStoreService(_dbPath, verbose: false);
        await store.InitializeAsync();

        var profile1 = CreateTestProfile("file1.csv", new[] { "Name", "Age" });
        var profile2 = CreateTestProfile("file2.csv", new[] { "Product", "Price" });

        await store.UpsertProfileAsync(profile1);
        await store.UpsertEmbeddingsAsync(profile1);
        await store.UpsertProfileAsync(profile2);
        await store.UpsertEmbeddingsAsync(profile2);

        var hits = await store.SearchAsync("name age", topK: 10);
        Assert.NotEmpty(hits);

        var productHits = await store.SearchAsync("product price", topK: 10);
        Assert.NotEmpty(productHits);
    }

    #region Helper Methods

    private static DataProfile CreateTestProfile(string fileName = "test.csv", string[]? columnNames = null)
    {
        columnNames ??= new[] { "Name", "Age", "Salary" };

        return new DataProfile
        {
            SourcePath = $"/test/{fileName}",
            RowCount = 100,
            // ColumnCount is computed from Columns.Count
            Columns = columnNames.Select(name => new ColumnProfile
            {
                Name = name,
                InferredType = ColumnType.Text,
                Count = 100,
                NullCount = 0,       // NullPercent is computed from NullCount/Count
                UniqueCount = 50     // UniquePercent is computed from UniqueCount/Count
            }).ToList(),
            Insights = new List<DataInsight>
            {
                new DataInsight
                {
                    Title = "Test Insight",
                    Description = "This is a test insight",
                    Source = InsightSource.Statistical,
                    RelatedColumns = columnNames.Take(2).ToList()
                }
            }
        };
    }

    #endregion
}
