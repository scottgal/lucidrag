using LucidRAG.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Tests.Services;

/// <summary>
/// Unit tests for the QueryExpansionService.
/// Tests vocabulary deduplication and basic expansion logic.
/// </summary>
public class QueryExpansionServiceTests
{
    private readonly Mock<IEmbeddingService> _mockEmbedder;
    private readonly Mock<ILogger<EmbeddingQueryExpansionService>> _mockLogger;
    private readonly EmbeddingQueryExpansionService _service;

    public QueryExpansionServiceTests()
    {
        _mockEmbedder = new Mock<IEmbeddingService>();
        _mockLogger = new Mock<ILogger<EmbeddingQueryExpansionService>>();
        _service = new EmbeddingQueryExpansionService(_mockEmbedder.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task ExpandQueryAsync_WithStopword_SkipsExpansion()
    {
        // Arrange - "the" is a stopword
        var query = "the red car";

        // Mock embeddings - return simple vectors
        _mockEmbedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 1.0f, 0.0f, 0.0f });

        // Act
        var result = await _service.ExpandQueryAsync(query);

        // Assert
        Assert.Equal(query, result.OriginalQuery);
        Assert.Contains("the", result.OriginalTerms);
        // "the" should only expand to itself (stopword)
        Assert.Single(result.Expansions["the"]);
        Assert.Equal("the", result.Expansions["the"][0]);
    }

    [Fact]
    public async Task ExpandQueryAsync_WithShortTerm_SkipsExpansion()
    {
        // Arrange - "is" is too short (< 3 chars)
        var query = "it is red";

        _mockEmbedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 1.0f, 0.0f, 0.0f });

        // Act
        var result = await _service.ExpandQueryAsync(query);

        // Assert
        // "it" should only expand to itself (too short)
        Assert.Single(result.Expansions["it"]);
        Assert.Equal("it", result.Expansions["it"][0]);
    }

    [Fact]
    public async Task ExpandQueryAsync_TokenizesCorrectly()
    {
        // Arrange
        var query = "red, blue; green!";

        _mockEmbedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 1.0f, 0.0f, 0.0f });

        // Act
        var result = await _service.ExpandQueryAsync(query);

        // Assert - should split on punctuation
        Assert.Contains("red", result.OriginalTerms);
        Assert.Contains("blue", result.OriginalTerms);
        Assert.Contains("green", result.OriginalTerms);
        Assert.Equal(3, result.OriginalTerms.Count);
    }

    [Fact]
    public async Task ExpandTermAsync_ReturnsOriginalTermFirst()
    {
        // Arrange
        _mockEmbedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 1.0f, 0.0f, 0.0f });

        // Act
        var result = await _service.ExpandTermAsync("golden");

        // Assert - original term should always be first
        Assert.Equal("golden", result[0]);
    }

    [Fact]
    public async Task ExpandTermAsync_CachesResults()
    {
        // Arrange
        _mockEmbedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 1.0f, 0.0f, 0.0f });

        // Act - call twice with same term
        var result1 = await _service.ExpandTermAsync("blue");
        var result2 = await _service.ExpandTermAsync("blue");

        // Assert - both should return same cached result
        Assert.Equal(result1, result2);
    }

    [Fact]
    public async Task ExpandTermAsync_NormalizesToLowercase()
    {
        // Arrange
        _mockEmbedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 1.0f, 0.0f, 0.0f });

        // Act - call with uppercase
        var result = await _service.ExpandTermAsync("BLUE");

        // Assert - should be lowercase
        Assert.Equal("blue", result[0]);
    }

    [Fact]
    public void SignalVocabulary_HasNoEmptyArrays()
    {
        // Access the vocabulary via reflection to verify no empty categories
        var vocabularyField = typeof(EmbeddingQueryExpansionService)
            .GetField("SignalVocabulary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var vocabulary = vocabularyField?.GetValue(null) as Dictionary<string, string[]>;

        Assert.NotNull(vocabulary);

        foreach (var (category, terms) in vocabulary)
        {
            Assert.True(terms.Length > 0, $"Category '{category}' has no terms");
        }
    }

    [Fact]
    public void SignalVocabulary_HasExpectedCategories()
    {
        // Access the vocabulary via reflection
        var vocabularyField = typeof(EmbeddingQueryExpansionService)
            .GetField("SignalVocabulary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var vocabulary = vocabularyField?.GetValue(null) as Dictionary<string, string[]>;

        Assert.NotNull(vocabulary);

        // Verify expected categories exist
        Assert.True(vocabulary.ContainsKey("colors"));
        Assert.True(vocabulary.ContainsKey("entities"));
        Assert.True(vocabulary.ContainsKey("image_types"));
        Assert.True(vocabulary.ContainsKey("motion"));
        Assert.True(vocabulary.ContainsKey("scenes"));
        Assert.True(vocabulary.ContainsKey("subjects"));
        Assert.True(vocabulary.ContainsKey("quality"));
    }

    [Fact]
    public void SignalVocabulary_ColorsContainsExpectedTerms()
    {
        var vocabularyField = typeof(EmbeddingQueryExpansionService)
            .GetField("SignalVocabulary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var vocabulary = vocabularyField?.GetValue(null) as Dictionary<string, string[]>;

        Assert.NotNull(vocabulary);

        var colors = vocabulary["colors"];

        // Verify key colors from ColorAnalyzer are present
        Assert.Contains("red", colors);
        Assert.Contains("blue", colors);
        Assert.Contains("green", colors);
        Assert.Contains("golden", colors);
        Assert.Contains("amber", colors);
        Assert.Contains("crimson", colors);
        Assert.Contains("sapphire", colors);
        Assert.Contains("sepia", colors);
    }
}
