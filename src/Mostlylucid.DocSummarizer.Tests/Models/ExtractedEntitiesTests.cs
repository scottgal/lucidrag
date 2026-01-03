using Xunit;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Tests.Models;

/// <summary>
/// Tests for ExtractedEntities record
/// </summary>
public class ExtractedEntitiesTests
{
    [Fact]
    public void ExtractedEntities_Empty_ReturnsEmptyLists()
    {
        // Act
        var entities = ExtractedEntities.Empty;

        // Assert
        Assert.NotNull(entities);
        Assert.Empty(entities.Characters);
        Assert.Empty(entities.Locations);
        Assert.Empty(entities.Dates);
        Assert.Empty(entities.Organizations);
        Assert.Empty(entities.Events);
    }

    [Fact]
    public void ExtractedEntities_Empty_CreatesEmptyInstances()
    {
        // Arrange & Act
        var empty1 = ExtractedEntities.Empty;
        var empty2 = ExtractedEntities.Empty;

        // Assert - both are empty (not necessarily same reference)
        Assert.Empty(empty1.Characters);
        Assert.Empty(empty2.Characters);
        Assert.Empty(empty1.Locations);
        Assert.Empty(empty2.Locations);
    }

    [Fact]
    public void ExtractedEntities_WithData_StoresCorrectly()
    {
        // Arrange
        var characters = new List<string> { "Holmes", "Watson" };
        var locations = new List<string> { "London", "Baker Street" };
        var dates = new List<string> { "1891" };
        var organizations = new List<string> { "Scotland Yard" };
        var events = new List<string> { "murder" };

        // Act
        var entities = new ExtractedEntities(characters, locations, dates, events, organizations);

        // Assert
        Assert.Equal(2, entities.Characters.Count);
        Assert.Contains("Holmes", entities.Characters);
        Assert.Contains("Watson", entities.Characters);
        Assert.Equal(2, entities.Locations.Count);
        Assert.Single(entities.Dates);
        Assert.Single(entities.Events);
        Assert.Single(entities.Organizations);
    }

    [Fact]
    public void ExtractedEntities_IsEmpty_ReturnsTrueForEmpty()
    {
        // Arrange
        var entities = ExtractedEntities.Empty;

        // Act
        var isEmpty = entities.Characters.Count == 0 
                   && entities.Locations.Count == 0 
                   && entities.Dates.Count == 0;

        // Assert
        Assert.True(isEmpty);
    }

    [Fact]
    public void ExtractedEntities_IsEmpty_ReturnsFalseForNonEmpty()
    {
        // Arrange
        var entities = new ExtractedEntities(
            Characters: new List<string> { "Character" },
            Locations: new List<string>(),
            Dates: new List<string>(),
            Events: new List<string>(),
            Organizations: new List<string>());

        // Act
        var isEmpty = entities.Characters.Count == 0;

        // Assert
        Assert.False(isEmpty);
    }
}
