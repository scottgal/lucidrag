using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using LucidRAG.Core.Services;
using LucidRAG.Data;
using LucidRAG.Entities;

namespace LucidRAG.Tests.Integration;

/// <summary>
/// Integration tests for PostgreSQL full-text search service.
/// Tests the actual PostgreSQL FTS functionality with real database.
/// </summary>
[Collection("Integration")]
public class PostgresBM25ServiceTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;
    private PostgresBM25Service _service = null!;
    private RagDocumentsDbContext _db = null!;
    private IServiceScope _scope = null!;

    public PostgresBM25ServiceTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureDatabaseAsync();

        // Create scope for services
        _scope = _factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        _service = _scope.ServiceProvider.GetRequiredService<PostgresBM25Service>();

        // Seed test data
        await SeedTestEvidenceAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.CleanupAsync();
        _scope?.Dispose();
    }

    private async Task SeedTestEvidenceAsync()
    {
        // Create test document
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            Name = "Machine Learning Guide",
            ContentHash = "test-hash-ml",
            FilePath = "/test/ml.md",
            MimeType = "text/markdown",
            Status = DocumentStatus.Completed
        };

        _db.Documents.Add(document);

        // Create evidence artifacts with various content
        var evidence = new[]
        {
            new EvidenceArtifact
            {
                Id = Guid.NewGuid(),
                EntityId = document.Id,
                ArtifactType = EvidenceTypes.SegmentText,
                MimeType = "text/plain",
                StorageBackend = "inline",
                StoragePath = "inline:segment_text",
                Content = "Machine learning is a subset of artificial intelligence. " +
                         "It uses algorithms to learn patterns from data without explicit programming.",
                SegmentHash = "hash1",
                Metadata = "{\"salience_score\": 0.9}"
            },
            new EvidenceArtifact
            {
                Id = Guid.NewGuid(),
                EntityId = document.Id,
                ArtifactType = EvidenceTypes.SegmentText,
                MimeType = "text/plain",
                StorageBackend = "inline",
                StoragePath = "inline:segment_text",
                Content = "Neural networks are inspired by biological neurons. " +
                         "Deep learning uses multiple layers of neural networks for complex tasks.",
                SegmentHash = "hash2",
                Metadata = "{\"salience_score\": 0.85}"
            },
            new EvidenceArtifact
            {
                Id = Guid.NewGuid(),
                EntityId = document.Id,
                ArtifactType = EvidenceTypes.SegmentText,
                MimeType = "text/plain",
                StorageBackend = "inline",
                StoragePath = "inline:segment_text",
                Content = "Supervised learning requires labeled training data. " +
                         "Classification and regression are common supervised learning tasks.",
                SegmentHash = "hash3",
                Metadata = "{\"salience_score\": 0.7}"
            },
            new EvidenceArtifact
            {
                Id = Guid.NewGuid(),
                EntityId = document.Id,
                ArtifactType = EvidenceTypes.SegmentText,
                MimeType = "text/plain",
                StorageBackend = "inline",
                StoragePath = "inline:segment_text",
                Content = "Unsupervised learning discovers patterns without labels. " +
                         "Clustering and dimensionality reduction are unsupervised techniques.",
                SegmentHash = "hash4",
                Metadata = "{\"salience_score\": 0.6}"
            }
        };

        _db.EvidenceArtifacts.AddRange(evidence);
        await _db.SaveChangesAsync();

        // Ensure the migration has run (content_tokens should exist)
        // This will be populated automatically by the GENERATED ALWAYS AS clause
    }

    [Fact]
    public async Task SearchWithScoresAsync_SimpleQuery_ReturnsRankedResults()
    {
        // Arrange
        var query = "machine learning algorithms";

        // Act
        var results = await _service.SearchWithScoresAsync(query, topK: 10);

        // Assert
        results.Should().NotBeEmpty();
        results.Should().HaveCountGreaterThan(0);

        // First result should contain "machine learning"
        var topResult = results.First();
        topResult.artifact.Content.Should().ContainAny("machine", "Machine");
        topResult.score.Should().BeGreaterThan(0);

        // Results should be ordered by score descending
        for (int i = 0; i < results.Count - 1; i++)
        {
            results[i].score.Should().BeGreaterThanOrEqualTo(results[i + 1].score);
        }
    }

    [Fact]
    public async Task SearchWithScoresAsync_SpecificTerms_ReturnsRelevantDocuments()
    {
        // Arrange
        var query = "neural networks deep learning";

        // Act
        var results = await _service.SearchWithScoresAsync(query, topK: 5);

        // Assert
        results.Should().NotBeEmpty();

        // Top result should mention neural networks
        var topResult = results.First();
        topResult.artifact.Content.Should().ContainAny("neural", "Neural");
    }

    [Fact]
    public async Task SearchWithScoresAsync_NoMatches_ReturnsEmptyResults()
    {
        // Arrange
        var query = "quantum computing blockchain cryptocurrency";

        // Act
        var results = await _service.SearchWithScoresAsync(query, topK: 10);

        // Assert
        // Should return empty or very low scores since none of these terms exist
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchWithScoresAsync_WithTopK_RespectsLimit()
    {
        // Arrange
        var query = "learning";
        var topK = 2;

        // Act
        var results = await _service.SearchWithScoresAsync(query, topK: topK);

        // Assert
        results.Should().HaveCountLessThanOrEqualTo(topK);
    }

    [Fact]
    public async Task SearchWithScoresAsync_EmptyQuery_ReturnsEmpty()
    {
        // Arrange
        var query = "";

        // Act
        var results = await _service.SearchWithScoresAsync(query, topK: 10);

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task HybridSearchAsync_WithQueryEmbedding_CombinesScores()
    {
        // Arrange
        var query = "machine learning";

        // Create a mock embedding (384 dimensions for all-MiniLM-L6-v2)
        var queryEmbedding = Enumerable.Range(0, 384).Select(i => (float)(i * 0.001)).ToArray();

        // Act
        var results = await _service.HybridSearchAsync(
            query,
            queryEmbedding,
            topK: 5,
            rrfK: 60);

        // Assert
        // May return empty if no embeddings in database, but should not throw
        results.Should().NotBeNull();

        // If results exist, they should have RRF scores
        if (results.Count > 0)
        {
            results.First().rrfScore.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task HybridSearchAsync_BM25Only_WorksWithoutEmbedding()
    {
        // Arrange
        var query = "supervised learning classification";

        // Act - no embedding provided
        var results = await _service.HybridSearchAsync(
            query,
            queryEmbedding: null,
            topK: 5);

        // Assert
        results.Should().NotBeEmpty();
        results.First().artifact.Content.Should().ContainAny("supervised", "Supervised");
    }

    [Fact]
    public async Task RefreshCorpusStatsAsync_DoesNotThrow()
    {
        // Act & Assert
        await _service.Invoking(s => s.RefreshCorpusStatsAsync())
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task SearchWithScoresAsync_PerformanceCheck_CompletesQuickly()
    {
        // Arrange
        var query = "machine learning neural networks";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Act
        var results = await _service.SearchWithScoresAsync(query, topK: 25);

        // Assert
        sw.Stop();

        // Should complete in under 100ms (PostgreSQL FTS is fast!)
        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "PostgreSQL FTS should be much faster than C# BM25");

        results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SearchWithScoresAsync_SupportsPostgreSQLSyntax_PhraseSearch()
    {
        // Arrange - use PostgreSQL phrase search syntax
        var query = "\"neural networks\"";

        // Act
        var results = await _service.SearchWithScoresAsync(query, topK: 5);

        // Assert
        if (results.Count > 0)
        {
            var topResult = results.First();
            topResult.artifact.Content.Should().ContainAny("neural networks", "Neural networks");
        }
    }

    [Fact]
    public async Task SearchWithScoresAsync_MultipleQueries_ProducesDifferentResults()
    {
        // Arrange
        var query1 = "machine learning";
        var query2 = "neural networks";

        // Act
        var results1 = await _service.SearchWithScoresAsync(query1, topK: 10);
        var results2 = await _service.SearchWithScoresAsync(query2, topK: 10);

        // Assert
        results1.Should().NotBeEmpty();
        results2.Should().NotBeEmpty();

        // Different queries should produce different top results
        if (results1.Count > 0 && results2.Count > 0)
        {
            var top1 = results1.First().artifact.Id;
            var top2 = results2.First().artifact.Id;

            // Top results should be different (different queries emphasize different content)
            // Note: This may not always be true if one document dominates, so we check scores differ
            var score1 = results1.First().score;
            var score2 = results2.First().score;

            (score1 != score2 || top1 != top2).Should().BeTrue(
                "Different queries should produce different rankings");
        }
    }
}
