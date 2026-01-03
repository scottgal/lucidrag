using Mostlylucid.GraphRag.Extraction;
using Mostlylucid.GraphRag.Graph;
using Mostlylucid.GraphRag.Indexing;
using Mostlylucid.GraphRag.Query;
using Mostlylucid.GraphRag.Search;
using Mostlylucid.GraphRag.Services;
using Mostlylucid.GraphRag.Storage;

namespace Mostlylucid.GraphRag;

/// <summary>
/// Main orchestrator for the GraphRAG pipeline.
/// Coordinates indexing, entity extraction, community detection, and querying.
/// </summary>
public class GraphRagPipeline : IDisposable
{
    private readonly GraphRagDb _db;
    private readonly EmbeddingService _embedder;
    private readonly OllamaClient _llm;
    private readonly SearchService _search;
    private readonly QueryEngine _queryEngine;
    private readonly GraphRagConfig _config;

    public GraphRagPipeline(GraphRagConfig config)
    {
        _config = config;
        _db = new GraphRagDb(config.DatabasePath, config.EmbeddingDimension);
        _embedder = new EmbeddingService();
        _llm = new OllamaClient(config.OllamaUrl, config.Model);
        _search = new SearchService(_db, _embedder);
        _queryEngine = new QueryEngine(_db, _search, _llm);
    }

    /// <summary>
    /// Initialize the database and embedding model
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _db.InitializeAsync();
        await _embedder.InitializeAsync(ct);
    }

    /// <summary>
    /// Run the full indexing pipeline on a directory of markdown files
    /// </summary>
    public async Task IndexAsync(string markdownPath, IProgress<PipelineProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Phase 1: Index documents and create chunks with embeddings
        progress?.Report(new PipelineProgress(PipelinePhase.Indexing, 0, "Starting document indexing..."));
        
        var indexer = new MarkdownIndexer(_db, _embedder);
        await indexer.IndexDirectoryAsync(markdownPath, 
            new Progress<IndexProgress>(p => progress?.Report(
                new PipelineProgress(PipelinePhase.Indexing, p.Percentage, p.Message))), ct);

        // Phase 2: Extract entities using selected mode
        var modeLabel = _config.ExtractionMode switch
        {
            ExtractionMode.Llm => "LLM (MSFT-style)",
            ExtractionMode.Hybrid => "Hybrid (heuristic + LLM)",
            _ => "Heuristic"
        };
        progress?.Report(new PipelineProgress(PipelinePhase.EntityExtraction, 0, 
            $"Extracting entities ({modeLabel})..."));
        
        IEntityExtractor extractor = _config.ExtractionMode switch
        {
            ExtractionMode.Llm => new LlmEntityExtractor(_db, _embedder, _llm),
            ExtractionMode.Hybrid => new HybridEntityExtractor(_db, _embedder, _llm),
            _ => new EntityExtractor(_db, _embedder, _llm, _config.ExtractionMode)
        };
        
        var extractionResult = await extractor.ExtractAsync(
            new Progress<ProgressInfo>(p => progress?.Report(
                new PipelineProgress(PipelinePhase.EntityExtraction, p.Percentage, p.Message))), ct);

        progress?.Report(new PipelineProgress(PipelinePhase.EntityExtraction, 100,
            $"Extracted {extractionResult.EntitiesExtracted} entities, {extractionResult.RelationshipsExtracted} rels ({extractionResult.LlmCallCount} LLM calls)"));

        // Phase 3: Detect communities and generate summaries
        progress?.Report(new PipelineProgress(PipelinePhase.CommunityDetection, 0, "Detecting communities..."));
        
        var detector = new CommunityDetector(_db, _llm);
        await detector.DetectAndSummarizeAsync(
            new Progress<ProgressInfo>(p =>
            {
                var phase = p.Message.Contains("Summariz") ? PipelinePhase.Summarization : PipelinePhase.CommunityDetection;
                progress?.Report(new PipelineProgress(phase, p.Percentage, p.Message));
            }), ct);

        // Initialize BM25 for search
        await _search.InitializeBM25Async();

        progress?.Report(new PipelineProgress(PipelinePhase.Complete, 100, "Indexing complete!"));
    }

    /// <summary>
    /// Query the indexed corpus
    /// </summary>
    public Task<QueryResult> QueryAsync(string query, QueryMode? mode = null, 
        CancellationToken ct = default)
        => _queryEngine.QueryAsync(query, mode, ct);

    /// <summary>
    /// Get database statistics
    /// </summary>
    public Task<DbStats> GetStatsAsync() => _db.GetStatsAsync();

    public void Dispose()
    {
        _db.Dispose();
        _embedder.Dispose();
        _llm.Dispose();
    }
}
