using System.Threading.Channels;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer;

/// <summary>
/// Main interface for document summarization.
/// Provides a clean, DI-friendly API for summarizing documents from various sources.
/// </summary>
/// <remarks>
/// <para>
/// The summarizer supports multiple modes, defaulting to BertRag which provides:
/// </para>
/// <list type="bullet">
///   <item>Local ONNX-based BERT embeddings (no external API required)</item>
///   <item>Semantic retrieval with hybrid RRF scoring</item>
///   <item>Optional LLM synthesis for fluent prose (requires Ollama)</item>
///   <item>Citation grounding - every claim traceable to source</item>
/// </list>
/// <para>
/// Basic usage:
/// </para>
/// <code>
/// // In Startup/Program.cs
/// services.AddDocSummarizer();
/// 
/// // In your service
/// var summary = await summarizer.SummarizeMarkdownAsync(markdownContent);
/// </code>
/// </remarks>
public interface IDocumentSummarizer
{
    /// <summary>
    /// Gets or sets the current summary template.
    /// Templates control output style, length, tone, and format.
    /// </summary>
    SummaryTemplate Template { get; set; }

    /// <summary>
    /// Summarize markdown content.
    /// </summary>
    /// <param name="markdown">The markdown content to summarize.</param>
    /// <param name="documentId">Optional document identifier for caching. If not provided, a hash will be computed.</param>
    /// <param name="focusQuery">Optional focus query to bias retrieval toward specific topics.</param>
    /// <param name="mode">Summarization mode. Defaults to Auto which selects BertRag for most documents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="DocumentSummary"/> containing the executive summary, topic breakdowns, and trace metadata.</returns>
    /// <example>
    /// <code>
    /// var summary = await summarizer.SummarizeMarkdownAsync(
    ///     markdown: "# My Document\n\nContent here...",
    ///     focusQuery: "What are the key architectural decisions?");
    /// 
    /// Console.WriteLine(summary.ExecutiveSummary);
    /// foreach (var topic in summary.TopicSummaries)
    /// {
    ///     Console.WriteLine($"## {topic.Topic}\n{topic.Summary}");
    /// }
    /// </code>
    /// </example>
    Task<DocumentSummary> SummarizeMarkdownAsync(
        string markdown,
        string? documentId = null,
        string? focusQuery = null,
        SummarizationMode mode = SummarizationMode.Auto,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Summarize markdown content with progress reporting via a channel.
    /// </summary>
    /// <param name="markdown">The markdown content to summarize.</param>
    /// <param name="progress">Channel writer to receive progress updates. Create with <see cref="ProgressChannel.CreateUnbounded"/> or <see cref="ProgressChannel.CreateBounded"/>.</param>
    /// <param name="documentId">Optional document identifier for caching.</param>
    /// <param name="focusQuery">Optional focus query to bias retrieval toward specific topics.</param>
    /// <param name="mode">Summarization mode. Defaults to Auto.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="DocumentSummary"/> containing the summary and metadata.</returns>
    /// <example>
    /// <code>
    /// // Create a progress channel
    /// var channel = ProgressChannel.CreateUnbounded();
    /// 
    /// // Start consuming progress updates in the background
    /// _ = Task.Run(async () =>
    /// {
    ///     await foreach (var update in channel.Reader.ReadAllAsync())
    ///     {
    ///         Console.WriteLine($"[{update.Stage}] {update.Message} ({update.PercentComplete:F0}%)");
    ///     }
    /// });
    /// 
    /// // Summarize with progress
    /// var summary = await summarizer.SummarizeMarkdownAsync(
    ///     markdown,
    ///     channel.Writer,
    ///     focusQuery: "key points");
    /// </code>
    /// </example>
    Task<DocumentSummary> SummarizeMarkdownAsync(
        string markdown,
        ChannelWriter<ProgressUpdate> progress,
        string? documentId = null,
        string? focusQuery = null,
        SummarizationMode mode = SummarizationMode.Auto,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Summarize a file (markdown, PDF, DOCX, or text).
    /// </summary>
    /// <param name="filePath">Path to the file to summarize.</param>
    /// <param name="focusQuery">Optional focus query to bias retrieval toward specific topics.</param>
    /// <param name="mode">Summarization mode. Defaults to Auto.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="DocumentSummary"/> containing the summary and metadata.</returns>
    /// <remarks>
    /// Supported file types:
    /// <list type="bullet">
    ///   <item>.md - Markdown (native support)</item>
    ///   <item>.pdf - PDF (requires PdfPig, or Docling for complex layouts)</item>
    ///   <item>.docx - Word documents (requires OpenXml)</item>
    ///   <item>.txt - Plain text</item>
    ///   <item>.html - HTML (converted to markdown)</item>
    /// </list>
    /// </remarks>
    Task<DocumentSummary> SummarizeFileAsync(
        string filePath,
        string? focusQuery = null,
        SummarizationMode mode = SummarizationMode.Auto,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Summarize a file with progress reporting via a channel.
    /// </summary>
    Task<DocumentSummary> SummarizeFileAsync(
        string filePath,
        ChannelWriter<ProgressUpdate> progress,
        string? focusQuery = null,
        SummarizationMode mode = SummarizationMode.Auto,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Summarize content from a URL.
    /// </summary>
    /// <param name="url">The URL to fetch and summarize.</param>
    /// <param name="focusQuery">Optional focus query to bias retrieval toward specific topics.</param>
    /// <param name="mode">Summarization mode. Defaults to Auto.</param>
    /// <param name="usePlaywright">If true, uses Playwright for JavaScript-rendered pages. Defaults to false.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="DocumentSummary"/> containing the summary and metadata.</returns>
    Task<DocumentSummary> SummarizeUrlAsync(
        string url,
        string? focusQuery = null,
        SummarizationMode mode = SummarizationMode.Auto,
        bool usePlaywright = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Summarize content from a URL with progress reporting via a channel.
    /// </summary>
    Task<DocumentSummary> SummarizeUrlAsync(
        string url,
        ChannelWriter<ProgressUpdate> progress,
        string? focusQuery = null,
        SummarizationMode mode = SummarizationMode.Auto,
        bool usePlaywright = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Answer a question about a document using RAG retrieval.
    /// </summary>
    /// <param name="markdown">The markdown content to query.</param>
    /// <param name="question">The question to answer.</param>
    /// <param name="documentId">Optional document identifier for caching.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="QueryAnswer"/> containing the answer with supporting evidence.</returns>
    /// <remarks>
    /// This method uses semantic search to find relevant segments, then synthesizes an answer.
    /// Each claim in the answer is grounded with citations to source segments.
    /// </remarks>
    Task<QueryAnswer> QueryAsync(
        string markdown,
        string question,
        string? documentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extract segments from markdown content without summarizing.
    /// Useful for building search indexes or custom pipelines.
    /// </summary>
    /// <param name="markdown">The markdown content to extract segments from.</param>
    /// <param name="documentId">Optional document identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="ExtractionResult"/> containing all segments with embeddings and salience scores.</returns>
    Task<ExtractionResult> ExtractSegmentsAsync(
        string markdown,
        string? documentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extract segments with progress reporting via a channel.
    /// </summary>
    Task<ExtractionResult> ExtractSegmentsAsync(
        string markdown,
        ChannelWriter<ProgressUpdate> progress,
        string? documentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if required services (Ollama, Docling) are available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ServiceAvailability"/> indicating which services are available.</returns>
    Task<ServiceAvailability> CheckServicesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Answer to a document query with supporting evidence.
/// </summary>
/// <param name="Answer">The synthesized answer to the question.</param>
/// <param name="Confidence">Confidence level in the answer.</param>
/// <param name="Evidence">Source segments that support the answer.</param>
/// <param name="Question">The original question asked.</param>
public record QueryAnswer(
    string Answer,
    ConfidenceLevel Confidence,
    List<EvidenceSegment> Evidence,
    string Question);

/// <summary>
/// A source segment used as evidence for an answer or claim.
/// </summary>
/// <param name="SegmentId">Unique identifier for the segment.</param>
/// <param name="Text">The segment text.</param>
/// <param name="Similarity">Semantic similarity to the query (0-1).</param>
/// <param name="SectionTitle">The section this segment belongs to.</param>
public record EvidenceSegment(
    string SegmentId,
    string Text,
    double Similarity,
    string? SectionTitle);

/// <summary>
/// Result of service availability check.
/// </summary>
/// <param name="OllamaAvailable">True if Ollama is running and accessible.</param>
/// <param name="DoclingAvailable">True if Docling is running and accessible.</param>
/// <param name="OllamaModel">The configured Ollama model, if available.</param>
/// <param name="EmbeddingReady">True if the embedding service is initialized.</param>
public record ServiceAvailability(
    bool OllamaAvailable,
    bool DoclingAvailable,
    string? OllamaModel,
    bool EmbeddingReady)
{
    /// <summary>
    /// True if BertRag mode can run (only requires ONNX embeddings, no external services).
    /// </summary>
    public bool CanRunBertOnly => EmbeddingReady;
    
    /// <summary>
    /// True if full summarization with LLM synthesis is available.
    /// </summary>
    public bool CanRunWithLlm => EmbeddingReady && OllamaAvailable;
    
    /// <summary>
    /// True if PDF/DOCX conversion via Docling is available.
    /// </summary>
    public bool CanConvertDocuments => DoclingAvailable;
}
