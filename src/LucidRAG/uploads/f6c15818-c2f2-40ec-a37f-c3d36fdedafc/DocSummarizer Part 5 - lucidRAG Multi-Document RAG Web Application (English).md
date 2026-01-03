# DocSummarizer Part 5 - lucidRAG: Multi-Document RAG Web Application (English)

> Source: https://www.mostlylucid.net/blog/lucidrag-multi-document-rag-web-app

Notification message

DocSummarizer Part 5 - lucidRAG: Multi-Document RAG Web Application (English)

            DocSummarizer Part 5 - lucidRAG: Multi-Document RAG Web Application

                Thursday, 01 January 2026

            //

                8 minute read

                     Comments

                     Raw

                     Edit

    AI

    C#

    DuckDB

    GraphRAG

    HTMX

    LLM

    RAG

    Semantic Search

        This is Part 5 of the DocSummarizer series, and it's also the culmination of the GraphRAG series and Semantic Search series. We're combining everything into a deployable web application.

🚨🚨 PREVIEW ARTICLE 🚨🚨 Still working out some kinks and adding features. But the core is done and working well. Expect updates over the next few weeks. It WILL be at lucidRAG.com. I'll add screenshots here once I bottom out the design.

The whole point of building RAG infrastructure is to use it for something real.

Over the past few weeks, we've built:

DocSummarizer - Document parsing, semantic chunking, ONNX embeddings
GraphRAG - Entity extraction, knowledge graphs, community detection
Semantic Search - BM25 + BERT hybrid search with RRF fusion

Now we wire them together into lucidRAG - a standalone web application for multi-document question answering with knowledge graph visualization.
Website: lucidrag.com | Source: GitHub

What lucidRAG Does
Upload documents. Ask questions. Get answers with citations and a knowledge graph showing how concepts connect.
Key features:

Multi-document upload with drag-and-drop
Agentic RAG - Query decomposition and self-correction (bounded, deterministic in structure, single request lifecycle)
Knowledge graph visualization - See entity relationships
Evidence view - Sentence-level source citations
Standalone deployment - Single executable or Docker

Design constraints:

No cloud dependency for indexing
Deterministic preprocessing (chunking, embedding, entity extraction)
Rebuildable vector state from source documents
LLMs used only for answer synthesis over retrieved evidence

At no point are LLMs used for chunking, embedding, entity extraction, or storage — only for synthesizing answers over retrieved, citation-backed evidence.

Why Combine Vector Search + Knowledge Graphs?
Vector search alone breaks down for certain query types:

Query Type
Vector Search Problem
Graph Solution

Cross-document
"How does X relate to Y?"
Entity linking across docs

Entity-centric
"What about Docker?"
Graph traversal from entity

Global summaries
"Main themes?"
Community detection

lucidRAG uses both: vectors for precision, graphs for context. Graph queries are depth-limited (max 2 hops) and scoped to retrieved documents to prevent unbounded traversal on large corpora.
Architecture Overview
The app layers three projects we've already built:
lucidRAG
├── Controllers/Api/    # REST endpoints
├── Services/           # Business logic
│   ├── DocumentProcessingService   # Wraps DocSummarizer
│   ├── EntityGraphService          # Wraps GraphRAG
│   └── Background/                 # Async queue processing
└── Views/              # HTMX + Alpine.js UI

The Processing Pipeline
When you upload a document, it flows through three stages:
Stage 1: Upload and Queue
The upload endpoint validates the file, computes a content hash for deduplication, and queues it for background processing:
public async Task<Guid> QueueDocumentAsync(Stream fileStream, string fileName)
{
    // Compute hash to detect duplicates
    var contentHash = ComputeHash(fileStream);

    var existing = await _db.Documents
        .FirstOrDefaultAsync(d => d.ContentHash == contentHash);
    if (existing != null)
        return existing.Id; // Already processed

The key insight: we hash first, save later. This prevents wasting processing time on duplicate uploads.
    // Save to disk, create DB record
    var docId = Guid.NewGuid();
    await SaveFileToDiskAsync(fileStream, docId, fileName);

    // Queue for background processing
    await _queue.EnqueueAsync(new DocumentProcessingJob(docId, filePath));

    return docId;
}

Stage 2: Chunking and Embedding
The background processor picks up queued documents and runs them through DocSummarizer:
var result = await _summarizer.SummarizeFileAsync(job.FilePath, progressChannel);

This single line does a lot of work (see DocSummarizer Part 1):

Parse the document structure (PDF, DOCX, Markdown)
Split into semantic chunks respecting headings
Generate ONNX embeddings for each chunk
Store vectors in DuckDB with HNSW indexing

Stage 3: Entity Extraction
After chunking, we extract entities using GraphRAG's heuristic approach:
var segments = await _vectorStore.GetDocumentSegmentsAsync(documentId);
var entityResult = await _entityGraph.ExtractAndStoreEntitiesAsync(documentId, segments);

This uses IDF scoring and structural signals rather than per-chunk LLM calls - see GraphRAG Part 2 for details.
Bounded Channels for Backpressure
A naive implementation would use unbounded queues, risking out-of-memory crashes during upload floods. We use bounded channels with explicit capacity limits:
private readonly Channel<DocumentProcessingJob> _queue =
    Channel.CreateBounded<DocumentProcessingJob>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.Wait
    });

When the queue fills up, Wait mode blocks new writes until space opens. We add a timeout so users get a clear error instead of hanging:
using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

try {
    await _queue.Writer.WriteAsync(job, timeoutCts.Token);
} catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
    throw new InvalidOperationException("Queue full. Try again later.");
}

Per-Document Timeouts
Large documents can take minutes to process. But a stuck document shouldn't block the entire queue. Each document gets its own timeout:
while (!stoppingToken.IsCancellationRequested)
{
    var job = await _queue.DequeueAsync(stoppingToken);

    // 30-minute timeout per document
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    timeoutCts.CancelAfter(TimeSpan.FromMinutes(30));

    try {
        await ProcessDocumentAsync(job, timeoutCts.Token);
    } catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) {
        await MarkDocumentFailedAsync(job.DocumentId, "Processing timed out");
    }
}

The linked token ensures we still respect application shutdown while adding the per-document limit.
Progress Channel Cleanup
Each processing document gets a progress channel for SSE updates. But if a user closes their browser mid-upload, that channel becomes orphaned. We track creation times and clean up periodically:
private readonly ConcurrentDictionary<Guid, ProgressChannelEntry> _progressChannels = new();

public int CleanupAbandonedChannels()
{
    var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
    var cleaned = 0;

    foreach (var kvp in _progressChannels.Where(x => x.Value.CreatedAt < cutoff))
    {
        if (_progressChannels.TryRemove(kvp.Key, out var entry))
        {
            entry.Channel.Writer.TryComplete();
            cleaned++;
        }
    }
    return cleaned;
}

A PeriodicTimer calls this every 15 minutes in the background processor.
Storage: DuckDB + PostgreSQL/SQLite
We use two databases for different purposes:
PostgreSQL/SQLite (EF Core) stores document metadata - what exists, processing status, relationships. This data is durable and queryable.
DuckDB stores vectors and the entity graph. It's ephemeral - you can rebuild it from source documents. This separation means vector store corruption doesn't lose your document inventory.
// Metadata in PostgreSQL
public class DocumentEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string ContentHash { get; set; }
    public DocumentStatus Status { get; set; }
}

// Vectors in DuckDB (managed by DocSummarizer)
// Entities in DuckDB (managed by GraphRAG)

The Chat API
Questions flow through the agentic search pipeline:
[HttpPost]
public async Task<IActionResult> ChatAsync([FromBody] ChatRequest request)
{
    // 1. Get or create conversation for memory
    var conversation = await GetOrCreateConversationAsync(request.ConversationId);

    // 2. Search with hybrid retrieval
    var searchResult = await _search.SearchAsync(request.Query, new SearchOptions
    {
        TopK = 10,
        IncludeGraphData = request.IncludeGraphData
    });

The search service handles query decomposition if needed, then synthesizes an answer:
    // 3. Generate answer with LLM
    var answer = await _summarizer.SummarizeAsync(
        request.Query,
        searchResult.Segments,
        new SummarizeOptions { IncludeCitations = true });

    // 4. Save to conversation history
    await SaveToConversationAsync(conversation.Id, request.Query, answer);

    return Ok(new ChatResponse
    {
        Answer = answer.Text,
        Sources = answer.Citations,
        GraphData = searchResult.GraphData
    });
}

The UI: HTMX + Alpine.js
The UI is a single page with documents on the left, chat on the right:
┌──────────────────┬─────────────────────────────────────┐
│  📁 Documents    │  💬 Chat                            │
│  ─────────────   │  [Answer] [Evidence] [Graph]       │
│  [+ Upload]      │                                     │
│  📄 api-docs.pdf │  Q: How does auth work?            │
│  📝 readme.md    │  A: JWT tokens stored... [1][2]    │
│  ─────────────   │                                     │
│  🕸️ Graph: 168   │  ┌─────────────────────────────┐   │
│                  │  │ Ask about your documents... │   │
└──────────────────┴──┴─────────────────────────────┴───┘

Alpine.js manages state; HTMX handles document list updates:
function ragApp() {
    return {
        messages: [],
        isTyping: false,

        async sendMessage() {
            const query = this.currentMessage.trim();
            this.messages.push({ role: 'user', content: query });
            this.isTyping = true;

            const result = await fetch('/api/chat', {
                method: 'POST',
                body: JSON.stringify({ query })
            }).then(r => r.json());

            this.messages.push({
                role: 'assistant',
                content: result.answer,
                sources: result.sources
            });
            this.isTyping = false;
        }
    };
}

Demo Mode
For public deployments like lucidrag.com, demo mode disables uploads and uses pre-loaded content. Demo mode exists to make public deployments safe, deterministic, and cheap without special-case code paths:
public class DemoModeConfig
{
    public bool Enabled { get; set; } = false;
    public string ContentPath { get; set; } = "./demo-content";
    public string BannerMessage { get; set; } = "Demo Mode: Pre-loaded RAG articles";
}

A DemoContentSeeder background service watches the content directory and processes any dropped files:
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    if (!_config.DemoMode.Enabled) return;

    await SeedExistingContentAsync();
    StartFileWatcher(_config.DemoMode.ContentPath);
}

This lets you update demo content by simply copying files - no restart needed.
Running lucidRAG
Standalone (No Dependencies)
dotnet run --project Mostlylucid.RagDocuments -- --standalone

Uses SQLite + DuckDB locally. Open http://localhost:5080.
Docker
services:
  lucidrag:
    build: .
    ports: ["5080:8080"]
    depends_on: [postgres, ollama]

What Actually Runs

Component
Source
Purpose

Document parsing
DocSummarizer
PDF, DOCX, Markdown

ONNX embeddings
DocSummarizer
Local, no API keys

Entity extraction
GraphRAG
IDF + structural signals

Hybrid search
Both
BM25 + BERT with RRF

Async processing
New
Bounded channels, timeouts

Web UI
New
HTMX + Alpine.js

Cost
Zero API costs for indexing - embeddings are ONNX, entities are heuristic. You only pay for LLM synthesis at query time, and that works with local Ollama.
Related Articles

DocSummarizer Part 1 - Architecture
DocSummarizer Part 4 - RAG Pipelines
GraphRAG Part 2 - Implementation
Semantic Search with ONNX

             Add Comment

    © 2025 Scott Galloway — 
    Unlicense — 
    All content and source code on this site is free to use, copy, modify, and sell.
