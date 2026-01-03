namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Options for document processing
/// </summary>
public class DocumentHandlerOptions
{
    /// <summary>
    /// Whether to run in verbose mode with detailed output
    /// </summary>
    public bool Verbose { get; set; }

    /// <summary>
    /// Maximum file size in bytes to process
    /// </summary>
    public long? MaxFileSizeBytes { get; set; }

    /// <summary>
    /// Cancellation token for the operation
    /// </summary>
    public CancellationToken CancellationToken { get; set; } = default;
}

/// <summary>
/// Content extracted from a document by a handler
/// </summary>
public class DocumentContent
{
    /// <summary>
    /// The main text content in Markdown format
    /// </summary>
    public required string Markdown { get; init; }

    /// <summary>
    /// Document title if detected
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Detected content type (e.g., "article", "code", "image", "spreadsheet")
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Additional metadata extracted from the document
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Any embedded images or assets extracted (paths or base64)
    /// </summary>
    public IReadOnlyList<ExtractedAsset>? Assets { get; init; }
}

/// <summary>
/// An asset extracted from a document (image, attachment, etc.)
/// </summary>
public record ExtractedAsset(
    string Name,
    string MimeType,
    byte[]? Data,
    string? FilePath
);

/// <summary>
/// Pluggable document handler for processing specific file types.
/// Implement this interface to add support for new document formats.
/// </summary>
public interface IDocumentHandler
{
    /// <summary>
    /// File extensions this handler can process (e.g., ".pdf", ".docx")
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Priority when multiple handlers support the same extension.
    /// Higher priority wins. Default handlers use priority 0.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Human-readable name for logging/diagnostics
    /// </summary>
    string HandlerName { get; }

    /// <summary>
    /// Process a file and extract its content as Markdown
    /// </summary>
    /// <param name="filePath">Path to the file to process</param>
    /// <param name="options">Processing options</param>
    /// <returns>Extracted document content</returns>
    Task<DocumentContent> ProcessAsync(string filePath, DocumentHandlerOptions options);

    /// <summary>
    /// Check if this handler can process the given file.
    /// May perform additional validation beyond extension checking.
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <returns>True if this handler can process the file</returns>
    bool CanHandle(string filePath);
}

/// <summary>
/// Registry for document handlers. Routes files to appropriate handlers.
/// </summary>
public interface IDocumentHandlerRegistry
{
    /// <summary>
    /// Get the best handler for a file extension
    /// </summary>
    /// <param name="extension">File extension including dot (e.g., ".pdf")</param>
    /// <returns>Handler or null if no handler found</returns>
    IDocumentHandler? GetHandler(string extension);

    /// <summary>
    /// Get the best handler for a file path
    /// </summary>
    /// <param name="filePath">Full path to the file</param>
    /// <returns>Handler or null if no handler found</returns>
    IDocumentHandler? GetHandlerForFile(string filePath);

    /// <summary>
    /// Register a handler with the registry
    /// </summary>
    /// <param name="handler">The handler to register</param>
    void Register(IDocumentHandler handler);

    /// <summary>
    /// Get all registered handlers
    /// </summary>
    IReadOnlyList<IDocumentHandler> GetAllHandlers();

    /// <summary>
    /// Get all supported file extensions across all handlers
    /// </summary>
    IReadOnlyList<string> GetSupportedExtensions();
}
