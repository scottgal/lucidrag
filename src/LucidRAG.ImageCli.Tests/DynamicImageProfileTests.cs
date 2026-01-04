using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using System.Text.Json;

namespace LucidRAG.ImageCli.Tests;

public class DynamicImageProfileTests
{
    [Fact]
    public void AddSignal_ShouldStoreSignal()
    {
        // Arrange
        var profile = new DynamicImageProfile();
        var signal = new Signal
        {
            Key = "test.key",
            Value = "test value",
            Confidence = 0.9,
            Source = "TestSource"
        };

        // Act
        profile.AddSignal(signal);

        // Assert
        var retrieved = profile.GetBestSignal("test.key");
        retrieved.Should().NotBeNull();
        retrieved!.Value.Should().Be("test value");
        retrieved.Confidence.Should().Be(0.9);
    }

    [Fact]
    public void GetValue_WithDirectType_ShouldReturnValue()
    {
        // Arrange
        var profile = new DynamicImageProfile();
        profile.AddSignal(new Signal { Key = "number", Value = 42, Confidence = 1.0, Source = "Test" });

        // Act
        var value = profile.GetValue<int>("number");

        // Assert
        value.Should().Be(42);
    }

    [Fact]
    public void GetValue_WithJsonElement_ShouldDeserialize()
    {
        // Arrange
        var profile = new DynamicImageProfile();
        var jsonElement = JsonSerializer.SerializeToElement(2856.97);
        profile.AddSignal(new Signal { Key = "sharpness", Value = jsonElement, Confidence = 0.8, Source = "Test" });

        // Act
        var value = profile.GetValue<double>("sharpness");

        // Assert
        value.Should().BeApproximately(2856.97, 0.01);
    }

    [Fact]
    public void GetValue_WithNonExistentKey_ShouldReturnDefault()
    {
        // Arrange
        var profile = new DynamicImageProfile();

        // Act
        var value = profile.GetValue<string>("nonexistent");

        // Assert
        value.Should().BeNull();
    }

    [Fact]
    public void GetBestSignal_WithMultipleSignals_ShouldReturnHighestConfidence()
    {
        // Arrange
        var profile = new DynamicImageProfile();
        profile.AddSignal(new Signal { Key = "test", Value = "low", Confidence = 0.5, Source = "Source1" });
        profile.AddSignal(new Signal { Key = "test", Value = "medium", Confidence = 0.7, Source = "Source2" });
        profile.AddSignal(new Signal { Key = "test", Value = "high", Confidence = 0.9, Source = "Source3" });

        // Act
        var best = profile.GetBestSignal("test");

        // Assert
        best.Should().NotBeNull();
        best!.Value.Should().Be("high");
        best.Confidence.Should().Be(0.9);
        best.Source.Should().Be("Source3");
    }

    [Fact]
    public void GetAllSignals_ShouldReturnAllSignals()
    {
        // Arrange
        var profile = new DynamicImageProfile();
        profile.AddSignal(new Signal { Key = "signal1", Value = 1, Confidence = 1.0, Source = "Test" });
        profile.AddSignal(new Signal { Key = "signal2", Value = 2, Confidence = 1.0, Source = "Test" });
        profile.AddSignal(new Signal { Key = "signal3", Value = 3, Confidence = 1.0, Source = "Test" });

        // Act
        var allSignals = profile.GetAllSignals().ToList();

        // Assert
        allSignals.Should().HaveCount(3);
        allSignals.Select(s => s.Key).Should().Contain(new[] { "signal1", "signal2", "signal3" });
    }

    [Fact]
    public void AddSignal_WithTags_ShouldPreserveTags()
    {
        // Arrange
        var profile = new DynamicImageProfile();
        var signal = new Signal
        {
            Key = "tagged",
            Value = "value",
            Confidence = 1.0,
            Source = "Test",
            Tags = new List<string> { "tag1", "tag2", "tag3" }
        };

        // Act
        profile.AddSignal(signal);
        var retrieved = profile.GetBestSignal("tagged");

        // Assert
        retrieved!.Tags.Should().NotBeNull();
        retrieved.Tags.Should().Contain(new[] { "tag1", "tag2", "tag3" });
    }

    [Fact]
    public void AddSignal_WithMetadata_ShouldPreserveMetadata()
    {
        // Arrange
        var profile = new DynamicImageProfile();
        var signal = new Signal
        {
            Key = "meta",
            Value = "value",
            Confidence = 1.0,
            Source = "Test",
            Metadata = new Dictionary<string, object>
            {
                { "escalated", true },
                { "reason", "low_confidence" },
                { "threshold", 0.7 }
            }
        };

        // Act
        profile.AddSignal(signal);
        var retrieved = profile.GetBestSignal("meta");

        // Assert
        retrieved!.Metadata.Should().NotBeNull();
        retrieved.Metadata!["escalated"].Should().Be(true);
        retrieved.Metadata["reason"].Should().Be("low_confidence");
    }
}
