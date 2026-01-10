using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Threading.Channels;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Mostlylucid.Summarizer.Core.Utilities;
using UglyToad.PdfPig;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Main implementation of <see cref="IDocumentSummarizer"/> for the library.
/// Wraps the internal DocumentSummarizer with a clean, DI-friendly API.
/// 
/// Observability:
/// - OpenTelemetry tracing with Activity spans for all operations
/// - Metrics for summarization counts, durations, and document sizes
/// </summary>
public class DocumentSummarizerService : IDocumentSummarizer, IDisposable
{
    private readonly DocSummarizerConfig _config;
    private readonly IVectorStore? _vectorStore;
    private readonly bool _verbose;
    
    #region OpenTelemetry Instrumentation
    
    /// <summary>ActivitySource for distributed tracing</summary>
    private static readonly ActivitySource ActivitySource = new("Mostlylucid.DocSummarizer", "1.0.0");
    
    /// <summary>Meter for metrics</summary>
    private static readonly Meter Meter = new("Mostlylucid.DocSummarizer", "1.0.0");
    
    /// <summary>Counter for total summarization requests</summary>
    private static readonly Counter<long> SummarizationCounter = Meter.CreateCounter<long>(
        "docsummarizer.summarizations",
        "documents",
        "Total number of documents summarized");
    
    /// <summary>Counter for query requests</summary>
    private static readonly Counter<long> QueryCounter = Meter.CreateCounter<long>(
        "docsummarizer.queries",
        "queries",
        "Total number of Q&A queries");
    
    /// <summary>Histogram for summarization duration</summary>
    private static readonly Histogram<double> SummarizationDurationHistogram = Meter.CreateHistogram<double>(
        "docsummarizer.summarization.duration",
        "ms",
        "Duration of summarization operations in milliseconds");
    
    /// <summary>Histogram for document size (in characters)</summary>
    private static readonly Histogram<long> DocumentSizeHistogram = Meter.CreateHistogram<long>(
        "docsummarizer.document.size",
        "characters",
        "Size of documents processed in characters");
    
    /// <summary>Counter for errors by type</summary>
    private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
        "docsummarizer.errors",
        "errors",
        "Total number of errors during processing");
    
    #endregion
    
    // Lazy-initialized services
    private OllamaService? _ollama;
    private DoclingClient? _docling;
    private BertRagSummarizer? _bertRag;
    private OnnxEmbeddingService? _embedder;
    private WebFetcher? _webFetcher;
    private bool _initialized;
    private readonly object _initLock = new();
    
    /// <inheritdoc />
    public SummaryTemplate Template { get; set; }

    public DocumentSummarizerService(
        IOptions<DocSummarizerConfig> config,
        IVectorStore? vectorStore = null)
    {
        _config = config.Value;
        _vectorStore = vectorStore;
        _verbose = _config.Output.Verbose;
        Template = SummaryTemplate.Presets.Default;
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        
        lock (_initLock)
        {
            if (_initialized) return;
            
            // Initialize Ollama client
            var ollamaConfig = _config.Ollama;
            _ollama = new OllamaService(
                model: ollamaConfig.Model,
                embedModel: ollamaConfig.EmbedModel,
                baseUrl: ollamaConfig.BaseUrl,
                timeout: TimeSpan.FromSeconds(ollamaConfig.TimeoutSeconds),
                embeddingConfig: _config.Embedding,
                classifierModel: ollamaConfig.ClassifierModel);
            
            // Initialize Docling client if configured
            if (!string.IsNullOrEmpty(_config.Docling?.BaseUrl))
            {
                _docling = new DoclingClient(_config.Docling);
            }
            
            // Initialize embedding service
            _embedder = new OnnxEmbeddingService(_config.Onnx, _verbose);
            
            // Initialize BERT-RAG summarizer with converted configs
            _bertRag = new BertRagSummarizer(
                _config.Onnx,
                _ollama,
                _config.Extraction.ToExtractionConfig(),
                _config.Retrieval.ToRetrievalConfig(),
                Template,
                _verbose,
                _vectorStore,
                _config.BertRag);
            
            // Initialize web fetcher
            _webFetcher = new WebFetcher(_config.WebFetch);
            
            _initialized = true;
        }
    }

    /// <inheritdoc />
    public Task<DocumentSummary> SummarizeMarkdownAsync(
        string markdown,
        string? documentId = null,
        string? focusQuery = null,
        SummarizationMode mode = SummarizationMode.Auto,
        CancellationToken cancellationToken = default)
    {
        return SummarizeMarkdownAsync(markdown, null!, documentId, focusQuery, mode, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentSummary> SummarizeMarkdownAsync(
        string markdown,
        ChannelWriter<ProgressUpdate> progress,
        string? documentId = null,
        string? focusQuery = null,
        SummarizationMode mode = SummarizationMode.Auto,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("SummarizeMarkdown", ActivityKind.Internal);
        activity?.SetTag("docsummarizer.mode", mode.ToString());
        activity?.SetTag("docsummarizer.has_focus_query", focusQuery != null);
        activity?.SetTag("docsummarizer.document_size", markdown.Length);
        
        var sw = Stopwatch.StartNew();
        
        try
        {
            EnsureInitialized();
            progress.WriteStage("Initialization", "Services initialized", 5, sw.ElapsedMilliseconds);
            
            var docId = documentId ?? ComputeDocumentId(markdown);
            activity?.SetTag("docsummarizer.document_id", docId);
            
            progress.WriteStage("Extraction", "Starting segment extraction", 10, sw.ElapsedMilliseconds);
            _bertRag!.SetTemplate(Template);
            
            DocumentSizeHistogram.Record(markdown.Length, 
                new KeyValuePair<string, object?>("source", "markdown"));
            
            // TODO: Pass progress channel to BertRagSummarizer for internal progress
            var result = await _bertRag.SummarizeAsync(docId, markdown, focusQuery, ContentType.Unknown, cancellationToken);
            
            activity?.SetTag("docsummarizer.chunks_processed", result.Trace.ChunksProcessed);
            activity?.SetTag("docsummarizer.summary_length", result.ExecutiveSummary?.Length ?? 0);
            activity?.SetStatus(ActivityStatusCode.Ok);
            
            SummarizationCounter.Add(1,
                new KeyValuePair<string, object?>("mode", mode.ToString()),
                new KeyValuePair<string, object?>("source", "markdown"));
            
            progress.WriteCompleted($"Summarization complete", sw.ElapsedMilliseconds, new Dictionary<string, object>
            {
                ["segments"] = result.Trace.ChunksProcessed,
                ["mode"] = mode.ToString()
            });
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().Name);
            
            ErrorCounter.Add(1, 
                new KeyValuePair<string, object?>("operation", "summarize_markdown"),
                new KeyValuePair<string, object?>("error.type", ex.GetType().Name));
            throw;
        }
        finally
        {
            sw.Stop();
            SummarizationDurationHistogram.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("mode", mode.ToString()),
                new KeyValuePair<string, object?>("source", "markdown"));
        }
    }

    /// <inheritdoc />
    public Task<DocumentSummary> SummarizeFileAsync(
        string filePath,
        string? focusQuery = null,
        SummarizationMode mode = SummarizationMode.Auto,
        CancellationToken cancellationToken = default)
    {
        return SummarizeFileAsync(filePath, null!, focusQuery, mode, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentSummary> SummarizeFileAsync(
        string filePath,
        ChannelWriter<ProgressUpdate> progress,
        string? focusQuery = null,
        SummarizationMode mode = SummarizationMode.Auto,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("SummarizeFile", ActivityKind.Internal);
        activity?.SetTag("docsummarizer.file_path", filePath);
        activity?.SetTag("docsummarizer.file_extension", Path.GetExtension(filePath).ToLowerInvariant());
        activity?.SetTag("docsummarizer.mode", mode.ToString());
        
        var sw = Stopwatch.StartNew();
        
        try
        {
            EnsureInitialized();
            progress.WriteStage("FileLoad", $"Loading file: {Path.GetFileName(filePath)}", 5, sw.ElapsedMilliseconds);
            
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            string markdown;
            
            // Handle different file types
            switch (ext)
            {
                case ".md":
                case ".txt":
                    markdown = await File.ReadAllTextAsync(filePath, cancellationToken);
                    progress.WriteInfo("FileLoad", $"Loaded {markdown.Length:N0} characters", sw.ElapsedMilliseconds);
                    break;
                    
                case ".pdf":
                case ".docx":
                    if (_docling == null)
                    {
                        throw new InvalidOperationException(
                            "Docling is not configured. PDF/DOCX conversion requires Docling service.");
                    }
                    progress.WriteStage("Conversion", "Converting document with Docling", 10, sw.ElapsedMilliseconds);
                    markdown = await _docling.ConvertAsync(filePath, cancellationToken);
                    progress.WriteInfo("Conversion", $"Converted to {markdown.Length:N0} characters", sw.ElapsedMilliseconds);
                    break;
                    
                case ".html":
                case ".htm":
                    var html = await File.ReadAllTextAsync(filePath, cancellationToken);
                    markdown = HtmlToMarkdown(html);
                    progress.WriteInfo("FileLoad", $"Converted HTML to {markdown.Length:N0} characters", sw.ElapsedMilliseconds);
                    break;
                    
                default:
                    throw new NotSupportedException($"File type '{ext}' is not supported.");
            }
            
            DocumentSizeHistogram.Record(markdown.Length,
                new KeyValuePair<string, object?>("source", "file"),
                new KeyValuePair<string, object?>("extension", ext));
            
            var docId = Path.GetFileNameWithoutExtension(filePath);
            activity?.SetTag("docsummarizer.document_id", docId);
            
            return await SummarizeMarkdownAsync(markdown, progress, docId, focusQuery, mode, cancellationToken);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ErrorCounter.Add(1,
                new KeyValuePair<string, object?>("operation", "summarize_file"),
                new KeyValuePair<string, object?>("error.type", ex.GetType().Name));
            throw;
        }
    }

    /// <inheritdoc />
    public Task<DocumentSummary> SummarizeUrlAsync(
        string url,
        string? focusQuery = null,
        SummarizationMode mode = SummarizationMode.Auto,
        bool usePlaywright = false,
        CancellationToken cancellationToken = default)
    {
        return SummarizeUrlAsync(url, null!, focusQuery, mode, usePlaywright, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentSummary> SummarizeUrlAsync(
        string url,
        ChannelWriter<ProgressUpdate> progress,
        string? focusQuery = null,
        SummarizationMode mode = SummarizationMode.Auto,
        bool usePlaywright = false,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("SummarizeUrl", ActivityKind.Internal);
        activity?.SetTag("url.full", url);
        activity?.SetTag("docsummarizer.mode", mode.ToString());
        activity?.SetTag("docsummarizer.use_playwright", usePlaywright);
        
        var sw = Stopwatch.StartNew();
        
        try
        {
            EnsureInitialized();
            progress.WriteStage("Fetch", $"Fetching URL: {url}", 5, sw.ElapsedMilliseconds);
            
            var fetchMode = usePlaywright ? WebFetchMode.Playwright : WebFetchMode.Simple;
            using var result = await _webFetcher!.FetchAsync(url, fetchMode);
            
            activity?.SetTag("http.response.content_type", result.ContentType);
            progress.WriteInfo("Fetch", $"Fetched {result.ContentType ?? "content"}", sw.ElapsedMilliseconds);
            
            // Read the content from the temp file
            string content;
            if (result.IsHtmlContent)
            {
                var html = await File.ReadAllTextAsync(result.TempFilePath, cancellationToken);
                content = HtmlToMarkdown(html);
            }
            else
            {
                content = await File.ReadAllTextAsync(result.TempFilePath, cancellationToken);
            }
            
            DocumentSizeHistogram.Record(content.Length,
                new KeyValuePair<string, object?>("source", "url"),
                new KeyValuePair<string, object?>("fetch_mode", fetchMode.ToString()));
            
            var docId = new Uri(url).Host;
            activity?.SetTag("url.host", docId);
            
            return await SummarizeMarkdownAsync(content, progress, docId, focusQuery, mode, cancellationToken);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ErrorCounter.Add(1,
                new KeyValuePair<string, object?>("operation", "summarize_url"),
                new KeyValuePair<string, object?>("error.type", ex.GetType().Name));
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<QueryAnswer> QueryAsync(
        string markdown,
        string question,
        string? documentId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("Query", ActivityKind.Internal);
        activity?.SetTag("docsummarizer.question_length", question.Length);
        activity?.SetTag("docsummarizer.document_size", markdown.Length);
        
        var sw = Stopwatch.StartNew();
        
        try
        {
            EnsureInitialized();
            
            var docId = documentId ?? ComputeDocumentId(markdown);
            activity?.SetTag("docsummarizer.document_id", docId);
            
            // Use BertRag with the question as focus query to get segments first
            var (extraction, retrieved) = await _bertRag!.ExtractAndRetrieveAsync(
                docId, markdown, question, ContentType.Unknown, cancellationToken);
            
            activity?.SetTag("docsummarizer.segments_retrieved", retrieved.Count);
            
            // Now summarize with the focus query
            var result = await _bertRag.SummarizeAsync(docId, markdown, question, ContentType.Unknown, cancellationToken);
            
            // Convert retrieved segments to evidence format
            var evidence = retrieved
                .Take(5)
                .Select(s => new EvidenceSegment(
                    s.Id,
                    s.Text,
                    s.RetrievalScore,
                    s.SectionTitle))
                .ToList();
            
            activity?.SetTag("docsummarizer.evidence_count", evidence.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            
            QueryCounter.Add(1);
            
            return new QueryAnswer(
                Answer: result.ExecutiveSummary,
                Confidence: ConfidenceLevel.Medium, // Could be computed from scores
                Evidence: evidence,
                Question: question);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ErrorCounter.Add(1,
                new KeyValuePair<string, object?>("operation", "query"),
                new KeyValuePair<string, object?>("error.type", ex.GetType().Name));
            throw;
        }
        finally
        {
            sw.Stop();
            SummarizationDurationHistogram.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("mode", "query"),
                new KeyValuePair<string, object?>("source", "markdown"));
        }
    }

    /// <inheritdoc />
    public Task<ExtractionResult> ExtractSegmentsAsync(
        string markdown,
        string? documentId = null,
        CancellationToken cancellationToken = default)
    {
        return ExtractSegmentsAsync(markdown, null!, documentId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExtractionResult> ExtractSegmentsAsync(
        string markdown,
        ChannelWriter<ProgressUpdate> progress,
        string? documentId = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        
        EnsureInitialized();
        progress.WriteStage("Extraction", "Starting segment extraction", 10, sw.ElapsedMilliseconds);
        
        var docId = documentId ?? ComputeDocumentId(markdown);
        
        // Use the extract-and-retrieve method to get segments
        var (extraction, _) = await _bertRag!.ExtractAndRetrieveAsync(
            docId, markdown, null, ContentType.Unknown, cancellationToken);
        
        progress.WriteCompleted($"Extracted {extraction.AllSegments.Count} segments", sw.ElapsedMilliseconds, new Dictionary<string, object>
        {
            ["totalSegments"] = extraction.AllSegments.Count,
            ["topSegments"] = extraction.TopBySalience.Count,
            ["contentType"] = extraction.ContentType.ToString()
        });
        
        return extraction;
    }

    /// <inheritdoc />
    public async Task<ServiceAvailability> CheckServicesAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        
        var ollamaAvailable = false;
        var doclingAvailable = false;
        string? ollamaModel = null;
        
        try
        {
            ollamaAvailable = await _ollama!.IsAvailableAsync();
            if (ollamaAvailable)
            {
                ollamaModel = _config.Ollama.Model;
            }
        }
        catch
        {
            // Ollama not available
        }
        
        try
        {
            if (_docling != null)
            {
                doclingAvailable = await _docling.IsAvailableAsync();
            }
        }
        catch
        {
            // Docling not available
        }
        
        return new ServiceAvailability(
            OllamaAvailable: ollamaAvailable,
            DoclingAvailable: doclingAvailable,
            OllamaModel: ollamaModel,
            EmbeddingReady: _embedder != null);
    }

    private static string ComputeDocumentId(string content)
        => ContentHasher.ComputeHash(content);

    private static string HtmlToMarkdown(string html)
    {
        // Simple HTML to markdown conversion
        // In production, use a proper library like ReverseMarkdown
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return text.Trim();
    }

    public void Dispose()
    {
        _docling?.Dispose();
        _bertRag?.Dispose();
        _embedder?.Dispose();
    }
}
