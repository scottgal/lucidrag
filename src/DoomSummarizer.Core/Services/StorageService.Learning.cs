using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     Learning log: captures sentinel LLM corrections to the embedding classifier.
///     Table: learning_log — stores query, both classification results, and disagreement flags.
///     Used by LearningAnalyzer to propose new exemplars from observed gaps.
/// </summary>
public partial class StorageService : ILearningLogger
{
    // --- Learning Log Methods ---

    /// <summary>
    ///     Log a disagreement between embedding classifier and sentinel LLM.
    ///     Computes per-dimension disagreement flags and stores the full context.
    /// </summary>
    public async Task LogDisagreementAsync(string query, float[]? embedding,
        QueryClassification embeddingResult, SentinelIntent sentinelResult)
    {
        // Extract top embedding topic
        var embTopic = embeddingResult.Categories
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .FirstOrDefault();
        var embType = embeddingResult.QueryType;
        var embVibe = embeddingResult.Vibe;

        // Extract sentinel topic (highest weight category)
        var sentinelTopic = sentinelResult.Categories
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .FirstOrDefault();
        var sentinelType = sentinelResult.Intent;
        var sentinelVibe = sentinelResult.Tone is "neutral" ? null : sentinelResult.Tone;

        // Compute disagreement flags
        var topicDisagree = !string.Equals(embTopic, sentinelTopic, StringComparison.OrdinalIgnoreCase);
        var typeDisagree = !string.Equals(embType, sentinelType, StringComparison.OrdinalIgnoreCase);
        var vibeDisagree = !string.Equals(embVibe ?? "", sentinelVibe ?? "", StringComparison.OrdinalIgnoreCase);

        // Only log if there's at least one disagreement
        if (!topicDisagree && !typeDisagree && !vibeDisagree)
            return;

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
                          INSERT INTO learning_log (
                              query_text, query_embedding,
                              emb_topic, emb_type, emb_vibe, emb_best_score,
                              emb_is_composite, emb_is_complex,
                              sentinel_topic, sentinel_type, sentinel_vibe,
                              sentinel_is_composite,
                              topic_disagreement, type_disagreement, vibe_disagreement,
                              logged_at
                          ) VALUES (
                              @query, @embedding,
                              @embTopic, @embType, @embVibe, @embBestScore,
                              @embIsComposite, @embIsComplex,
                              @sentinelTopic, @sentinelType, @sentinelVibe,
                              @sentinelIsComposite,
                              @topicDisagree, @typeDisagree, @vibeDisagree,
                              @now
                          )
                          """;

        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@embedding",
            embedding != null ? EmbeddingCompat.ToBytes(embedding) : DBNull.Value);
        cmd.Parameters.AddWithValue("@embTopic", (object?)embTopic ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@embType", embType);
        cmd.Parameters.AddWithValue("@embVibe", (object?)embVibe ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@embBestScore", embeddingResult.BestMatchScore);
        cmd.Parameters.AddWithValue("@embIsComposite", embeddingResult.IsComposite ? 1 : 0);
        cmd.Parameters.AddWithValue("@embIsComplex", embeddingResult.IsComplex ? 1 : 0);
        cmd.Parameters.AddWithValue("@sentinelTopic", (object?)sentinelTopic ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sentinelType", sentinelType);
        cmd.Parameters.AddWithValue("@sentinelVibe", (object?)sentinelVibe ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sentinelIsComposite", sentinelResult.IsComposite ? 1 : 0);
        cmd.Parameters.AddWithValue("@topicDisagree", topicDisagree ? 1 : 0);
        cmd.Parameters.AddWithValue("@typeDisagree", typeDisagree ? 1 : 0);
        cmd.Parameters.AddWithValue("@vibeDisagree", vibeDisagree ? 1 : 0);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    ///     Get unpromoted learning log entries (not yet used for exemplar generation).
    /// </summary>
    /// <param name="since">Optional cutoff date — only return entries after this time.</param>
    public async Task<List<LearningLogEntry>> GetUnpromotedDisagreementsAsync(DateTimeOffset? since = null)
    {
        await using var cmd = _connection!.CreateCommand();
        var whereClause = "WHERE promoted = 0";
        if (since.HasValue)
        {
            whereClause += " AND logged_at >= @since";
            cmd.Parameters.AddWithValue("@since", since.Value.ToString("O"));
        }

        cmd.CommandText = $"""
                           SELECT id, query_text, query_embedding,
                                  emb_topic, emb_type, emb_vibe, emb_best_score,
                                  emb_is_composite, emb_is_complex,
                                  sentinel_topic, sentinel_type, sentinel_vibe,
                                  sentinel_is_composite,
                                  topic_disagreement, type_disagreement, vibe_disagreement,
                                  logged_at, promoted
                           FROM learning_log
                           {whereClause}
                           ORDER BY logged_at DESC
                           """;

        var results = new List<LearningLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadLearningLogEntry(reader));
        }

        return results;
    }

    /// <summary>
    ///     Get aggregate disagreement statistics grouped by pattern.
    /// </summary>
    public async Task<List<DisagreementStat>> GetDisagreementStatsAsync()
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
                          SELECT sentinel_topic, sentinel_type,
                                 COUNT(*) as count,
                                 SUM(topic_disagreement) as topic_disagree_count,
                                 SUM(type_disagreement) as type_disagree_count,
                                 SUM(vibe_disagreement) as vibe_disagree_count,
                                 AVG(emb_best_score) as avg_emb_score
                          FROM learning_log
                          WHERE promoted = 0
                          GROUP BY sentinel_topic, sentinel_type
                          HAVING COUNT(*) >= 2
                          ORDER BY count DESC
                          """;

        var results = new List<DisagreementStat>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DisagreementStat
            {
                SentinelTopic = reader.IsDBNull(0) ? null : reader.GetString(0),
                SentinelType = reader.GetString(1),
                Count = reader.GetInt32(2),
                TopicDisagreeCount = reader.GetInt32(3),
                TypeDisagreeCount = reader.GetInt32(4),
                VibeDisagreeCount = reader.GetInt32(5),
                AvgEmbeddingScore = reader.GetDouble(6)
            });
        }

        return results;
    }

    /// <summary>
    ///     Mark learning log entries as promoted (used for exemplar generation).
    /// </summary>
    public async Task MarkPromotedAsync(IEnumerable<long> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;

        foreach (var batch in idList.Chunk(50))
        {
            await using var cmd = _connection!.CreateCommand();
            var placeholders = new List<string>();
            for (var i = 0; i < batch.Length; i++)
            {
                placeholders.Add($"@id{i}");
                cmd.Parameters.AddWithValue($"@id{i}", batch[i]);
            }

            cmd.CommandText =
                $"UPDATE learning_log SET promoted = 1 WHERE id IN ({string.Join(",", placeholders)})";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    ///     Get total count of learning log entries for diagnostics.
    /// </summary>
    public async Task<(int total, int unpromoted, int promoted)> GetLearningLogCountsAsync()
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
                          SELECT
                              COUNT(*) as total,
                              SUM(CASE WHEN promoted = 0 THEN 1 ELSE 0 END) as unpromoted,
                              SUM(CASE WHEN promoted = 1 THEN 1 ELSE 0 END) as promoted
                          FROM learning_log
                          """;

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (
                reader.GetInt32(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2)
            );
        }

        return (0, 0, 0);
    }

    /// <summary>
    ///     Get the timestamp of the most recent learning log entry.
    ///     Used by the CLI to decide whether to run learning on startup.
    /// </summary>
    public async Task<DateTimeOffset?> GetLastLearnRunAsync()
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT MAX(logged_at) FROM learning_log WHERE promoted = 1";
        var result = await cmd.ExecuteScalarAsync();
        if (result is DBNull || result is null)
            return null;
        return DateTimeOffset.Parse((string)result);
    }

    /// <summary>
    ///     Cleanup old learning log entries past retention window.
    ///     Promoted entries are kept for 7 days, unpromoted for 30 days.
    /// </summary>
    public async Task CleanupLearningLogAsync()
    {
        var promotedCutoff = DateTimeOffset.UtcNow.AddDays(-7).ToString("O");
        var unpromotedCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = """
                          DELETE FROM learning_log
                          WHERE (promoted = 1 AND logged_at < @promotedCutoff)
                             OR (promoted = 0 AND logged_at < @unpromotedCutoff)
                          """;
        cmd.Parameters.AddWithValue("@promotedCutoff", promotedCutoff);
        cmd.Parameters.AddWithValue("@unpromotedCutoff", unpromotedCutoff);
        await cmd.ExecuteNonQueryAsync();
    }

    private static LearningLogEntry ReadLearningLogEntry(System.Data.Common.DbDataReader reader)
    {
        return new LearningLogEntry
        {
            Id = reader.GetInt64(0),
            QueryText = reader.GetString(1),
            QueryEmbedding = reader.IsDBNull(2) ? null : EmbeddingCompat.FromBytes((byte[])reader[2]),
            EmbTopic = reader.IsDBNull(3) ? null : reader.GetString(3),
            EmbType = reader.GetString(4),
            EmbVibe = reader.IsDBNull(5) ? null : reader.GetString(5),
            EmbBestScore = reader.GetDouble(6),
            EmbIsComposite = reader.GetInt32(7) == 1,
            EmbIsComplex = reader.GetInt32(8) == 1,
            SentinelTopic = reader.IsDBNull(9) ? null : reader.GetString(9),
            SentinelType = reader.GetString(10),
            SentinelVibe = reader.IsDBNull(11) ? null : reader.GetString(11),
            SentinelIsComposite = reader.GetInt32(12) == 1,
            TopicDisagreement = reader.GetInt32(13) == 1,
            TypeDisagreement = reader.GetInt32(14) == 1,
            VibeDisagreement = reader.GetInt32(15) == 1,
            LoggedAt = DateTimeOffset.Parse(reader.GetString(16)),
            Promoted = reader.GetInt32(17) == 1
        };
    }
}

/// <summary>
///     A row from the learning_log table.
/// </summary>
public record LearningLogEntry
{
    public long Id { get; init; }
    public string QueryText { get; init; } = "";
    public float[]? QueryEmbedding { get; init; }
    public string? EmbTopic { get; init; }
    public string EmbType { get; init; } = "";
    public string? EmbVibe { get; init; }
    public double EmbBestScore { get; init; }
    public bool EmbIsComposite { get; init; }
    public bool EmbIsComplex { get; init; }
    public string? SentinelTopic { get; init; }
    public string SentinelType { get; init; } = "";
    public string? SentinelVibe { get; init; }
    public bool SentinelIsComposite { get; init; }
    public bool TopicDisagreement { get; init; }
    public bool TypeDisagreement { get; init; }
    public bool VibeDisagreement { get; init; }
    public DateTimeOffset LoggedAt { get; init; }
    public bool Promoted { get; init; }
}

/// <summary>
///     Aggregate disagreement statistics for a (topic, type) bucket.
/// </summary>
public record DisagreementStat
{
    public string? SentinelTopic { get; init; }
    public string SentinelType { get; init; } = "";
    public int Count { get; init; }
    public int TopicDisagreeCount { get; init; }
    public int TypeDisagreeCount { get; init; }
    public int VibeDisagreeCount { get; init; }
    public double AvgEmbeddingScore { get; init; }
}
