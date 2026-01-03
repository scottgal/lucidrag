using Xunit;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Tests.Services;

public class BatchProcessorTests
{
    [Fact]
    public void BatchConfig_DefaultExtensions_ContainsExpectedTypes()
    {
        // Arrange
        var config = new BatchConfig();

        // Assert
        Assert.Contains(".pdf", config.FileExtensions);
        Assert.Contains(".docx", config.FileExtensions);
        Assert.Contains(".md", config.FileExtensions);
    }

    [Fact]
    public void BatchConfig_ContinueOnError_DefaultsToTrue()
    {
        // Arrange
        var config = new BatchConfig();

        // Assert
        Assert.True(config.ContinueOnError);
    }

    [Fact]
    public void BatchConfig_Recursive_DefaultsToFalse()
    {
        // Arrange
        var config = new BatchConfig();

        // Assert
        Assert.False(config.Recursive);
    }

    [Fact]
    public void BatchResult_Success_ContainsSummary()
    {
        // Arrange
        var summary = new DocumentSummary(
            "Test executive summary",
            new List<TopicSummary>(),
            new List<string>(),
            new SummarizationTrace("test.pdf", 1, 1, new List<string>(), TimeSpan.FromSeconds(1), 1.0, 1.0));

        // Act
        var result = new BatchResult("test.pdf", true, summary, null, TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Summary);
        Assert.Null(result.Error);
        Assert.Equal("test.pdf", result.FilePath);
    }

    [Fact]
    public void BatchResult_Failure_ContainsError()
    {
        // Act
        var result = new BatchResult("test.pdf", false, null, "File not found", TimeSpan.FromSeconds(1));

        // Assert
        Assert.False(result.Success);
        Assert.Null(result.Summary);
        Assert.Equal("File not found", result.Error);
    }

    [Fact]
    public void BatchSummary_CalculatesSuccessRate()
    {
        // Arrange
        var results = new List<BatchResult>
        {
            new("file1.pdf", true, null, null, TimeSpan.FromSeconds(1)),
            new("file2.pdf", true, null, null, TimeSpan.FromSeconds(1)),
            new("file3.pdf", false, null, "error", TimeSpan.FromSeconds(1))
        };

        // Act
        var summary = new BatchSummary(3, 2, 1, results, TimeSpan.FromSeconds(3));

        // Assert
        Assert.Equal(3, summary.TotalFiles);
        Assert.Equal(2, summary.SuccessCount);
        Assert.Equal(1, summary.FailureCount);
        Assert.Equal(2.0 / 3.0, summary.SuccessRate, precision: 2);
    }

    [Fact]
    public void BatchSummary_ZeroFiles_ReturnsZeroSuccessRate()
    {
        // Act
        var summary = new BatchSummary(0, 0, 0, new List<BatchResult>(), TimeSpan.Zero);

        // Assert
        Assert.Equal(0, summary.SuccessRate);
    }
}
