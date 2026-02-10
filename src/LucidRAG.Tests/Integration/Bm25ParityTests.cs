using FluentAssertions;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Plugin.Postgres;
using LucidRAG.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.FullText.Lucene;

namespace LucidRAG.Tests.Integration;

/// <summary>
///     Parity tests verifying that LuceneBm25SearchService and PostgresBm25Service
///     produce equivalent results for the same data set.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class Bm25ParityTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;

    private RagDocumentsDbContext _db = null!;
    private LuceneBm25SearchService _luceneService = null!;
    private LuceneFullTextSearch _luceneFts = null!;
    private PostgresBm25Service _pgService = null!;
    private IServiceScope _scope = null!;

    private Guid _doc1Id;
    private Guid _doc2Id;

    public Bm25ParityTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureDatabaseAsync();

        _scope = _factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        _pgService = (PostgresBm25Service)_scope.ServiceProvider.GetRequiredService<IBm25SearchService>();

        // Create Lucene service with a temp index directory
        var tempDir = Path.Combine(Path.GetTempPath(), "bm25-parity-" + Guid.NewGuid().ToString("N"));
        _luceneFts = new LuceneFullTextSearch(tempDir);
        await _luceneFts.InitializeAsync();

        var loggerFactory = _scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        _luceneService = new LuceneBm25SearchService(
            _luceneFts,
            _db,
            loggerFactory.CreateLogger<LuceneBm25SearchService>());

        await SeedTestDataAsync();
    }

    public async Task DisposeAsync()
    {
        _luceneFts.Dispose();
        await _factory.CleanupAsync();
        _scope.Dispose();

        // Clean up temp Lucene index
        if (Directory.Exists(_luceneFts.IndexPath))
        {
            try { Directory.Delete(_luceneFts.IndexPath, true); }
            catch { /* best effort */ }
        }
    }

    private async Task SeedTestDataAsync()
    {
        _doc1Id = Guid.NewGuid();
        _doc2Id = Guid.NewGuid();

        var doc1 = new DocumentEntity
        {
            Id = _doc1Id,
            Name = "Machine Learning Guide",
            ContentHash = "parity-test-hash-1",
            FilePath = "/test/parity-ml.md",
            MimeType = "text/markdown",
            Status = DocumentStatus.Completed
        };

        var doc2 = new DocumentEntity
        {
            Id = _doc2Id,
            Name = "Quantum Computing Guide",
            ContentHash = "parity-test-hash-2",
            FilePath = "/test/parity-qc.md",
            MimeType = "text/markdown",
            Status = DocumentStatus.Completed
        };

        _db.Documents.AddRange(doc1, doc2);

        var artifacts = new[]
        {
            new EvidenceArtifact
            {
                Id = Guid.NewGuid(),
                EntityId = _doc1Id,
                ArtifactType = EvidenceTypes.SegmentText,
                MimeType = "text/plain",
                StorageBackend = "inline",
                StoragePath = "inline:segment_text",
                Content = "Machine learning is a subset of artificial intelligence that uses algorithms to learn patterns from data.",
                SegmentHash = "parity-hash-1",
                Metadata = "{}"
            },
            new EvidenceArtifact
            {
                Id = Guid.NewGuid(),
                EntityId = _doc1Id,
                ArtifactType = EvidenceTypes.SegmentText,
                MimeType = "text/plain",
                StorageBackend = "inline",
                StoragePath = "inline:segment_text",
                Content = "Neural networks use multiple layers of interconnected nodes to model complex relationships in data.",
                SegmentHash = "parity-hash-2",
                Metadata = "{}"
            },
            new EvidenceArtifact
            {
                Id = Guid.NewGuid(),
                EntityId = _doc2Id,
                ArtifactType = EvidenceTypes.SegmentText,
                MimeType = "text/plain",
                StorageBackend = "inline",
                StoragePath = "inline:segment_text",
                Content = "Quantum computing uses qubits and superposition to solve certain problems exponentially faster.",
                SegmentHash = "parity-hash-3",
                Metadata = "{}"
            }
        };

        _db.EvidenceArtifacts.AddRange(artifacts);
        await _db.SaveChangesAsync();

        // Index the same artifacts in Lucene
        foreach (var artifact in artifacts)
        {
            await _luceneFts.IndexDocumentAsync(
                artifact.Id.ToString(),
                "", // no title for segment text
                artifact.Content ?? "");
        }
    }

    [Fact]
    public async Task MatchingQuery_BothReturnNonEmpty()
    {
        var query = "machine learning algorithms";

        var pgResults = await _pgService.SearchWithScoresAsync(query, 10);
        var luceneResults = await _luceneService.SearchWithScoresAsync(query, 10);

        pgResults.Should().NotBeEmpty("PostgreSQL should find matching artifacts");
        luceneResults.Should().NotBeEmpty("Lucene should find matching artifacts");
    }

    [Fact]
    public async Task NonMatchingQuery_BothReturnEmpty()
    {
        var query = "blockchain cryptocurrency ethereum";

        var pgResults = await _pgService.SearchWithScoresAsync(query, 10);
        var luceneResults = await _luceneService.SearchWithScoresAsync(query, 10);

        pgResults.Should().BeEmpty("PostgreSQL should not find non-matching artifacts");
        luceneResults.Should().BeEmpty("Lucene should not find non-matching artifacts");
    }

    [Fact]
    public async Task DocumentIdFilter_BothRespectFilter()
    {
        // Search for "machine learning" but restrict to doc2 (quantum computing) — should exclude ML results
        var query = "machine learning";

        var pgResults = await _pgService.SearchWithScoresAsync(query, 10, [_doc2Id]);
        var luceneResults = await _luceneService.SearchWithScoresAsync(query, 10, [_doc2Id]);

        // Both backends should only return artifacts from doc2
        pgResults.Should().AllSatisfy(r =>
            r.artifact.EntityId.Should().Be(_doc2Id, "PostgreSQL should filter to doc2 only"));
        luceneResults.Should().AllSatisfy(r =>
            r.artifact.EntityId.Should().Be(_doc2Id, "Lucene should filter to doc2 only"));
    }

    [Fact]
    public async Task BothReturnRealScores()
    {
        var query = "neural networks";

        var pgResults = await _pgService.SearchWithScoresAsync(query, 10);
        var luceneResults = await _luceneService.SearchWithScoresAsync(query, 10);

        pgResults.Should().NotBeEmpty();
        luceneResults.Should().NotBeEmpty();

        // Both should return actual positive scores, not zeros or hardcoded values
        pgResults.Should().AllSatisfy(r =>
            r.score.Should().BeGreaterThan(0, "PostgreSQL scores should be positive"));
        luceneResults.Should().AllSatisfy(r =>
            r.score.Should().BeGreaterThan(0, "Lucene scores should be positive"));
    }

    [Fact]
    public async Task ScoreOrdering_BothDescending()
    {
        // Use a broad query that should match multiple artifacts
        var query = "learning data";

        var pgResults = await _pgService.SearchWithScoresAsync(query, 10);
        var luceneResults = await _luceneService.SearchWithScoresAsync(query, 10);

        // Verify descending score order for both backends
        for (var i = 0; i < pgResults.Count - 1; i++)
            pgResults[i].score.Should().BeGreaterThanOrEqualTo(pgResults[i + 1].score,
                "PostgreSQL results should be in descending score order");

        for (var i = 0; i < luceneResults.Count - 1; i++)
            luceneResults[i].score.Should().BeGreaterThanOrEqualTo(luceneResults[i + 1].score,
                "Lucene results should be in descending score order");
    }
}
