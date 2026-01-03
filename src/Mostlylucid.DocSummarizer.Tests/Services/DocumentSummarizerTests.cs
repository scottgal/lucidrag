using Xunit;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.Tests.Services;

/// <summary>
/// Integration tests for DocumentSummarizer - requires Ollama, Docling, and Qdrant running locally
/// These tests are marked with the IntegrationTest trait and can be skipped in CI
/// </summary>
public class DocumentSummarizerTests
{
    private const string TestCategory = "IntegrationTest";
    private readonly DocumentSummarizer _summarizer;

    /// <summary>
    /// Initializes a new instance of the DocumentSummarizerTests class
    /// </summary>
    public DocumentSummarizerTests()
    {
        _summarizer = new DocumentSummarizer("llama3.2:3b");
    }

    [Fact(Skip = "Integration test - requires Ollama, Docling, and Qdrant running locally")]
    [Trait("Category", TestCategory)]
    public async Task SummarizeAsync_WithValidMarkdown_ReturnsSummary()
    {
        // Arrange
        var markdown = @"
# Test Document
This is a test document.

## Section 1
Content for section 1.

## Section 2
Content for section 2.
";

        // Act
        var summary = await _summarizer.SummarizeAsync("test.md", SummarizationMode.MapReduce);

        // Assert
        Assert.NotNull(summary);
        Assert.NotNull(summary.ExecutiveSummary);
        Assert.True(summary.Trace.TotalChunks > 0);
    }

    [Fact(Skip = "Integration test - requires Ollama, Docling, and Qdrant running locally")]
    [Trait("Category", TestCategory)]
    public async Task SummarizeAsync_WithRagMode_ReturnsSummary()
    {
        // Arrange
        var markdown = @"
# Test Document
This is a test document.

## Section 1
Content for section 1.

## Section 2
Content for section 2.
";

        // Act
        var summary = await _summarizer.SummarizeAsync("test.md", SummarizationMode.Rag);

        // Assert
        Assert.NotNull(summary);
        Assert.NotNull(summary.ExecutiveSummary);
        Assert.True(summary.Trace.TotalChunks > 0);
    }

    [Fact(Skip = "Integration test - requires Ollama, Docling, and Qdrant running locally")]
    [Trait("Category", TestCategory)]
    public async Task QueryAsync_WithValidQuery_ReturnsAnswer()
    {
        // Arrange
        var markdown = @"
# Test Document
This is a test document.

## Section 1
Content for section 1.

## Section 2
Content for section 2.
";

        // Act
        var answer = await _summarizer.QueryAsync("test.md", "What is the document about?");

        // Assert
        Assert.NotNull(answer);
        Assert.NotEmpty(answer);
    }

    [Fact(Skip = "Integration test - requires Ollama, Docling, and Qdrant running locally")]
    [Trait("Category", TestCategory)]
    public async Task SummarizeAsync_WithIterativeMode_ReturnsSummary()
    {
        // Arrange
        var markdown = @"
# Test Document
This is a test document.

## Section 1
Content for section 1.

## Section 2
Content for section 2.
";

        // Act
        var summary = await _summarizer.SummarizeAsync("test.md", SummarizationMode.Iterative);

        // Assert
        Assert.NotNull(summary);
        Assert.NotNull(summary.ExecutiveSummary);
        Assert.True(summary.Trace.TotalChunks > 0);
    }

    [Fact(Skip = "Integration test - requires Ollama, Docling, and Qdrant running locally")]
    [Trait("Category", TestCategory)]
    public async Task SummarizeAsync_WithDifferentModes_ReturnsDifferentSummaries()
    {
        // Arrange
        var markdown = @"
# Test Document
This is a test document.

## Section 1
Content for section 1.

## Section 2
Content for section 2.
";

        // Act
        var mapReduceSummary = await _summarizer.SummarizeAsync("test.md", SummarizationMode.MapReduce);
        var ragSummary = await _summarizer.SummarizeAsync("test.md", SummarizationMode.Rag);

        // Assert
        Assert.NotNull(mapReduceSummary);
        Assert.NotNull(ragSummary);
        Assert.NotEqual(mapReduceSummary.ExecutiveSummary, ragSummary.ExecutiveSummary);
    }
}