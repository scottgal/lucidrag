using Xunit;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Tests.Services;

public class OutputFormatterTests
{
    private readonly DocumentSummary _testSummary;

    public OutputFormatterTests()
    {
        _testSummary = new DocumentSummary(
            "This is the executive summary with key findings.",
            new List<TopicSummary>
            {
                new("Topic 1", "Summary for topic 1 [chunk-0]", new List<string> { "chunk-0" }),
                new("Topic 2", "Summary for topic 2 [chunk-1]", new List<string> { "chunk-1" })
            },
            new List<string> { "Open question 1?", "Open question 2?" },
            new SummarizationTrace("test.pdf", 5, 5, new List<string> { "Topic 1", "Topic 2" }, 
                TimeSpan.FromSeconds(10), 1.0, 0.8));
    }

    [Fact]
    public void Format_Console_ContainsExecutiveSummary()
    {
        // Arrange
        var config = new OutputConfig { Format = OutputFormat.Console, IncludeTopics = true };

        // Act
        var output = OutputFormatter.Format(_testSummary, config, "test.pdf");

        // Assert
        Assert.Contains("executive summary", output.ToLower());
    }

    [Fact]
    public void Format_Console_WithIncludeTopics_ContainsTopicSummaries()
    {
        // Arrange
        var config = new OutputConfig { Format = OutputFormat.Console, IncludeTopics = true };

        // Act
        var output = OutputFormatter.Format(_testSummary, config, "test.pdf");

        // Assert
        Assert.Contains("Topic 1", output);
        Assert.Contains("Topic 2", output);
    }

    [Fact]
    public void Format_Console_WithoutIncludeTopics_ExcludesTopicSummaries()
    {
        // Arrange
        var config = new OutputConfig { Format = OutputFormat.Console, IncludeTopics = false };

        // Act
        var output = OutputFormatter.Format(_testSummary, config, "test.pdf");

        // Assert
        Assert.DoesNotContain("Topic Summaries", output);
    }

    [Fact]
    public void Format_Console_WithIncludeOpenQuestions_ContainsQuestions()
    {
        // Arrange
        var config = new OutputConfig { Format = OutputFormat.Console, IncludeOpenQuestions = true };

        // Act
        var output = OutputFormatter.Format(_testSummary, config, "test.pdf");

        // Assert
        Assert.Contains("Open question 1", output);
    }

    [Fact]
    public void Format_Console_WithIncludeTrace_ContainsTraceInfo()
    {
        // Arrange
        var config = new OutputConfig { Format = OutputFormat.Console, IncludeTrace = true };

        // Act
        var output = OutputFormatter.Format(_testSummary, config, "test.pdf");

        // Assert
        Assert.Contains("Trace", output);
        Assert.Contains("test.pdf", output);
    }

    [Fact]
    public void Format_Markdown_ContainsMarkdownHeadings()
    {
        // Arrange
        var config = new OutputConfig { Format = OutputFormat.Markdown, IncludeTopics = true };

        // Act
        var output = OutputFormatter.Format(_testSummary, config, "test.pdf");

        // Assert
        Assert.Contains("# Document Summary", output);
        Assert.Contains("## Executive Summary", output);
        Assert.Contains("## Topic Summaries", output);
    }

    [Fact]
    public void Format_Text_ContainsPlainTextFormat()
    {
        // Arrange
        var config = new OutputConfig { Format = OutputFormat.Text };

        // Act
        var output = OutputFormatter.Format(_testSummary, config, "test.pdf");

        // Assert
        Assert.Contains("DOCUMENT SUMMARY", output);
        Assert.Contains("EXECUTIVE SUMMARY", output);
    }

    [Fact]
    public void Format_Json_ReturnsValidJson()
    {
        // Arrange
        var config = new OutputConfig { Format = OutputFormat.Json };

        // Act
        var output = OutputFormatter.Format(_testSummary, config, "test.pdf");

        // Assert
        Assert.StartsWith("{", output.Trim());
        Assert.EndsWith("}", output.Trim());
        Assert.Contains("ExecutiveSummary", output);
    }

    [Fact]
    public void FormatBatch_Console_ContainsBatchSummary()
    {
        // Arrange
        var results = new List<BatchResult>
        {
            new("file1.pdf", true, null, null, TimeSpan.FromSeconds(5)),
            new("file2.pdf", false, null, "Error message", TimeSpan.FromSeconds(2))
        };
        var batchSummary = new BatchSummary(2, 1, 1, results, TimeSpan.FromSeconds(7));
        var config = new OutputConfig { Format = OutputFormat.Console };

        // Act
        var output = OutputFormatter.FormatBatch(batchSummary, config);

        // Assert
        Assert.Contains("BATCH PROCESSING COMPLETE", output);
        Assert.Contains("Total files: 2", output);
        Assert.Contains("Success: 1", output);
        Assert.Contains("Failed: 1", output);
    }

    [Fact]
    public void FormatBatch_Markdown_ContainsMarkdownTable()
    {
        // Arrange
        var results = new List<BatchResult>
        {
            new("file1.pdf", true, null, null, TimeSpan.FromSeconds(5))
        };
        var batchSummary = new BatchSummary(1, 1, 0, results, TimeSpan.FromSeconds(5));
        var config = new OutputConfig { Format = OutputFormat.Markdown };

        // Act
        var output = OutputFormatter.FormatBatch(batchSummary, config);

        // Assert
        Assert.Contains("# Batch Processing Summary", output);
        Assert.Contains("| Metric | Value |", output);
    }
}
