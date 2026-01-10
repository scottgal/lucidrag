using Xunit;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Tests.Models;

public class HashHelperTests
{
    [Fact]
    public void ComputeHash_WithSameContent_ReturnsSameHash()
    {
        // Arrange
        var content1 = "This is test content";
        var content2 = "This is test content";

        // Act
        var hash1 = HashHelper.ComputeHash(content1);
        var hash2 = HashHelper.ComputeHash(content2);

        // Assert
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_WithDifferentContent_ReturnsDifferentHashes()
    {
        // Arrange
        var content1 = "This is test content";
        var content2 = "This is different content";

        // Act
        var hash1 = HashHelper.ComputeHash(content1);
        var hash2 = HashHelper.ComputeHash(content2);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_WithEmptyContent_ReturnsEmptyString()
    {
        // Arrange
        var content = "";

        // Act
        var hash = HashHelper.ComputeHash(content);

        // Assert
        Assert.NotNull(hash);
        Assert.Empty(hash); // ContentHasher returns empty string for empty content
    }

    [Fact]
    public void ComputeHash_WithSpecialCharacters_ReturnsValidHash()
    {
        // Arrange
        var content = "Special chars: !@#$%^&*()_+-=[]{}|;':\",./<>?";

        // Act
        var hash = HashHelper.ComputeHash(content);

        // Assert
        Assert.NotNull(hash);
        Assert.Equal(16, hash.Length);
    }

    [Fact]
    public void ComputeHash_WithLongContent_ReturnsConsistentHash()
    {
        // Arrange
        var content = new string('a', 1000);

        // Act
        var hash = HashHelper.ComputeHash(content);

        // Assert
        Assert.NotNull(hash);
        Assert.Equal(16, hash.Length);
    }
}