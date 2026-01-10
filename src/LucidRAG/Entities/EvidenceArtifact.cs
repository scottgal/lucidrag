using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LucidRAG.Entities;

/// <summary>
/// Evidence artifact stored for a retrieval entity.
/// Metadata in database, actual content in blob storage (filesystem/S3).
/// </summary>
public class EvidenceArtifact
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Parent entity this evidence belongs to.
    /// </summary>
    public Guid EntityId { get; set; }

    /// <summary>
    /// Type of evidence artifact (see EvidenceTypes constants).
    /// </summary>
    [Required]
    [MaxLength(64)]
    public required string ArtifactType { get; set; }

    /// <summary>
    /// MIME type of the stored content.
    /// </summary>
    [Required]
    [MaxLength(128)]
    public required string MimeType { get; set; }

    /// <summary>
    /// Storage backend: 'filesystem', 's3', 'azure_blob'
    /// </summary>
    [Required]
    [MaxLength(32)]
    public required string StorageBackend { get; set; }

    /// <summary>
    /// Path or key within the storage backend.
    /// </summary>
    [Required]
    [MaxLength(2048)]
    public required string StoragePath { get; set; }

    /// <summary>
    /// Size of the stored content in bytes.
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// SHA256 hash of the content for deduplication.
    /// </summary>
    [MaxLength(64)]
    public string? ContentHash { get; set; }

    /// <summary>
    /// What produced this evidence (e.g., 'tesseract', 'whisper', 'claude-3').
    /// </summary>
    [MaxLength(128)]
    public string? ProducerSource { get; set; }

    /// <summary>
    /// Version of the producer.
    /// </summary>
    [MaxLength(32)]
    public string? ProducerVersion { get; set; }

    /// <summary>
    /// Confidence score if applicable (0-1).
    /// </summary>
    public double? Confidence { get; set; }

    /// <summary>
    /// Type-specific metadata (JSON).
    /// For OCR: bounding boxes, language, word count.
    /// For frames: timestamp, frame number, scene label.
    /// For transcripts: start/end time, speaker.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? Metadata { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional expiration for temporary artifacts.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    // Navigation
    public RetrievalEntityRecord? Entity { get; set; }
}

/// <summary>
/// Standard evidence artifact types.
/// </summary>
public static class EvidenceTypes
{
    // Document/Text Evidence
    public const string SegmentText = "segment_text";     // RAG segment text content
    public const string DocumentText = "document_text";   // Full document text
    public const string OcrText = "ocr_text";
    public const string OcrWordBoxes = "ocr_word_boxes";
    public const string OcrConfidenceMap = "ocr_confidence_map";
    public const string LlmSummary = "llm_summary";
    public const string LlmClaims = "llm_claims";
    public const string LlmEntities = "llm_entities";

    // Image/Visual Evidence (from ImageSummarizer/VisionLLM)
    public const string ImageCaption = "image_caption";       // AI-generated caption
    public const string ImageAltText = "image_alt_text";      // Original alt text
    public const string ImageOcrText = "image_ocr_text";      // Text extracted from image
    public const string ImageMetadata = "image_metadata";     // EXIF, dimensions, etc.

    // Image Evidence
    public const string OriginalImage = "original_image";
    public const string Thumbnail = "thumbnail";
    public const string OcrCrop = "ocr_crop";
    public const string AnnotatedImage = "annotated_image";

    // Video Evidence
    public const string Filmstrip = "filmstrip";
    public const string KeyFrame = "key_frame";
    public const string FrameOcrCrop = "frame_ocr_crop";
    public const string SceneTransition = "scene_transition";

    // Audio Evidence
    public const string Transcript = "transcript";
    public const string TranscriptSegments = "transcript_segments";
    public const string SpeakerDiarization = "speaker_diarization";
    public const string AudioWaveform = "audio_waveform";

    // Analysis Evidence
    public const string SignalDump = "signal_dump";
    public const string EmbeddingVector = "embedding_vector";
    public const string QualityReport = "quality_report";
    public const string ProcessingLog = "processing_log";

    // Data/Tabular Evidence (DataSummarizer outputs)
    public const string DataProfile = "data_profile";
    public const string DataSchema = "data_schema";
    public const string DataInsights = "data_insights";
    public const string DataCorrelations = "data_correlations";
    public const string DataAnomalies = "data_anomalies";
    public const string DataPiiReport = "data_pii_report";
    public const string TableCsv = "table_csv";
    public const string TableParquet = "table_parquet";
    public const string TableJson = "table_json";
    public const string DataSampleRows = "data_sample_rows";
    public const string DataHistograms = "data_histograms";
}

/// <summary>
/// Metadata for OCR evidence artifacts.
/// </summary>
public record OcrEvidenceMetadata
{
    public string? Engine { get; init; }
    public string? Language { get; init; }
    public double OverallConfidence { get; init; }
    public int WordCount { get; init; }
    public BoundingBox? TextRegion { get; init; }
    public Dictionary<string, double>? LanguageConfidences { get; init; }
}

/// <summary>
/// Metadata for video frame evidence.
/// </summary>
public record FrameEvidenceMetadata
{
    public double TimestampSeconds { get; init; }
    public int FrameNumber { get; init; }
    public string? SceneLabel { get; init; }
    public double? MotionScore { get; init; }
    public bool IsKeyFrame { get; init; }
}

/// <summary>
/// Metadata for transcript evidence.
/// </summary>
public record TranscriptEvidenceMetadata
{
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public string? Speaker { get; init; }
    public double Confidence { get; init; }
    public string? Language { get; init; }
}

/// <summary>
/// Metadata for LLM-generated evidence (provenance tracking).
/// </summary>
public record LlmEvidenceMetadata
{
    public required string Model { get; init; }
    public string? PromptTemplate { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public double Temperature { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public string? SystemPromptHash { get; init; }
}

/// <summary>
/// Bounding box for spatial evidence.
/// </summary>
public record BoundingBox
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>
/// Metadata for data profile evidence (DataSummarizer outputs).
/// </summary>
public record DataEvidenceMetadata
{
    /// <summary>Row count in the dataset.</summary>
    public long RowCount { get; init; }

    /// <summary>Column count in the dataset.</summary>
    public int ColumnCount { get; init; }

    /// <summary>Column names.</summary>
    public List<string>? ColumnNames { get; init; }

    /// <summary>Column types inferred.</summary>
    public Dictionary<string, string>? ColumnTypes { get; init; }

    /// <summary>Number of alerts detected.</summary>
    public int AlertCount { get; init; }

    /// <summary>Number of insights generated.</summary>
    public int InsightCount { get; init; }

    /// <summary>Number of correlations found.</summary>
    public int CorrelationCount { get; init; }

    /// <summary>Whether PII was detected.</summary>
    public bool HasPii { get; init; }

    /// <summary>Target column if analyzed.</summary>
    public string? TargetColumn { get; init; }

    /// <summary>Profile processing time in seconds.</summary>
    public double ProfileSeconds { get; init; }

    /// <summary>LLM model used for insights (if any).</summary>
    public string? LlmModel { get; init; }
}

/// <summary>
/// Metadata for table extraction evidence (PDF tables).
/// </summary>
public record TableEvidenceMetadata
{
    /// <summary>Table ID/index in source document.</summary>
    public required string TableId { get; init; }

    /// <summary>Page number where table was found.</summary>
    public int PageNumber { get; init; }

    /// <summary>Number of rows in the table.</summary>
    public int RowCount { get; init; }

    /// <summary>Number of columns in the table.</summary>
    public int ColumnCount { get; init; }

    /// <summary>Column names if detected.</summary>
    public List<string>? ColumnNames { get; init; }

    /// <summary>Whether a header row was detected.</summary>
    public bool HasHeader { get; init; }

    /// <summary>Whether extraction was heuristic-based.</summary>
    public bool IsHeuristic { get; init; }

    /// <summary>Extraction confidence (0-1).</summary>
    public double Confidence { get; init; }
}
