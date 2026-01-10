using Microsoft.Data.Sqlite;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.DocSummarizer.Images.Services.Storage;

/// <summary>
/// SQLite-based storage for image analysis signals and feedback.
/// Enables persistent storage, feedback loops, and analysis history tracking.
///
/// References:
/// - Microsoft.Data.Sqlite: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/
/// - SQLite best practices: Use parameterized queries to prevent injection
/// </summary>
public class SignalDatabase : ISignalDatabase, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SignalDatabase>? _logger;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _disposed;

    public SignalDatabase(string databasePath, ILogger<SignalDatabase>? logger = null)
    {
        _logger = logger;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        // Enable WAL mode for better concurrency
        using var walCmd = _connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        walCmd.ExecuteNonQuery();

        InitializeSchema();
    }

    /// <summary>
    /// Initialize database schema with tables and indexes.
    /// </summary>
    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();

        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS images (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sha256 TEXT NOT NULL UNIQUE,
                file_path TEXT,
                width INTEGER,
                height INTEGER,
                format TEXT,
                analyzed_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS signals (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                image_id INTEGER NOT NULL,
                key TEXT NOT NULL,
                value_type TEXT,
                value_json TEXT,
                confidence REAL,
                source TEXT,
                timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                tags_json TEXT,
                metadata_json TEXT,
                FOREIGN KEY (image_id) REFERENCES images(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_signals_image_id ON signals(image_id);
            CREATE INDEX IF NOT EXISTS idx_signals_key ON signals(key);
            CREATE INDEX IF NOT EXISTS idx_signals_source ON signals(source);
            CREATE INDEX IF NOT EXISTS idx_signals_timestamp ON signals(timestamp);
            CREATE INDEX IF NOT EXISTS idx_images_sha256 ON images(sha256);

            CREATE TABLE IF NOT EXISTS feedback (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                image_id INTEGER NOT NULL,
                signal_id INTEGER,
                feedback_type TEXT,
                original_value TEXT,
                corrected_value TEXT,
                confidence_adjustment REAL,
                notes TEXT,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (image_id) REFERENCES images(id) ON DELETE CASCADE,
                FOREIGN KEY (signal_id) REFERENCES signals(id) ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS idx_feedback_image_id ON feedback(image_id);
            CREATE INDEX IF NOT EXISTS idx_feedback_type ON feedback(feedback_type);

            -- Discriminator scoring tables
            CREATE TABLE IF NOT EXISTS discriminator_scores (
                id TEXT PRIMARY KEY,
                image_hash TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                image_type INTEGER NOT NULL,
                goal TEXT NOT NULL,
                overall_score REAL NOT NULL,
                ocr_fidelity REAL,
                motion_agreement REAL,
                palette_consistency REAL,
                structural_alignment REAL,
                grounding_completeness REAL,
                novelty_vs_prior REAL,
                vision_model TEXT,
                strategy TEXT,
                accepted INTEGER,
                feedback TEXT,
                signal_contributions_json TEXT,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_discriminator_scores_image_hash ON discriminator_scores(image_hash);
            CREATE INDEX IF NOT EXISTS idx_discriminator_scores_timestamp ON discriminator_scores(timestamp);
            CREATE INDEX IF NOT EXISTS idx_discriminator_scores_image_type ON discriminator_scores(image_type);
            CREATE INDEX IF NOT EXISTS idx_discriminator_scores_goal ON discriminator_scores(goal);
            CREATE INDEX IF NOT EXISTS idx_discriminator_scores_accepted ON discriminator_scores(accepted);

            -- Discriminator effectiveness tracking (with decay)
            CREATE TABLE IF NOT EXISTS discriminator_effectiveness (
                signal_name TEXT NOT NULL,
                image_type INTEGER NOT NULL,
                goal TEXT NOT NULL,
                weight REAL NOT NULL,
                evaluation_count INTEGER NOT NULL,
                agreement_count INTEGER NOT NULL,
                disagreement_count INTEGER NOT NULL,
                last_evaluated TEXT NOT NULL,
                decay_rate REAL NOT NULL,
                is_retired INTEGER DEFAULT 0,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (signal_name, image_type, goal)
            );

            CREATE INDEX IF NOT EXISTS idx_discriminator_effectiveness_weight ON discriminator_effectiveness(weight);
            CREATE INDEX IF NOT EXISTS idx_discriminator_effectiveness_retired ON discriminator_effectiveness(is_retired);
        ";

        cmd.ExecuteNonQuery();

        _logger?.LogInformation("Signal database schema initialized");
    }

    /// <summary>
    /// Store a complete dynamic image profile with all signals.
    /// </summary>
    public async Task<long> StoreProfileAsync(
        DynamicImageProfile profile,
        string sha256,
        string? filePath = null,
        int width = 0,
        int height = 0,
        string? format = null,
        CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            // Insert or get existing image record
            var imageId = await InsertOrGetImageAsync(sha256, filePath, width, height, format, ct);

            // Insert all signals
            using var transaction = _connection.BeginTransaction();
            try
            {
                foreach (var signal in profile.GetAllSignals())
                {
                    await InsertSignalAsync(imageId, signal, ct);
                }

                await transaction.CommitAsync(ct);

                _logger?.LogInformation("Stored {SignalCount} signals for image {SHA256}",
                    profile.GetAllSignals().Count(), sha256);

                return imageId;
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Insert or get existing image record.
    /// </summary>
    private async Task<long> InsertOrGetImageAsync(
        string sha256,
        string? filePath,
        int width,
        int height,
        string? format,
        CancellationToken ct)
    {
        // Check if exists
        using (var selectCmd = _connection.CreateCommand())
        {
            selectCmd.CommandText = "SELECT id FROM images WHERE sha256 = @sha256";
            selectCmd.Parameters.AddWithValue("@sha256", sha256);

            var existing = await selectCmd.ExecuteScalarAsync(ct);
            if (existing != null)
            {
                return Convert.ToInt64(existing);
            }
        }

        // Insert new
        using var insertCmd = _connection.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO images (sha256, file_path, width, height, format, analyzed_at)
            VALUES (@sha256, @filePath, @width, @height, @format, @analyzedAt);
            SELECT last_insert_rowid();
        ";

        insertCmd.Parameters.AddWithValue("@sha256", sha256);
        insertCmd.Parameters.AddWithValue("@filePath", filePath ?? (object)DBNull.Value);
        insertCmd.Parameters.AddWithValue("@width", width);
        insertCmd.Parameters.AddWithValue("@height", height);
        insertCmd.Parameters.AddWithValue("@format", format ?? (object)DBNull.Value);
        insertCmd.Parameters.AddWithValue("@analyzedAt", DateTime.UtcNow);

        var result = await insertCmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result!);
    }

    /// <summary>
    /// Insert a signal into the database.
    /// </summary>
    private async Task InsertSignalAsync(long imageId, Signal signal, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();

        cmd.CommandText = @"
            INSERT INTO signals (
                image_id, key, value_type, value_json, confidence,
                source, timestamp, tags_json, metadata_json
            ) VALUES (
                @imageId, @key, @valueType, @valueJson, @confidence,
                @source, @timestamp, @tagsJson, @metadataJson
            )
        ";

        cmd.Parameters.AddWithValue("@imageId", imageId);
        cmd.Parameters.AddWithValue("@key", signal.Key);
        cmd.Parameters.AddWithValue("@valueType", signal.ValueType ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@valueJson", signal.Value != null
            ? JsonSerializer.Serialize(signal.Value)
            : DBNull.Value);
        cmd.Parameters.AddWithValue("@confidence", signal.Confidence);
        cmd.Parameters.AddWithValue("@source", signal.Source);
        cmd.Parameters.AddWithValue("@timestamp", signal.Timestamp);
        cmd.Parameters.AddWithValue("@tagsJson", signal.Tags != null
            ? JsonSerializer.Serialize(signal.Tags)
            : DBNull.Value);
        cmd.Parameters.AddWithValue("@metadataJson", signal.Metadata != null
            ? JsonSerializer.Serialize(signal.Metadata)
            : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Load all signals for an image by SHA256.
    /// </summary>
    public async Task<DynamicImageProfile?> LoadProfileAsync(string sha256, CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            // Get image ID
            using var imageCmd = _connection.CreateCommand();
            imageCmd.CommandText = "SELECT id, file_path FROM images WHERE sha256 = @sha256";
            imageCmd.Parameters.AddWithValue("@sha256", sha256);

            using var imageReader = await imageCmd.ExecuteReaderAsync(ct);
            if (!await imageReader.ReadAsync(ct))
            {
                return null; // Image not found
            }

            var imageId = imageReader.GetInt64(0);
            var filePath = imageReader.IsDBNull(1) ? null : imageReader.GetString(1);

            // Load signals
            using var signalsCmd = _connection.CreateCommand();
            signalsCmd.CommandText = @"
                SELECT key, value_type, value_json, confidence, source, timestamp, tags_json, metadata_json
                FROM signals
                WHERE image_id = @imageId
                ORDER BY timestamp ASC
            ";
            signalsCmd.Parameters.AddWithValue("@imageId", imageId);

            var profile = new DynamicImageProfile
            {
                ImagePath = filePath
            };

            using var signalsReader = await signalsCmd.ExecuteReaderAsync(ct);
            while (await signalsReader.ReadAsync(ct))
            {
                var signal = new Signal
                {
                    Key = signalsReader.GetString(0),
                    ValueType = signalsReader.IsDBNull(1) ? null : signalsReader.GetString(1),
                    Value = signalsReader.IsDBNull(2) ? null : JsonSerializer.Deserialize<object>(signalsReader.GetString(2)),
                    Confidence = signalsReader.GetDouble(3),
                    Source = signalsReader.GetString(4),
                    Timestamp = signalsReader.GetDateTime(5),
                    Tags = signalsReader.IsDBNull(6) ? null : JsonSerializer.Deserialize<List<string>>(signalsReader.GetString(6)),
                    Metadata = signalsReader.IsDBNull(7) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(signalsReader.GetString(7))
                };

                profile.AddSignal(signal);
            }

            return profile;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Store user feedback for learning/improvement.
    /// </summary>
    public async Task StoreFeedbackAsync(
        string sha256,
        string feedbackType,
        string? originalValue,
        string? correctedValue,
        double? confidenceAdjustment = null,
        string? notes = null,
        long? signalId = null,
        CancellationToken ct = default)
    {
        // Get image ID
        using var imageCmd = _connection.CreateCommand();
        imageCmd.CommandText = "SELECT id FROM images WHERE sha256 = @sha256";
        imageCmd.Parameters.AddWithValue("@sha256", sha256);

        var imageId = await imageCmd.ExecuteScalarAsync(ct);
        if (imageId == null)
        {
            throw new InvalidOperationException($"Image with SHA256 {sha256} not found");
        }

        // Insert feedback
        using var feedbackCmd = _connection.CreateCommand();
        feedbackCmd.CommandText = @"
            INSERT INTO feedback (
                image_id, signal_id, feedback_type, original_value,
                corrected_value, confidence_adjustment, notes
            ) VALUES (
                @imageId, @signalId, @feedbackType, @originalValue,
                @correctedValue, @confidenceAdjustment, @notes
            )
        ";

        feedbackCmd.Parameters.AddWithValue("@imageId", imageId);
        feedbackCmd.Parameters.AddWithValue("@signalId", signalId ?? (object)DBNull.Value);
        feedbackCmd.Parameters.AddWithValue("@feedbackType", feedbackType);
        feedbackCmd.Parameters.AddWithValue("@originalValue", originalValue ?? (object)DBNull.Value);
        feedbackCmd.Parameters.AddWithValue("@correctedValue", correctedValue ?? (object)DBNull.Value);
        feedbackCmd.Parameters.AddWithValue("@confidenceAdjustment", confidenceAdjustment ?? (object)DBNull.Value);
        feedbackCmd.Parameters.AddWithValue("@notes", notes ?? (object)DBNull.Value);

        await feedbackCmd.ExecuteNonQueryAsync(ct);

        _logger?.LogInformation("Stored feedback for image {SHA256}: {FeedbackType}", sha256, feedbackType);
    }

    /// <summary>
    /// Get statistics about stored data.
    /// </summary>
    public async Task<DatabaseStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                (SELECT COUNT(*) FROM images) as image_count,
                (SELECT COUNT(*) FROM signals) as signal_count,
                (SELECT COUNT(*) FROM feedback) as feedback_count,
                (SELECT COUNT(DISTINCT source) FROM signals) as unique_sources,
                (SELECT COUNT(DISTINCT key) FROM signals) as unique_signal_keys
        ";

        using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new DatabaseStatistics
        {
            ImageCount = reader.GetInt32(0),
            SignalCount = reader.GetInt32(1),
            FeedbackCount = reader.GetInt32(2),
            UniqueSourceCount = reader.GetInt32(3),
            UniqueSignalKeyCount = reader.GetInt32(4)
        };
    }

    // Discriminator scoring methods

    /// <summary>
    /// Store a discriminator score to the immutable ledger
    /// </summary>
    public async Task StoreDiscriminatorScoreAsync(DiscriminatorScore score, CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO discriminator_scores (
                    id, image_hash, timestamp, image_type, goal, overall_score,
                    ocr_fidelity, motion_agreement, palette_consistency,
                    structural_alignment, grounding_completeness, novelty_vs_prior,
                    vision_model, strategy, accepted, feedback, signal_contributions_json
                ) VALUES (
                    @id, @imageHash, @timestamp, @imageType, @goal, @overallScore,
                    @ocrFidelity, @motionAgreement, @paletteConsistency,
                    @structuralAlignment, @groundingCompleteness, @noveltyVsPrior,
                    @visionModel, @strategy, @accepted, @feedback, @signalContributionsJson
                )
            ";

            cmd.Parameters.AddWithValue("@id", score.Id);
            cmd.Parameters.AddWithValue("@imageHash", score.ImageHash);
            cmd.Parameters.AddWithValue("@timestamp", score.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@imageType", (int)score.ImageType);
            cmd.Parameters.AddWithValue("@goal", score.Goal);
            cmd.Parameters.AddWithValue("@overallScore", score.OverallScore);
            cmd.Parameters.AddWithValue("@ocrFidelity", score.Vectors.OcrFidelity);
            cmd.Parameters.AddWithValue("@motionAgreement", score.Vectors.MotionAgreement);
            cmd.Parameters.AddWithValue("@paletteConsistency", score.Vectors.PaletteConsistency);
            cmd.Parameters.AddWithValue("@structuralAlignment", score.Vectors.StructuralAlignment);
            cmd.Parameters.AddWithValue("@groundingCompleteness", score.Vectors.GroundingCompleteness);
            cmd.Parameters.AddWithValue("@noveltyVsPrior", score.Vectors.NoveltyVsPrior);
            cmd.Parameters.AddWithValue("@visionModel", score.VisionModel ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@strategy", score.Strategy ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@accepted", score.Accepted.HasValue ? (score.Accepted.Value ? 1 : 0) : DBNull.Value);
            cmd.Parameters.AddWithValue("@feedback", score.Feedback ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@signalContributionsJson", JsonSerializer.Serialize(score.SignalContributions));

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Get discriminator scores for an image
    /// </summary>
    public async Task<List<DiscriminatorScore>> GetDiscriminatorScoresAsync(
        string imageHash,
        int limit = 10,
        CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, image_hash, timestamp, image_type, goal, overall_score,
                       ocr_fidelity, motion_agreement, palette_consistency,
                       structural_alignment, grounding_completeness, novelty_vs_prior,
                       vision_model, strategy, accepted, feedback, signal_contributions_json
                FROM discriminator_scores
                WHERE image_hash = @imageHash
                ORDER BY timestamp DESC
                LIMIT @limit
            ";

            cmd.Parameters.AddWithValue("@imageHash", imageHash);
            cmd.Parameters.AddWithValue("@limit", limit);

            var scores = new List<DiscriminatorScore>();

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var score = new DiscriminatorScore
                {
                    Id = reader.GetString(0),
                    ImageHash = reader.GetString(1),
                    Timestamp = DateTimeOffset.Parse(reader.GetString(2)),
                    ImageType = (ImageType)reader.GetInt32(3),
                    Goal = reader.GetString(4),
                    Vectors = new VectorScores
                    {
                        OcrFidelity = reader.GetDouble(6),
                        MotionAgreement = reader.GetDouble(7),
                        PaletteConsistency = reader.GetDouble(8),
                        StructuralAlignment = reader.GetDouble(9),
                        GroundingCompleteness = reader.GetDouble(10),
                        NoveltyVsPrior = reader.GetDouble(11)
                    },
                    VisionModel = reader.IsDBNull(12) ? null : reader.GetString(12),
                    Strategy = reader.IsDBNull(13) ? null : reader.GetString(13),
                    Accepted = reader.IsDBNull(14) ? null : reader.GetInt32(14) == 1,
                    Feedback = reader.IsDBNull(15) ? null : reader.GetString(15),
                    SignalContributions = JsonSerializer.Deserialize<Dictionary<string, SignalContribution>>(
                        reader.GetString(16)) ?? new Dictionary<string, SignalContribution>()
                };

                scores.Add(score);
            }

            return scores;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Update discriminator effectiveness (with decay)
    /// </summary>
    public async Task UpdateDiscriminatorEffectivenessAsync(
        DiscriminatorEffectiveness effectiveness,
        CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO discriminator_effectiveness (
                    signal_name, image_type, goal, weight, evaluation_count,
                    agreement_count, disagreement_count, last_evaluated, decay_rate,
                    is_retired, updated_at
                ) VALUES (
                    @signalName, @imageType, @goal, @weight, @evaluationCount,
                    @agreementCount, @disagreementCount, @lastEvaluated, @decayRate,
                    @isRetired, @updatedAt
                )
            ";

            cmd.Parameters.AddWithValue("@signalName", effectiveness.SignalName);
            cmd.Parameters.AddWithValue("@imageType", (int)effectiveness.ImageType);
            cmd.Parameters.AddWithValue("@goal", effectiveness.Goal);
            cmd.Parameters.AddWithValue("@weight", effectiveness.Weight);
            cmd.Parameters.AddWithValue("@evaluationCount", effectiveness.EvaluationCount);
            cmd.Parameters.AddWithValue("@agreementCount", effectiveness.AgreementCount);
            cmd.Parameters.AddWithValue("@disagreementCount", effectiveness.DisagreementCount);
            cmd.Parameters.AddWithValue("@lastEvaluated", effectiveness.LastEvaluated.ToString("O"));
            cmd.Parameters.AddWithValue("@decayRate", effectiveness.DecayRate);
            cmd.Parameters.AddWithValue("@isRetired", 0);
            cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Get discriminator effectiveness for a specific signal/type/goal
    /// </summary>
    public async Task<DiscriminatorEffectiveness?> GetDiscriminatorEffectivenessAsync(
        string signalName,
        ImageType imageType,
        string goal,
        CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT signal_name, image_type, goal, weight, evaluation_count,
                       agreement_count, disagreement_count, last_evaluated, decay_rate
                FROM discriminator_effectiveness
                WHERE signal_name = @signalName
                  AND image_type = @imageType
                  AND goal = @goal
                  AND is_retired = 0
            ";

            cmd.Parameters.AddWithValue("@signalName", signalName);
            cmd.Parameters.AddWithValue("@imageType", (int)imageType);
            cmd.Parameters.AddWithValue("@goal", goal);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new DiscriminatorEffectiveness
                {
                    SignalName = reader.GetString(0),
                    ImageType = (ImageType)reader.GetInt32(1),
                    Goal = reader.GetString(2),
                    Weight = reader.GetDouble(3),
                    EvaluationCount = reader.GetInt32(4),
                    AgreementCount = reader.GetInt32(5),
                    DisagreementCount = reader.GetInt32(6),
                    LastEvaluated = DateTimeOffset.Parse(reader.GetString(7)),
                    DecayRate = reader.GetDouble(8)
                };
            }

            return null;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Get all discriminator effectiveness records for a type/goal
    /// </summary>
    public async Task<List<DiscriminatorEffectiveness>> GetAllDiscriminatorEffectivenessAsync(
        ImageType imageType,
        string goal,
        CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT signal_name, image_type, goal, weight, evaluation_count,
                       agreement_count, disagreement_count, last_evaluated, decay_rate
                FROM discriminator_effectiveness
                WHERE image_type = @imageType
                  AND goal = @goal
                  AND is_retired = 0
                ORDER BY weight DESC
            ";

            cmd.Parameters.AddWithValue("@imageType", (int)imageType);
            cmd.Parameters.AddWithValue("@goal", goal);

            var effectiveness = new List<DiscriminatorEffectiveness>();

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                effectiveness.Add(new DiscriminatorEffectiveness
                {
                    SignalName = reader.GetString(0),
                    ImageType = (ImageType)reader.GetInt32(1),
                    Goal = reader.GetString(2),
                    Weight = reader.GetDouble(3),
                    EvaluationCount = reader.GetInt32(4),
                    AgreementCount = reader.GetInt32(5),
                    DisagreementCount = reader.GetInt32(6),
                    LastEvaluated = DateTimeOffset.Parse(reader.GetString(7)),
                    DecayRate = reader.GetDouble(8)
                });
            }

            return effectiveness;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Retire a discriminator (mark as inactive due to low effectiveness)
    /// </summary>
    public async Task RetireDiscriminatorAsync(
        string signalName,
        ImageType imageType,
        string goal,
        CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE discriminator_effectiveness
                SET is_retired = 1, updated_at = @updatedAt
                WHERE signal_name = @signalName
                  AND image_type = @imageType
                  AND goal = @goal
            ";

            cmd.Parameters.AddWithValue("@signalName", signalName);
            cmd.Parameters.AddWithValue("@imageType", (int)imageType);
            cmd.Parameters.AddWithValue("@goal", goal);
            cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Get total count of discriminator scores
    /// </summary>
    public async Task<int> GetTotalScoreCountAsync(CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM discriminator_scores";

            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Get count of scores with feedback
    /// </summary>
    public async Task<int> GetTotalFeedbackCountAsync(CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM discriminator_scores WHERE accepted IS NOT NULL";

            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection?.Dispose();
            _dbLock?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Database statistics for monitoring.
/// </summary>
public record DatabaseStatistics
{
    public int ImageCount { get; init; }
    public int SignalCount { get; init; }
    public int FeedbackCount { get; init; }
    public int UniqueSourceCount { get; init; }
    public int UniqueSignalKeyCount { get; init; }
}
