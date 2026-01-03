using Xunit;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Tests.Models;

public class CitationValidatorTests
{
    [Fact]
    public void Validate_WithValidCitations_ReturnsValid()
    {
        // Arrange
        var summary = "This is a summary [chunk-0] with valid citations [chunk-1].";
        var validIds = new HashSet<string> { "chunk-0", "chunk-1", "chunk-2" };

        // Act
        var result = CitationValidator.Validate(summary, validIds);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(2, result.TotalCitations);
        Assert.Equal(0, result.InvalidCount);
    }

    [Fact]
    public void Validate_WithInvalidCitations_ReturnsInvalid()
    {
        // Arrange
        var summary = "This has [chunk-0] and [chunk-99] citations.";
        var validIds = new HashSet<string> { "chunk-0", "chunk-1", "chunk-2" };

        // Act
        var result = CitationValidator.Validate(summary, validIds);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(2, result.TotalCitations);
        Assert.Equal(1, result.InvalidCount);
        Assert.Contains("chunk-99", result.InvalidCitations);
    }

    [Fact]
    public void Validate_WithNoCitations_ReturnsInvalid()
    {
        // Arrange
        var summary = "This summary has no citations.";
        var validIds = new HashSet<string> { "chunk-0", "chunk-1" };

        // Act
        var result = CitationValidator.Validate(summary, validIds);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(0, result.TotalCitations);
        Assert.Equal(0, result.InvalidCount);
    }

    [Fact]
    public void Validate_WithMixedCitations_ReturnsCorrectCounts()
    {
        // Arrange
        var summary = "Summary with [chunk-0], [chunk-1], and [chunk-99].";
        var validIds = new HashSet<string> { "chunk-0", "chunk-1" };

        // Act
        var result = CitationValidator.Validate(summary, validIds);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(3, result.TotalCitations);
        Assert.Equal(1, result.InvalidCount);
        Assert.Contains("chunk-99", result.InvalidCitations);
    }

    [Fact]
    public void Validate_WithNonChunkCitations_IgnoresThem()
    {
        // Arrange
        var summary = "Summary with [ref-1] and [chunk-0].";
        var validIds = new HashSet<string> { "chunk-0", "chunk-1" };

        // Act
        var result = CitationValidator.Validate(summary, validIds);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(1, result.TotalCitations);
        Assert.Equal(0, result.InvalidCount);
    }

    [Fact]
    public void Validate_WithEmptySummary_ReturnsInvalid()
    {
        // Arrange
        var summary = "";
        var validIds = new HashSet<string> { "chunk-0" };

        // Act
        var result = CitationValidator.Validate(summary, validIds);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(0, result.TotalCitations);
        Assert.Equal(0, result.InvalidCount);
    }
}