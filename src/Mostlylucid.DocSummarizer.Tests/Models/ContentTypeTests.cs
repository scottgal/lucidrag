using Xunit;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Tests.Models;

/// <summary>
/// Tests for ContentType enum values and usage
/// </summary>
public class ContentTypeTests
{
    [Fact]
    public void ContentType_Unknown_HasValue0()
    {
        // Assert
        Assert.Equal(0, (int)ContentType.Unknown);
    }

    [Fact]
    public void ContentType_Narrative_HasValue1()
    {
        // Assert - Narrative is second in enum (index 1)
        Assert.Equal(1, (int)ContentType.Narrative);
    }

    [Fact]
    public void ContentType_Expository_HasValue2()
    {
        // Assert - Expository is third in enum (index 2)
        Assert.Equal(2, (int)ContentType.Expository);
    }

    [Fact]
    public void ContentType_HasThreeValues()
    {
        // Arrange & Act
        var values = Enum.GetValues<ContentType>();

        // Assert - Unknown, Expository, Narrative
        Assert.Equal(3, values.Length);
    }

    [Theory]
    [InlineData(ContentType.Unknown, "Unknown")]
    [InlineData(ContentType.Expository, "Expository")]
    [InlineData(ContentType.Narrative, "Narrative")]
    public void ContentType_ToString_ReturnsExpectedName(ContentType contentType, string expectedName)
    {
        // Act
        var result = contentType.ToString();

        // Assert
        Assert.Equal(expectedName, result);
    }
}
