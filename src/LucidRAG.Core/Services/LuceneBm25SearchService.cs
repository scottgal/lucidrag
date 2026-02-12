using System.Diagnostics;
using LucidRAG.Data;
using LucidRAG.Entities;
using Mostlylucid.DocSummarizer.Search;

namespace LucidRAG.Services;

/// <summary>
///     Adapts IFullTextSearch (Lucene.NET / SQLite FTS5) into IBm25SearchService.
///     This is the core/default FTS implementation. Plugins like PostgreSQL can override it.
/// </summary>
public class LuceneBm25SearchService : IBm25SearchService
{
    private readonly RagDocumentsDbContext _db;
    private readonly IFullTextSearch _fts;
    private readonly ILogger<LuceneBm25SearchService> _logger;

    public LuceneBm25SearchService(
        IFullTextSearch fts,
        RagDocumentsDbContext db,
        ILogger<LuceneBm25SearchService> logger)
    {
        _fts = fts;
        _db = db;
        _logger = logger;
    }

    public async Task<List<(EvidenceArtifact artifact, double score)>> SearchAsync(
        string query,
        int topK = 25,
        IEnumerable<Guid>? documentIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return [];

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Search Lucene index - returns IDs and scores
            var ftsResults = await _fts.SearchAsync(query, topK, ct);

            if (ftsResults.Count == 0)
                return [];

            // Parse artifact IDs from Lucene results
            var idScores = ftsResults
                .Where(r => Guid.TryParse(r.Id, out _))
                .Select(r => (id: Guid.Parse(r.Id), r.Score))
                .ToList();

            if (idScores.Count == 0)
                return [];

            var ids = idScores.Select(x => x.id).ToList();

            // Fetch full artifacts from database
            var query2 = _db.EvidenceArtifacts
                .Where(ea => ids.Contains(ea.Id));

            // Apply document ID filter (same semantics as PostgreSQL service)
            if (documentIds != null)
            {
                var docIdList = documentIds.ToList();
                query2 = query2.Where(ea => docIdList.Contains(ea.EntityId));
            }

            var artifacts = await query2.AsNoTracking().ToListAsync(ct);

            // Pair artifacts with scores, preserving Lucene ranking order
            var artifactLookup = artifacts.ToDictionary(a => a.Id);
            var results = idScores
                .Where(x => artifactLookup.ContainsKey(x.id))
                .Select(x => (artifactLookup[x.id], x.Score))
                .ToList();

            stopwatch.Stop();
            _logger.LogDebug(
                "Lucene FTS query completed in {ElapsedMs}ms: '{Query}' returned {Count} results",
                stopwatch.ElapsedMilliseconds, query, results.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lucene FTS search failed for query: {Query}", query);
            throw;
        }
    }

    public async Task<List<(EvidenceArtifact artifact, double score)>> SearchWithScoresAsync(
        string query,
        int topK = 25,
        IEnumerable<Guid>? documentIds = null,
        CancellationToken ct = default)
    {
        // Lucene always returns scores, so this delegates directly
        return await SearchAsync(query, topK, documentIds, ct);
    }
}