using Mostlylucid.DataSummarizer.Models;
using Mostlylucid.DataSummarizer.Services;

namespace Mostlylucid.DataSummarizer.Tests;

/// <summary>
/// Tests for ProfileStore: caching, drift detection, and segment management
/// </summary>
public class ProfileStoreTests : IDisposable
{
    private readonly string _testStorePath;
    private readonly ProfileStore _store;

    public ProfileStoreTests()
    {
        // Use a unique temp directory for each test run
        _testStorePath = Path.Combine(Path.GetTempPath(), "datasummarizer_test_" + Guid.NewGuid().ToString("N")[..8]);
        _store = new ProfileStore(_testStorePath);
    }

    public void Dispose()
    {
        // Clean up test store
        if (Directory.Exists(_testStorePath))
        {
            try { Directory.Delete(_testStorePath, recursive: true); }
            catch { /* ignore cleanup errors */ }
        }
    }

    #region Basic Store/Load Tests

    [Fact]
    public void Store_ShouldSaveProfileAndGenerateId()
    {
        // Arrange
        var profile = CreateTestProfile("test.csv", 1000, 10);

        // Act
        var info = _store.Store(profile);

        // Assert
        Assert.NotNull(info);
        Assert.NotEmpty(info.Id);
        Assert.Equal(12, info.Id.Length); // 12 hex chars
        Assert.Equal("test.csv", info.FileName);
        Assert.Equal(1000, info.RowCount);
        Assert.Equal(10, info.ColumnCount);
    }

    [Fact]
    public void Store_ShouldComputeSchemaHash()
    {
        // Arrange
        var profile1 = CreateTestProfile("a.csv", 1000, 5);
        var profile2 = CreateTestProfile("b.csv", 2000, 5); // Same schema, different data

        // Act
        var info1 = _store.Store(profile1);
        var info2 = _store.Store(profile2);

        // Assert
        Assert.NotEmpty(info1.SchemaHash);
        Assert.Equal(info1.SchemaHash, info2.SchemaHash); // Same columns = same schema hash
    }

    [Fact]
    public void Store_DifferentSchemas_ShouldHaveDifferentHashes()
    {
        // Arrange
        var profile1 = CreateTestProfile("a.csv", 1000, 5);
        var profile2 = CreateTestProfile("b.csv", 1000, 7); // Different column count

        // Act
        var info1 = _store.Store(profile1);
        var info2 = _store.Store(profile2);

        // Assert
        Assert.NotEqual(info1.SchemaHash, info2.SchemaHash);
    }

    [Fact]
    public void LoadProfile_ShouldReturnStoredProfile()
    {
        // Arrange
        var original = CreateTestProfile("test.csv", 1000, 10);
        var info = _store.Store(original);

        // Act
        var loaded = _store.LoadProfile(info.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(original.SourcePath, loaded.SourcePath);
        Assert.Equal(original.RowCount, loaded.RowCount);
        Assert.Equal(original.Columns.Count, loaded.Columns.Count);
    }

    [Fact]
    public void LoadProfile_NonExistent_ShouldReturnNull()
    {
        // Act
        var loaded = _store.LoadProfile("nonexistent123");

        // Assert
        Assert.Null(loaded);
    }

    #endregion

    #region Schema Matching Tests

    [Fact]
    public void FindBySchema_ShouldFindProfilesWithSameSchema()
    {
        // Arrange
        var profile1 = CreateTestProfile("a.csv", 1000, 5);
        var profile2 = CreateTestProfile("b.csv", 2000, 5);
        var profile3 = CreateTestProfile("c.csv", 3000, 7); // Different schema
        
        _store.Store(profile1);
        _store.Store(profile2);
        _store.Store(profile3);

        // Act
        var matches = _store.FindBySchema(profile1);

        // Assert
        Assert.Equal(2, matches.Count); // profile1 and profile2
    }

    [Fact]
    public void GetHistory_ShouldReturnProfilesInChronologicalOrder()
    {
        // Arrange
        var profile1 = CreateTestProfile("a.csv", 1000, 5);
        var info1 = _store.Store(profile1);
        
        Thread.Sleep(10); // Ensure different timestamps
        
        var profile2 = CreateTestProfile("b.csv", 1500, 5);
        var info2 = _store.Store(profile2);
        
        Thread.Sleep(10);
        
        var profile3 = CreateTestProfile("c.csv", 2000, 5);
        var info3 = _store.Store(profile3);

        // Act
        var history = _store.GetHistory(info1.SchemaHash, limit: 10);

        // Assert
        Assert.Equal(3, history.Count);
        Assert.Equal(info3.Id, history[0].Id); // Most recent first
        Assert.Equal(info1.Id, history[2].Id); // Oldest last
    }

    #endregion

    #region Baseline/Drift Tests

    [Fact]
    public void LoadBaseline_ShouldReturnOldestProfileWithSameSchema()
    {
        // Arrange - store profiles with same schema at different times
        var profile1 = CreateTestProfile("jan.csv", 1000, 5);
        var info1 = _store.Store(profile1);
        
        Thread.Sleep(10);
        
        var profile2 = CreateTestProfile("feb.csv", 1100, 5);
        var info2 = _store.Store(profile2);
        
        Thread.Sleep(10);
        
        var profile3 = CreateTestProfile("mar.csv", 1200, 5);
        var info3 = _store.Store(profile3);

        // Act
        var baseline = _store.LoadBaseline(profile3);

        // Assert
        Assert.NotNull(baseline);
        Assert.Equal(profile1.SourcePath, baseline.SourcePath); // First stored is baseline
    }

    [Fact]
    public void LoadBaseline_NewSchema_ShouldReturnNull()
    {
        // Arrange
        var existingProfile = CreateTestProfile("existing.csv", 1000, 5);
        _store.Store(existingProfile);
        
        var newProfile = CreateTestProfile("new.csv", 1000, 8); // Different schema

        // Act
        var baseline = _store.LoadBaseline(newProfile);

        // Assert
        Assert.Null(baseline);
    }

    #endregion

    #region Similarity Matching Tests

    [Fact]
    public void FindSimilar_ShouldFindProfilesAboveThreshold()
    {
        // Arrange
        var profile1 = CreateTestProfile("a.csv", 1000, 5);
        var profile2 = CreateTestProfile("b.csv", 1100, 5); // Very similar
        var profile3 = CreateTestProfile("c.csv", 100, 20); // Very different
        
        _store.Store(profile1);
        _store.Store(profile2);
        _store.Store(profile3);

        // Act
        var similar = _store.FindSimilar(profile1, minSimilarity: 0.7);

        // Assert
        Assert.True(similar.Count >= 1); // Should find at least profile1 and profile2
        Assert.True(similar.All(s => s.Similarity >= 0.7));
    }

    [Fact]
    public void FindMostSimilarByCentroid_ShouldReturnClosestProfile()
    {
        // Arrange - profiles with different numeric characteristics
        var profile1 = CreateTestProfileWithStats("a.csv", 1000, mean: 100, stdDev: 20);
        var profile2 = CreateTestProfileWithStats("b.csv", 1000, mean: 105, stdDev: 22); // Very close to profile1
        var profile3 = CreateTestProfileWithStats("c.csv", 1000, mean: 500, stdDev: 100); // Far from profile1
        
        _store.Store(profile1);
        _store.Store(profile2);
        _store.Store(profile3);
        
        var query = CreateTestProfileWithStats("query.csv", 1000, mean: 102, stdDev: 21);

        // Act
        var (closest, distance) = _store.FindMostSimilarByCentroid(query);

        // Assert
        Assert.NotNull(closest);
        // Should find profile1 or profile2 (both are close)
        Assert.True(closest.FileName == "a.csv" || closest.FileName == "b.csv");
    }

    #endregion

    #region Segment Tests

    [Fact]
    public void StoreSegment_ShouldStoreWithSegmentMetadata()
    {
        // Arrange
        var profile = CreateTestProfile("data.csv", 500, 5);

        // Act
        var info = _store.StoreSegment(profile, "Q1-2024", filter: "quarter = 1 AND year = 2024");

        // Assert
        Assert.Equal("Q1-2024", info.SegmentName);
        Assert.Equal("quarter = 1 AND year = 2024", info.SegmentFilter);
        Assert.NotEmpty(info.SegmentGroup!);
    }

    [Fact]
    public void GetSegmentGroup_ShouldReturnAllSegmentsInGroup()
    {
        // Arrange
        var profile1 = CreateTestProfile("data.csv", 500, 5);
        var profile2 = CreateTestProfile("data.csv", 600, 5);
        var profile3 = CreateTestProfile("data.csv", 700, 5);
        
        var groupId = "quarterly_segments";
        _store.Store(profile1, segment: new SegmentInfo { Name = "Q1", Group = groupId });
        _store.Store(profile2, segment: new SegmentInfo { Name = "Q2", Group = groupId });
        _store.Store(profile3, segment: new SegmentInfo { Name = "Q3", Group = groupId });

        // Act
        var segments = _store.GetSegmentGroup(groupId);

        // Assert
        Assert.Equal(3, segments.Count);
        Assert.Contains(segments, s => s.SegmentName == "Q1");
        Assert.Contains(segments, s => s.SegmentName == "Q2");
        Assert.Contains(segments, s => s.SegmentName == "Q3");
    }

    #endregion

    #region Delete/Prune Tests

    [Fact]
    public void Delete_ShouldRemoveProfile()
    {
        // Arrange
        var profile = CreateTestProfile("test.csv", 1000, 5);
        var info = _store.Store(profile);

        // Act
        var deleted = _store.Delete(info.Id);
        var loaded = _store.LoadProfile(info.Id);

        // Assert
        Assert.True(deleted);
        Assert.Null(loaded);
    }

    [Fact]
    public void Prune_ShouldKeepOnlyMostRecentPerSchema()
    {
        // Arrange - store 5 profiles with same schema
        for (int i = 0; i < 5; i++)
        {
            var profile = CreateTestProfile($"v{i}.csv", 1000 + i * 100, 5);
            _store.Store(profile);
            Thread.Sleep(10); // Ensure different timestamps
        }

        // Act - keep only 2 per schema
        var pruned = _store.Prune(keepPerSchema: 2);

        // Assert
        Assert.Equal(3, pruned); // 5 - 2 = 3 pruned
        Assert.Equal(2, _store.ListAll().Count);
    }

    [Fact]
    public void ClearAll_ShouldRemoveAllProfiles()
    {
        // Arrange
        _store.Store(CreateTestProfile("a.csv", 100, 5));
        _store.Store(CreateTestProfile("b.csv", 200, 5));
        _store.Store(CreateTestProfile("c.csv", 300, 5));

        // Act
        var cleared = _store.ClearAll();

        // Assert
        Assert.Equal(3, cleared);
        Assert.Empty(_store.ListAll());
    }

    #endregion

    #region Stats Tests

    [Fact]
    public void GetStats_ShouldReturnCorrectStatistics()
    {
        // Arrange
        _store.Store(CreateTestProfile("a.csv", 100, 5));
        _store.Store(CreateTestProfile("b.csv", 200, 5)); // Same schema
        _store.Store(CreateTestProfile("c.csv", 300, 8)); // Different schema

        // Act
        var stats = _store.GetStats();

        // Assert
        Assert.Equal(3, stats.TotalProfiles);
        Assert.Equal(2, stats.UniqueSchemas);
        Assert.Equal(_testStorePath, stats.StorePath);
    }

    #endregion

    #region Hash Computation Tests

    [Fact]
    public void ComputeSchemaHash_SameColumns_ShouldBeDeterministic()
    {
        // Arrange
        var profile1 = CreateTestProfile("a.csv", 1000, 5);
        var profile2 = CreateTestProfile("b.csv", 2000, 5);

        // Act
        var hash1 = ProfileStore.ComputeSchemaHash(profile1);
        var hash2 = ProfileStore.ComputeSchemaHash(profile2);

        // Assert
        Assert.Equal(hash1, hash2);
        Assert.Equal(16, hash1.Length); // 64-bit hash = 16 hex chars
    }

    [Fact]
    public void ComputeSchemaHash_ShouldBeOrderIndependent()
    {
        // Arrange
        var profile1 = new DataProfile
        {
            SourcePath = "test.csv",
            RowCount = 100,
            Columns =
            [
                new ColumnProfile { Name = "A", InferredType = ColumnType.Numeric },
                new ColumnProfile { Name = "B", InferredType = ColumnType.Text }
            ]
        };
        
        var profile2 = new DataProfile
        {
            SourcePath = "test.csv",
            RowCount = 100,
            Columns =
            [
                new ColumnProfile { Name = "B", InferredType = ColumnType.Text },
                new ColumnProfile { Name = "A", InferredType = ColumnType.Numeric }
            ]
        };

        // Act
        var hash1 = ProfileStore.ComputeSchemaHash(profile1);
        var hash2 = ProfileStore.ComputeSchemaHash(profile2);

        // Assert
        Assert.Equal(hash1, hash2); // Order shouldn't matter
    }

    [Fact]
    public void ComputeDatabaseHash_ShouldIncludeStats()
    {
        // Arrange
        var profile1 = CreateTestProfileWithStats("db://table1", 1000, mean: 100, stdDev: 20);
        var profile2 = CreateTestProfileWithStats("db://table2", 1000, mean: 200, stdDev: 40); // Different stats

        // Act
        var hash1 = ProfileStore.ComputeDatabaseHash(profile1);
        var hash2 = ProfileStore.ComputeDatabaseHash(profile2);

        // Assert
        Assert.StartsWith("db:", hash1);
        Assert.StartsWith("db:", hash2);
        Assert.NotEqual(hash1, hash2); // Different stats = different hash
    }

    #endregion

    #region Centroid Tests

    [Fact]
    public void Store_ShouldComputeCentroidVector()
    {
        // Arrange
        var profile = CreateTestProfileWithStats("test.csv", 1000, mean: 50, stdDev: 10);

        // Act
        var info = _store.Store(profile);

        // Assert
        Assert.NotNull(info.CentroidVector);
        Assert.True(info.CentroidVector.Length > 0);
    }

    [Fact]
    public void FindWithinDistance_ShouldReturnProfilesWithinThreshold()
    {
        // Arrange
        var baseProfile = CreateTestProfileWithStats("base.csv", 1000, mean: 100, stdDev: 20);
        var closeProfile = CreateTestProfileWithStats("close.csv", 1000, mean: 105, stdDev: 22);
        var farProfile = CreateTestProfileWithStats("far.csv", 1000, mean: 500, stdDev: 100);
        
        _store.Store(baseProfile);
        _store.Store(closeProfile);
        _store.Store(farProfile);

        // Act
        var nearby = _store.FindWithinDistance(baseProfile, maxDistance: 0.5);

        // Assert
        Assert.True(nearby.Count >= 1); // At least baseProfile itself should match
        Assert.All(nearby, r => Assert.True(r.Distance <= 0.5));
    }

    #endregion

    #region Helper Methods

    private static DataProfile CreateTestProfile(string fileName, long rowCount, int columnCount)
    {
        var columns = new List<ColumnProfile>();
        for (int i = 0; i < columnCount; i++)
        {
            columns.Add(new ColumnProfile
            {
                Name = $"Column{i}",
                DuckDbType = i < columnCount / 2 ? "DOUBLE" : "VARCHAR",
                InferredType = i < columnCount / 2 ? ColumnType.Numeric : ColumnType.Text,
                Count = rowCount,
                UniqueCount = Math.Min(rowCount, 100 * (i + 1))
            });
        }

        return new DataProfile
        {
            SourcePath = fileName,
            RowCount = rowCount,
            Columns = columns
        };
    }

    private static DataProfile CreateTestProfileWithStats(string fileName, long rowCount, double mean, double stdDev)
    {
        return new DataProfile
        {
            SourcePath = fileName,
            RowCount = rowCount,
            Columns =
            [
                new ColumnProfile
                {
                    Name = "Value",
                    DuckDbType = "DOUBLE",
                    InferredType = ColumnType.Numeric,
                    Count = rowCount,
                    UniqueCount = rowCount / 2,
                    Mean = mean,
                    StdDev = stdDev,
                    Min = mean - 3 * stdDev,
                    Max = mean + 3 * stdDev
                },
                new ColumnProfile
                {
                    Name = "Category",
                    DuckDbType = "VARCHAR",
                    InferredType = ColumnType.Categorical,
                    Count = rowCount,
                    UniqueCount = 10
                }
            ]
        };
    }

    #endregion
}
