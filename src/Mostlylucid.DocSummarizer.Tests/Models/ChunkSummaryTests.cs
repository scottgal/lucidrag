using Xunit;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Tests.Models;

public class ChunkSummaryTests
{
    [Fact]
    public void ChunkSummary_CreatesWithCorrectProperties()
    {
        // Act
        var summary = new ChunkSummary("chunk-0", "Introduction", "This is the summary content.", 0);

        // Assert
        Assert.Equal("chunk-0", summary.ChunkId);
        Assert.Equal("Introduction", summary.Heading);
        Assert.Equal("This is the summary content.", summary.Summary);
        Assert.Equal(0, summary.Order);
    }

    [Fact]
    public void TopicSummary_CreatesWithCorrectProperties()
    {
        // Arrange
        var sourceChunks = new List<string> { "chunk-0", "chunk-1" };

        // Act
        var summary = new TopicSummary("Security", "Security findings summary [chunk-0]", sourceChunks);

        // Assert
        Assert.Equal("Security", summary.Topic);
        Assert.Equal("Security findings summary [chunk-0]", summary.Summary);
        Assert.Equal(2, summary.SourceChunks.Count);
        Assert.Contains("chunk-0", summary.SourceChunks);
        Assert.Contains("chunk-1", summary.SourceChunks);
    }

    [Fact]
    public void DocumentChunk_GeneratesConsistentId()
    {
        // Arrange - DocumentChunk(Order, Heading, HeadingLevel, Content, Hash)
        var chunk1 = new DocumentChunk(0, "Heading", 1, "Content here", "hash123");
        var chunk2 = new DocumentChunk(0, "Heading", 1, "Content here", "hash123");

        // Assert
        Assert.Equal(chunk1.Id, chunk2.Id);
        Assert.Equal("chunk-0", chunk1.Id);
    }

    [Fact]
    public void DocumentChunk_DifferentOrder_DifferentId()
    {
        // Arrange
        var chunk1 = new DocumentChunk(0, "Heading", 1, "Content", "hash1");
        var chunk2 = new DocumentChunk(1, "Heading", 1, "Content", "hash2");

        // Assert
        Assert.NotEqual(chunk1.Id, chunk2.Id);
        Assert.Equal("chunk-0", chunk1.Id);
        Assert.Equal("chunk-1", chunk2.Id);
    }

    [Fact]
    public void SummarizationTrace_CalculatesCorrectly()
    {
        // Arrange
        var topics = new List<string> { "Topic 1", "Topic 2", "Topic 3" };
        var time = TimeSpan.FromSeconds(15.5);

        // Act
        var trace = new SummarizationTrace("doc.pdf", 10, 8, topics, time, 0.85, 1.2);

        // Assert
        Assert.Equal("doc.pdf", trace.DocumentId);
        Assert.Equal(10, trace.TotalChunks);
        Assert.Equal(8, trace.ChunksProcessed);
        Assert.Equal(3, trace.Topics.Count);
        Assert.Equal(15.5, trace.TotalTime.TotalSeconds);
        Assert.Equal(0.85, trace.CoverageScore);
        Assert.Equal(1.2, trace.CitationRate);
    }
}
