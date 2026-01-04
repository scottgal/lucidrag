using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Storage;

namespace LucidRAG.ImageCli.Tests;

public class SignalDatabaseTests : IDisposable
{
    private readonly SignalDatabase _database;
    private readonly string _testDbPath;

    public SignalDatabaseTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _database = new SignalDatabase(_testDbPath, NullLogger<SignalDatabase>.Instance);
    }

    [Fact]
    public async Task StoreProfile_ShouldPersistSignals()
    {
        // Arrange
        var profile = new DynamicImageProfile
        {
            ImagePath = "test.jpg"
        };

        profile.AddSignal(new Signal
        {
            Key = "quality.sharpness",
            Value = 2856.97,
            Confidence = 0.8,
            Source = "ImageAnalyzer",
            Tags = new List<string> { "quality" }
        });

        var sha256 = "test_hash_123";

        // Act
        var imageId = await _database.StoreProfileAsync(profile, sha256, "test.jpg", 100, 80, "PNG");

        // Assert
        imageId.Should().BeGreaterThan(0);

        var loaded = await _database.LoadProfileAsync(sha256);
        loaded.Should().NotBeNull();
        loaded!.GetValue<double>("quality.sharpness").Should().BeApproximately(2856.97, 0.01);
    }

    [Fact]
    public async Task LoadProfile_WithNonExistentHash_ShouldReturnNull()
    {
        // Act
        var loaded = await _database.LoadProfileAsync("nonexistent_hash");

        // Assert
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task StoreProfile_WithMultipleSignals_ShouldLoadAll()
    {
        // Arrange
        var profile = new DynamicImageProfile();
        profile.AddSignal(new Signal { Key = "signal1", Value = "value1", Confidence = 0.9, Source = "Test" });
        profile.AddSignal(new Signal { Key = "signal2", Value = 42, Confidence = 0.8, Source = "Test" });
        profile.AddSignal(new Signal { Key = "signal3", Value = true, Confidence = 1.0, Source = "Test" });

        var sha256 = "multi_signal_hash";

        // Act
        await _database.StoreProfileAsync(profile, sha256);
        var loaded = await _database.LoadProfileAsync(sha256);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.GetValue<string>("signal1").Should().Be("value1");
        loaded.GetValue<int>("signal2").Should().Be(42);
        loaded.GetValue<bool>("signal3").Should().BeTrue();
    }

    [Fact]
    public async Task GetStatistics_ShouldReturnCorrectCounts()
    {
        // Arrange
        var profile = new DynamicImageProfile();
        profile.AddSignal(new Signal { Key = "test", Value = "value", Confidence = 1.0, Source = "Source1" });

        await _database.StoreProfileAsync(profile, "hash1");
        await _database.StoreProfileAsync(profile, "hash2");

        // Act
        var stats = await _database.GetStatisticsAsync();

        // Assert
        stats.ImageCount.Should().Be(2);
        stats.SignalCount.Should().BeGreaterOrEqualTo(2);
        stats.UniqueSourceCount.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task ConcurrentAccess_ShouldNotThrow()
    {
        // Arrange
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var profile = new DynamicImageProfile();
            profile.AddSignal(new Signal { Key = $"test{i}", Value = i, Confidence = 1.0, Source = "Test" });
            await _database.StoreProfileAsync(profile, $"hash{i}");
        });

        // Act & Assert
        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();
    }

    public void Dispose()
    {
        _database.Dispose();

        // Force garbage collection to release SQLite file handles
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }

            // Also clean up WAL and SHM files
            var walPath = _testDbPath + "-wal";
            var shmPath = _testDbPath + "-shm";
            if (File.Exists(walPath)) File.Delete(walPath);
            if (File.Exists(shmPath)) File.Delete(shmPath);
        }
        catch (IOException)
        {
            // Ignore cleanup errors in tests
        }
    }
}
