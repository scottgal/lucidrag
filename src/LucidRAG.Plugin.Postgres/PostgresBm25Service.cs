using System.Diagnostics;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LucidRAG.Plugin.Postgres;

/// <summary>
///     PostgreSQL-native full-text search service using ts_rank_cd.
///
///     Requires the AddFullTextSearch migration which creates:
///     - content_tokens tsvector GENERATED ALWAYS column on evidence_artifacts
///     - idx_evidence_fts GIN index for fast FTS queries
///     - corpus_stats materialized view for BM25 statistics
///
///     Document ID filtering: EvidenceArtifact.EntityId == RetrievalEntityRecord.Id == DocumentEntity.Id
///     for document-type entities (RetrievalEntityService.StoreDocumentAsync uses document.Id as entity ID).
///     This means documentIds can filter directly on EntityId without a join.
///
///     The corpus_stats materialized view is retained for ops monitoring but is not refreshed
///     by this service. Use REFRESH MATERIALIZED VIEW CONCURRENTLY corpus_stats manually if needed.
/// </summary>
public class PostgresBm25Service : IBm25SearchService
{
    private readonly string _connectionString;
    private readonly RagDocumentsDbContext _db;
    private readonly ILogger<PostgresBm25Service> _logger;

    public PostgresBm25Service(
        RagDocumentsDbContext db,
        IConfiguration configuration,
        ILogger<PostgresBm25Service> logger)
    {
        _db = db;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException(
                                "DefaultConnection string not found in configuration");
    }

    public Task<List<(EvidenceArtifact artifact, double score)>> SearchAsync(
        string query,
        int topK = 25,
        IEnumerable<Guid>? documentIds = null,
        CancellationToken ct = default)
        => SearchWithScoresAsync(query, topK, documentIds, ct);

    public async Task<List<(EvidenceArtifact artifact, double score)>> SearchWithScoresAsync(
        string query,
        int topK = 25,
        IEnumerable<Guid>? documentIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var docIdArray = documentIds?.ToArray();
            var hasDocFilter = docIdArray is { Length: > 0 };

            // EvidenceArtifact.EntityId == RetrievalEntityRecord.Id == DocumentEntity.Id
            // for document-type entities, so we can filter directly on EntityId.
            // EF FromSqlRaw ignores extra columns (rank_score), so we query IDs+scores via Npgsql.
            var sql = hasDocFilter
                ? """
                  SELECT ea."Id",
                      ts_rank_cd(ea.content_tokens, websearch_to_tsquery('english', $1), 32) as score
                  FROM evidence_artifacts ea
                  WHERE ea.content_tokens @@ websearch_to_tsquery('english', $1)
                  AND ea."EntityId" = ANY($3)
                  ORDER BY score DESC
                  LIMIT $2
                  """
                : """
                  SELECT ea."Id",
                      ts_rank_cd(ea.content_tokens, websearch_to_tsquery('english', $1), 32) as score
                  FROM evidence_artifacts ea
                  WHERE ea.content_tokens @@ websearch_to_tsquery('english', $1)
                  ORDER BY score DESC
                  LIMIT $2
                  """;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue(query);
            command.Parameters.AddWithValue(topK);
            if (hasDocFilter)
                command.Parameters.AddWithValue(docIdArray!);

            var idsAndScores = new List<(Guid id, double score)>();

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                idsAndScores.Add((reader.GetGuid(0), reader.GetDouble(1)));
            await reader.CloseAsync();

            if (idsAndScores.Count == 0)
            {
                stopwatch.Stop();
                return [];
            }

            var ids = idsAndScores.Select(x => x.id).ToList();
            var artifactLookup = (await _db.EvidenceArtifacts
                .Where(ea => ids.Contains(ea.Id))
                .AsNoTracking()
                .ToListAsync(ct))
                .ToDictionary(a => a.Id);

            var results = idsAndScores
                .Where(x => artifactLookup.ContainsKey(x.id))
                .Select(x => (artifactLookup[x.id], x.score))
                .ToList();

            stopwatch.Stop();
            _logger.LogDebug(
                "PostgreSQL FTS query completed in {ElapsedMs}ms: '{Query}' returned {Count} results",
                stopwatch.ElapsedMilliseconds, query, results.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PostgreSQL FTS search failed for query: {Query}", query);
            throw;
        }
    }
}