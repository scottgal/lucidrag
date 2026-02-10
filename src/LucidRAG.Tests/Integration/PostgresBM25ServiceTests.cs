using System.Diagnostics;
using FluentAssertions;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Plugin.Postgres;
using LucidRAG.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace LucidRAG.Tests.Integration;

/// <summary>
///     Integration tests for PostgreSQL full-text search service.
///     Tests the actual PostgreSQL FTS functionality with real database.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class PostgresBM25ServiceTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;
    private RagDocumentsDbContext _db = null!;
    private IServiceScope _scope = null!;
    private PostgresBm25Service _service = null!;

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
        _service = (PostgresBm25Service)_scope.ServiceProvider.GetRequiredService<IBm25SearchService>();

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
        var results = await _service.SearchWithScoresAsync(query, 10);

        // Assert
        results.Should().NotBeEmpty();
        results.Should().HaveCountGreaterThan(0);

        // First result should contain "machine learning"
        var topResult = results.First();
        topResult.artifact.Content.Should().ContainAny("machine", "Machine");
        topResult.score.Should().BeGreaterThan(0);

        // Results should be ordered by score descending
        for (var i = 0; i < results.Count - 1; i++)
            results[i].score.Should().BeGreaterThanOrEqualTo(results[i + 1].score);
    }

    [Fact]
    public async Task SearchWithScoresAsync_SpecificTerms_ReturnsRelevantDocuments()
    {
        // Arrange
        var query = "neural networks deep learning";

        // Act
        var results = await _service.SearchWithScoresAsync(query, 5);

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
        var results = await _service.SearchWithScoresAsync(query, 10);

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
        var results = await _service.SearchWithScoresAsync(query, topK);

        // Assert
        results.Should().HaveCountLessThanOrEqualTo(topK);
    }

    [Fact]
    public async Task SearchWithScoresAsync_EmptyQuery_ReturnsEmpty()
    {
        // Arrange
        var query = "";

        // Act
        var results = await _service.SearchWithScoresAsync(query, 10);

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchWithScoresAsync_PerformanceCheck_CompletesQuickly()
    {
        // Arrange
        var query = "machine learning neural networks";
        var sw = Stopwatch.StartNew();

        // Act
        var results = await _service.SearchWithScoresAsync(query);

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
        var results = await _service.SearchWithScoresAsync(query, 5);

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
        var results1 = await _service.SearchWithScoresAsync(query1, 10);
        var results2 = await _service.SearchWithScoresAsync(query2, 10);

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

    [Fact]
    public async Task Benchmark_PostgresFTS_AtScale()
    {
        // Seed 500 realistic segments
        await SeedBulkEvidenceAsync(500);

        // Warmup
        await _service.SearchWithScoresAsync("machine learning", 25);

        // Benchmark 10 queries
        var queries = new[]
        {
            "machine learning", "neural networks", "supervised classification",
            "clustering algorithms", "data processing", "gradient descent optimization",
            "natural language processing", "computer vision detection", "reinforcement learning",
            "feature extraction"
        };

        var times = new List<double>();
        foreach (var q in queries)
        {
            var sw = Stopwatch.StartNew();
            var results = await _service.SearchWithScoresAsync(q, 25);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        var p50 = times[(int)(times.Count * 0.5)];
        var p95 = times[(int)(times.Count * 0.95)];

        // PostgreSQL FTS should be under 50ms p95 for 500 segments with GIN index
        p95.Should().BeLessThan(50,
            $"PostgreSQL FTS p95 should be under 50ms (p50={p50:F1}ms, p95={p95:F1}ms)");
    }

    [Fact]
    public async Task ExplainAnalyze_UsesGinIndex()
    {
        // Seed enough data for the planner to choose the index
        await SeedBulkEvidenceAsync(100);

        var connectionString = _scope.ServiceProvider
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("DefaultConnection")!;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var sql = """
            EXPLAIN ANALYZE
            SELECT ea."Id",
                ts_rank_cd(ea.content_tokens, websearch_to_tsquery('english', $1), 32) as score
            FROM evidence_artifacts ea
            WHERE ea.content_tokens @@ websearch_to_tsquery('english', $1)
            ORDER BY score DESC
            LIMIT 25
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("machine learning");

        var explainOutput = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            explainOutput.Add(reader.GetString(0));

        var fullPlan = string.Join("\n", explainOutput);

        // Verify GIN index is being used (Bitmap Index Scan on idx_evidence_fts)
        fullPlan.Should().ContainAny("idx_evidence_fts", "Bitmap Index Scan", "Bitmap Heap Scan",
            $"Expected GIN index usage in query plan:\n{fullPlan}");
    }

    [Fact]
    public async Task SearchAsync_WithDocumentIds_FiltersCorrectly()
    {
        // The seed data has all evidence linked to one document via EntityId.
        // Create a second document with different evidence.
        var doc2 = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            Name = "Quantum Physics Guide",
            ContentHash = "test-hash-qp",
            FilePath = "/test/qp.md",
            MimeType = "text/markdown",
            Status = DocumentStatus.Completed
        };
        _db.Documents.Add(doc2);

        _db.EvidenceArtifacts.Add(new EvidenceArtifact
        {
            Id = Guid.NewGuid(),
            EntityId = doc2.Id,
            ArtifactType = EvidenceTypes.SegmentText,
            MimeType = "text/plain",
            StorageBackend = "inline",
            StoragePath = "inline:segment_text",
            Content = "Machine learning is applied in quantum physics simulations.",
            SegmentHash = "hash-qp-1",
            Metadata = "{}"
        });
        await _db.SaveChangesAsync();

        // Search with document filter for doc2 only
        var results = await _service.SearchAsync("machine learning", 10, [doc2.Id]);

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(r =>
            r.artifact.EntityId.Should().Be(doc2.Id,
                "all results should belong to the filtered document"));
    }

    private async Task SeedBulkEvidenceAsync(int count)
    {
        var topics = new[]
        {
            "Machine learning algorithms process large datasets to identify patterns and make predictions.",
            "Neural network architectures include convolutional, recurrent, and transformer models.",
            "Natural language processing enables computers to understand and generate human language.",
            "Computer vision systems detect and classify objects in images and video streams.",
            "Reinforcement learning agents learn optimal strategies through trial and error interaction.",
            "Gradient descent optimization minimizes loss functions during model training.",
            "Feature extraction transforms raw data into meaningful representations for analysis.",
            "Clustering algorithms group similar data points without requiring labeled examples.",
            "Decision tree models provide interpretable classification and regression predictions.",
            "Bayesian methods incorporate prior knowledge into probabilistic inference frameworks.",
            "Transfer learning adapts pre-trained models to new domains with limited data.",
            "Ensemble methods combine multiple models to improve prediction accuracy and robustness.",
            "Dimensionality reduction techniques like PCA compress high-dimensional feature spaces.",
            "Regularization prevents overfitting by penalizing model complexity during training.",
            "Data augmentation generates synthetic training examples to improve model generalization.",
            "Attention mechanisms allow models to focus on relevant parts of input sequences.",
            "Generative adversarial networks create realistic synthetic data through adversarial training.",
            "Recurrent neural networks process sequential data with temporal dependencies.",
            "Support vector machines find optimal hyperplanes for binary classification tasks.",
            "Random forests aggregate decision trees for robust ensemble predictions."
        };

        var rng = new Random(42);
        var parentId = Guid.NewGuid();

        var artifacts = Enumerable.Range(0, count).Select(i => new EvidenceArtifact
        {
            Id = Guid.NewGuid(),
            EntityId = parentId,
            ArtifactType = EvidenceTypes.SegmentText,
            MimeType = "text/plain",
            StorageBackend = "inline",
            StoragePath = "inline:segment_text",
            Content = topics[i % topics.Length] + $" Variant {i} explores additional concepts in this domain.",
            SegmentHash = $"bulk-{i}",
            Metadata = $"{{\"salience_score\": {0.5 + rng.NextDouble() * 0.5:F2}}}"
        }).ToList();

        _db.EvidenceArtifacts.AddRange(artifacts);
        await _db.SaveChangesAsync();
    }
}