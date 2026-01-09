namespace LucidRAG.Entities;

/// <summary>
/// Standard content types for retrieval entities.
/// Each type has specific summarization and embedding strategies.
/// </summary>
public static class ContentTypes
{
    /// <summary>Text documents: PDF, DOCX, Markdown, TXT, HTML.</summary>
    public const string Document = "document";

    /// <summary>Images: JPEG, PNG, GIF, WebP, SVG.</summary>
    public const string Image = "image";

    /// <summary>Audio: MP3, WAV, M4A, FLAC.</summary>
    public const string Audio = "audio";

    /// <summary>Video: MP4, WebM, MOV, AVI.</summary>
    public const string Video = "video";

    /// <summary>Structured data: CSV, Excel, Parquet, JSON (tabular).</summary>
    public const string Data = "data";

    /// <summary>Mixed content with multiple modalities.</summary>
    public const string Mixed = "mixed";

    /// <summary>Code files: source code with syntax highlighting support.</summary>
    public const string Code = "code";

    /// <summary>
    /// Get content type from MIME type.
    /// </summary>
    public static string FromMimeType(string mimeType)
    {
        if (string.IsNullOrEmpty(mimeType))
            return Document;

        var lower = mimeType.ToLowerInvariant();

        // Document types
        if (lower.StartsWith("text/") ||
            lower.Contains("pdf") ||
            lower.Contains("word") ||
            lower.Contains("document") ||
            lower.Contains("markdown") ||
            lower.Contains("html"))
            return Document;

        // Image types
        if (lower.StartsWith("image/"))
            return Image;

        // Audio types
        if (lower.StartsWith("audio/"))
            return Audio;

        // Video types
        if (lower.StartsWith("video/"))
            return Video;

        // Data types (tabular)
        if (lower.Contains("csv") ||
            lower.Contains("excel") ||
            lower.Contains("spreadsheet") ||
            lower.Contains("parquet") ||
            lower == "application/json" ||
            lower.Contains("json") && lower.Contains("table"))
            return Data;

        // Code types
        if (lower.Contains("javascript") ||
            lower.Contains("typescript") ||
            lower.Contains("python") ||
            lower.Contains("java") ||
            lower.Contains("csharp") ||
            lower.Contains("c#") ||
            lower.Contains("cpp") ||
            lower.Contains("c++") ||
            lower.Contains("rust") ||
            lower.Contains("go") ||
            lower.Contains("ruby") ||
            lower.Contains("php"))
            return Code;

        return Document; // Default
    }

    /// <summary>
    /// Get content type from file extension.
    /// </summary>
    public static string FromExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return Document;

        var ext = extension.ToLowerInvariant().TrimStart('.');

        return ext switch
        {
            // Documents
            "pdf" or "doc" or "docx" or "odt" or "rtf" => Document,
            "txt" or "md" or "markdown" or "rst" => Document,
            "html" or "htm" or "xhtml" => Document,

            // Images
            "jpg" or "jpeg" or "png" or "gif" or "webp" or "svg" or "bmp" or "tiff" or "ico" => Image,

            // Audio
            "mp3" or "wav" or "m4a" or "flac" or "ogg" or "aac" or "wma" => Audio,

            // Video
            "mp4" or "webm" or "mov" or "avi" or "mkv" or "wmv" or "flv" => Video,

            // Data (tabular)
            "csv" or "tsv" or "xls" or "xlsx" or "parquet" or "json" or "jsonl" or "ndjson" => Data,

            // Code
            "js" or "ts" or "jsx" or "tsx" => Code,
            "py" or "pyw" => Code,
            "cs" or "fs" or "vb" => Code,
            "java" or "kt" or "scala" => Code,
            "cpp" or "c" or "h" or "hpp" => Code,
            "rs" or "go" or "rb" or "php" or "swift" or "dart" => Code,
            "sql" or "sh" or "bash" or "ps1" or "yaml" or "yml" or "toml" => Code,

            _ => Document
        };
    }

    /// <summary>
    /// Check if content type supports OCR processing.
    /// </summary>
    public static bool SupportsOcr(string contentType) =>
        contentType is Document or Image;

    /// <summary>
    /// Check if content type supports table extraction.
    /// </summary>
    public static bool SupportsTableExtraction(string contentType) =>
        contentType is Document or Data;

    /// <summary>
    /// Check if content type supports transcription.
    /// </summary>
    public static bool SupportsTranscription(string contentType) =>
        contentType is Audio or Video;

    /// <summary>
    /// Check if content type is tabular/structured.
    /// </summary>
    public static bool IsTabular(string contentType) =>
        contentType is Data;
}

/// <summary>
/// Standard modality names for multi-modal entities.
/// An entity can have multiple modalities (e.g., a PDF with images and tables).
/// </summary>
public static class Modalities
{
    /// <summary>Text content - extracted from documents, OCR, transcription.</summary>
    public const string Text = "text";

    /// <summary>Visual content - images, diagrams, charts.</summary>
    public const string Visual = "visual";

    /// <summary>Audio content - speech, music, sounds.</summary>
    public const string Audio = "audio";

    /// <summary>Tabular content - structured data, tables, spreadsheets.</summary>
    public const string Tabular = "tabular";

    /// <summary>Code content - source code with semantic structure.</summary>
    public const string Code = "code";
}

/// <summary>
/// Standard signal types emitted by the pipeline.
/// Signals drive lazy activation of processing molecules.
/// </summary>
public static class SignalTypes
{
    // Ingestion signals
    public const string JobStarted = "ingestion:job_started";
    public const string ContentStored = "ingestion:content_stored";
    public const string JobCompleted = "ingestion:job_completed";

    // Document signals
    public const string DocumentConverted = "document:converted";
    public const string DocumentSummarized = "document:summarized";
    public const string EntitiesExtracted = "document:entities_extracted";

    // PDF-specific signals
    public const string PdfConverted = "pdf:converted";
    public const string PdfScannedPage = "pdf:scanned_page";
    public const string PdfImageExtracted = "pdf:image_extracted";
    public const string PdfTableDetected = "pdf:table_detected";
    public const string PdfLowQuality = "pdf:low_quality";

    // Image signals
    public const string ImageAnalyzed = "image:analyzed";
    public const string ImageDescribed = "image:described";
    public const string OcrCompleted = "ocr:completed";
    public const string OcrFailed = "ocr:failed";

    // Data signals (NEW - DataSummarizer)
    public const string DataProfiled = "data:profiled";
    public const string DataSummarized = "data:summarized";
    public const string DataSchemaDetected = "data:schema_detected";
    public const string DataAnomaliesDetected = "data:anomalies_detected";
    public const string DataCorrelationsFound = "data:correlations_found";
    public const string DataPiiDetected = "data:pii_detected";
    public const string DataInsightsGenerated = "data:insights_generated";

    // Audio/Video signals
    public const string AudioTranscribed = "audio:transcribed";
    public const string VideoFramesExtracted = "video:frames_extracted";

    // Processing signals
    public const string TextExtracted = "text:extracted";
    public const string TextSummarized = "text:summarized";
    public const string EmbeddingsGenerated = "embeddings:generated";

    // Error signals
    public const string ProcessingFailed = "processing:failed";
    public const string QualityLow = "quality:low";
}
