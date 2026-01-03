using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;


namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Detected service capabilities
/// </summary>
public class DetectedServices
{
    public bool OllamaAvailable { get; set; }
    public string? OllamaModel { get; set; }
    public List<string> AvailableModels { get; set; } = [];
    
    public bool DoclingAvailable { get; set; }
    public bool DoclingHasGpu { get; set; }
    public string? DoclingAccelerator { get; set; }
    
    public bool QdrantAvailable { get; set; }
    
    public bool OnnxAvailable { get; set; } = true; // Always available (embedded)
    
    /// <summary>
    /// Available features based on detected services
    /// </summary>
    public ServiceFeatures Features => new(this);
    
    /// <summary>
    /// Get a compact summary string for display
    /// </summary>
    public string GetSummary()
    {
        var parts = new List<string>();
        
        if (OllamaAvailable)
            parts.Add($"Ollama[green]✓[/]");
        else
            parts.Add($"Ollama[red]✗[/]");
        
        if (DoclingAvailable)
        {
            var gpu = DoclingHasGpu ? "[cyan](GPU)[/]" : "[dim](CPU)[/]";
            parts.Add($"Docling[green]✓[/]{gpu}");
        }
        else
            parts.Add($"Docling[yellow]○[/]");
        
        if (QdrantAvailable)
            parts.Add($"Qdrant[green]✓[/]");
        else
            parts.Add($"Qdrant[dim]○[/]");
        
        parts.Add($"ONNX[green]✓[/]");
        
        return string.Join(" ", parts);
    }
    
    /// <summary>
    /// Get recommended Docling config based on detected capabilities
    /// </summary>
    public DoclingConfig GetOptimizedDoclingConfig(DoclingConfig baseConfig)
    {
        var config = new DoclingConfig
        {
            BaseUrl = baseConfig.BaseUrl,
            TimeoutSeconds = baseConfig.TimeoutSeconds,
            PdfBackend = baseConfig.PdfBackend,
            AutoDetectGpu = baseConfig.AutoDetectGpu
        };
        
        if (DoclingHasGpu)
        {
            // GPU-optimized: larger chunks, less concurrency (GPU handles parallelism internally)
            config.EnableSplitProcessing = baseConfig.EnableSplitProcessing;
            config.PagesPerChunk = Math.Max(baseConfig.PagesPerChunk, 100);
            config.MaxConcurrentChunks = 1;
            config.MinPagesForSplit = Math.Max(baseConfig.MinPagesForSplit, 150);
        }
        else
        {
            // CPU-optimized: smaller chunks, more concurrency (use CPU cores)
            config.EnableSplitProcessing = true;
            config.PagesPerChunk = Math.Min(baseConfig.PagesPerChunk, 30);
            config.MaxConcurrentChunks = Math.Max(baseConfig.MaxConcurrentChunks, 2);
            config.MinPagesForSplit = Math.Min(baseConfig.MinPagesForSplit, 40);
        }
        
        return config;
    }
    
    /// <summary>
    /// Get the best summarization mode based on available services
    /// </summary>
    public SummarizationMode GetRecommendedMode(SummarizationMode requested)
    {
        if (requested != SummarizationMode.Auto)
            return requested;
        
        // Auto mode selection based on what's available
        if (OllamaAvailable && QdrantAvailable)
            return SummarizationMode.BertRag; // Full pipeline with persistence
        
        if (OllamaAvailable)
            return SummarizationMode.BertHybrid; // BERT extraction + LLM polish
        
        // No LLM available - use pure BERT
        return SummarizationMode.Bert;
    }
}

/// <summary>
/// Features available based on detected services
/// </summary>
public class ServiceFeatures
{
    private readonly DetectedServices _services;
    
    public ServiceFeatures(DetectedServices services) => _services = services;
    
    /// <summary>Can process PDF/DOCX files</summary>
    public bool PdfConversion => _services.DoclingAvailable;
    
    /// <summary>Fast PDF conversion with GPU acceleration</summary>
    public bool FastPdfConversion => _services.DoclingAvailable && _services.DoclingHasGpu;
    
    /// <summary>LLM-based summarization (MapReduce, Iterative, RAG, BertHybrid)</summary>
    public bool LlmSummarization => _services.OllamaAvailable;
    
    /// <summary>Pure extractive summarization (no LLM needed)</summary>
    public bool BertSummarization => _services.OnnxAvailable; // Always true
    
    /// <summary>Persistent vector storage for document caching</summary>
    public bool VectorPersistence => _services.QdrantAvailable;
    
    /// <summary>Cross-session document cache (avoid re-embedding)</summary>
    public bool CrossSessionCache => _services.QdrantAvailable;
    
    /// <summary>Semantic search across documents</summary>
    public bool SemanticSearch => _services.OnnxAvailable && _services.QdrantAvailable;
    
    /// <summary>Document QA with RAG</summary>
    public bool DocumentQA => _services.OllamaAvailable && _services.OnnxAvailable;
    
    /// <summary>Best available mode description</summary>
    public string BestModeDescription
    {
        get
        {
            if (LlmSummarization && VectorPersistence)
                return "Full pipeline (BertRag) - LLM + persistent vectors";
            if (LlmSummarization)
                return "Hybrid mode (BertHybrid) - BERT extraction + LLM polish";
            return "Extractive mode (Bert) - fast, no LLM required";
        }
    }
    
    /// <summary>
    /// Get a detailed explanation of why the current configuration was chosen
    /// </summary>
    public string GetConfigurationReasoning()
    {
        var reasons = new List<string>();
        
        // Embedding backend reasoning
        reasons.Add("Embeddings: ONNX (built-in, zero-config, always available)");
        
        // Summarization backend reasoning
        if (LlmSummarization)
        {
            if (VectorPersistence)
            {
                reasons.Add($"Summarization: BertRag - Ollama detected at {_services.OllamaModel ?? "default"}, Qdrant available for caching");
            }
            else
            {
                reasons.Add($"Summarization: BertHybrid - Ollama detected ({_services.OllamaModel ?? "default"}), no Qdrant (in-memory vectors)");
            }
        }
        else
        {
            reasons.Add("Summarization: BERT only - No LLM server detected, using extractive summarization");
        }
        
        // PDF conversion reasoning
        if (PdfConversion)
        {
            var gpuInfo = FastPdfConversion ? $"GPU ({_services.DoclingAccelerator ?? "CUDA"})" : "CPU";
            reasons.Add($"PDF/DOCX: Docling ({gpuInfo})");
        }
        else
        {
            reasons.Add("PDF/DOCX: Not available - Markdown/text only");
        }
        
        return string.Join("\n", reasons);
    }
}

/// <summary>
/// Detects available services and their capabilities
/// </summary>
public static class ServiceDetector
{
    /// <summary>
    /// Detect all available services
    /// </summary>
    public static async Task<DetectedServices> DetectAsync(DocSummarizerConfig config, bool verbose = false)
    {
        var result = new DetectedServices();
        
        // Run all checks in parallel
        var ollamaTask = CheckOllamaAsync(config.Ollama);
        var doclingTask = CheckDoclingAsync(config.Docling);
        var qdrantTask = CheckQdrantAsync(config.Qdrant);
        
        await Task.WhenAll(ollamaTask, doclingTask, qdrantTask);
        
        // Ollama
        var (ollamaOk, model, models) = await ollamaTask;
        result.OllamaAvailable = ollamaOk;
        result.OllamaModel = model;
        result.AvailableModels = models;
        
        // Docling
        var (doclingOk, hasGpu, accelerator) = await doclingTask;
        result.DoclingAvailable = doclingOk;
        result.DoclingHasGpu = hasGpu;
        result.DoclingAccelerator = accelerator;
        
        // Qdrant
        result.QdrantAvailable = await qdrantTask;
        
        return result;
    }
    
    /// <summary>
    /// Detect services and display results using Spectre.Console
    /// </summary>
    /// <param name="config">Configuration</param>
    /// <param name="verbose">Show verbose output</param>
    /// <param name="forceGpu">Override GPU detection: true=force GPU, false=force CPU, null=auto-detect</param>
    public static async Task<DetectedServices> DetectAndDisplayAsync(DocSummarizerConfig config, bool verbose = false, bool? forceGpu = null)
    {
        VerboseHelper.Log("Detecting services...");
        
        var result = await DetectAsync(config, verbose);
        
        // Apply GPU override if specified
        if (forceGpu.HasValue && result.DoclingAvailable)
        {
            result.DoclingHasGpu = forceGpu.Value;
        }
        
        // Display compact status line
        VerboseHelper.Log($"Services: {result.GetSummary()}");
        
        // Always show the configuration reasoning (why this setup was chosen)
        DisplayConfigurationReasoning(result);
        
        // Show what's available and what's missing
        var tips = new List<string>();
        
        if (!result.OllamaAvailable)
        {
            tips.Add("Tip: Start Ollama for LLM-enhanced summaries: ollama serve");
        }
        
        if (!result.DoclingAvailable)
        {
            tips.Add("Tip: Start Docling for PDF/DOCX support: docker run -p 5001:5001 quay.io/docling-project/docling-serve");
        }
        else if (!result.DoclingHasGpu && !forceGpu.HasValue)
        {
            // Only show CPU tip if user didn't explicitly set GPU mode
            if (verbose)
                tips.Add("Docling running on CPU - use --docling-gpu if you have CUDA");
        }
        
        if (!result.QdrantAvailable)
        {
            if (config.BertRag.PersistVectors || config.BertRag.VectorStore == VectorStoreBackend.Qdrant)
            {
                tips.Add("Tip: Start Qdrant for vector caching: docker run -p 6333:6333 qdrant/qdrant");
            }
        }
        
        // Show tips
        foreach (var tip in tips)
        {
            VerboseHelper.Log($"  {tip}");
        }
        
        // Show verbose details
        if (verbose)
        {
            if (result.DoclingAvailable && result.DoclingHasGpu)
            {
                VerboseHelper.Log($"  Docling accelerator: {result.DoclingAccelerator ?? "CUDA"}");
            }
            if (result.OllamaAvailable && result.AvailableModels.Count > 0)
            {
                var modelList = string.Join(", ", result.AvailableModels.Take(5));
                if (result.AvailableModels.Count > 5)
                    modelList += $", +{result.AvailableModels.Count - 5} more";
                VerboseHelper.Log($"  Models: {modelList}");
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Display clear reasoning for why the current configuration was chosen
    /// </summary>
    private static void DisplayConfigurationReasoning(DetectedServices services)
    {
        VerboseHelper.Log("Configuration selected based on available services:");
        
        // Embedding backend - always ONNX
        VerboseHelper.Log($"  [green]Embeddings:[/] ONNX [dim](built-in, always available)[/]");
        
        // Summarization mode reasoning
        if (services.OllamaAvailable)
        {
            if (services.QdrantAvailable)
            {
                VerboseHelper.Log($"  [green]Summarization:[/] BertRag [dim](Ollama + Qdrant detected = full pipeline with caching)[/]");
            }
            else
            {
                VerboseHelper.Log($"  [green]Summarization:[/] BertHybrid [dim](Ollama detected, no Qdrant = BERT extraction + LLM polish)[/]");
            }
            VerboseHelper.Log($"  [green]LLM Model:[/] {VerboseHelper.Escape(services.OllamaModel ?? "default")}");
        }
        else
        {
            VerboseHelper.Log($"  [yellow]Summarization:[/] BERT only [dim](no LLM detected = extractive summarization, still works!)[/]");
        }
        
        // PDF conversion reasoning
        if (services.DoclingAvailable)
        {
            var gpuInfo = services.DoclingHasGpu ? $"GPU ({services.DoclingAccelerator ?? "CUDA"})" : "CPU";
            VerboseHelper.Log($"  [green]PDF/DOCX:[/] Docling [dim]({gpuInfo})[/]");
        }
        else
        {
            VerboseHelper.Log($"  [yellow]PDF/DOCX:[/] Not available [dim](Markdown and text files only)[/]");
        }
        
        // Vector storage reasoning
        if (services.QdrantAvailable)
        {
            VerboseHelper.Log($"  [green]Vectors:[/] Qdrant [dim](persistent, cross-session caching)[/]");
        }
        else
        {
            VerboseHelper.Log($"  [dim]Vectors:[/] In-memory [dim](no persistence between runs)[/]");
        }
    }
    
    /// <summary>
    /// Detect services silently (no console output)
    /// </summary>
    public static async Task<DetectedServices> DetectSilentAsync(DocSummarizerConfig config)
    {
        return await DetectAsync(config, verbose: false);
    }
    
    private static async Task<(bool available, string? model, List<string> models)> CheckOllamaAsync(OllamaConfig config)
    {
        try
        {
            var ollama = new OllamaService(config.Model, config.BaseUrl);
            var available = await ollama.IsAvailableAsync();
            
            if (!available)
                return (false, null, []);
            
            var models = await ollama.GetAvailableModelsAsync();
            return (true, config.Model, models);
        }
        catch
        {
            return (false, null, []);
        }
    }
    
    private static async Task<(bool available, bool hasGpu, string? accelerator)> CheckDoclingAsync(DoclingConfig config)
    {
        try
        {
            using var client = new DoclingClient(config);
            var available = await client.IsAvailableAsync();
            
            if (!available)
                return (false, false, null);
            
            // Try to detect GPU
            var capabilities = await client.DetectCapabilitiesAsync();
            return (true, capabilities.HasGpu ?? false, capabilities.Accelerator);
        }
        catch
        {
            return (false, false, null);
        }
    }
    
    private static async Task<bool> CheckQdrantAsync(QdrantConfig config)
    {
        try
        {
            var qdrant = new QdrantHttpClient(config.Host, config.Port, config.ApiKey);
            await qdrant.ListCollectionsAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
