# Mostlylucid.DocSummarizer

Local-first document summarization library using BERT embeddings, RAG retrieval, and optional LLM synthesis.

## Features

- **Local-first**: Runs entirely offline using ONNX models - no API keys required
- **Citation grounding**: Every claim is traceable to source segments
- **Multiple modes**: Pure BERT extraction, hybrid BERT+LLM, full RAG pipeline
- **Format support**: Markdown, PDF, DOCX, HTML, URLs
- **Vector storage**: In-memory, DuckDB (embedded), or Qdrant (external)
- **Multi-framework**: .NET 8, .NET 9, and .NET 10 support
- **OpenTelemetry**: Built-in distributed tracing and metrics
- **Resilience**: Polly-based retry, circuit breaker, and rate limiting

## Installation

```bash
dotnet add package Mostlylucid.DocSummarizer
```

## Quick Start

```csharp
// Register in DI
builder.Services.AddDocSummarizer();

// Inject and use
public class MyService(IDocumentSummarizer summarizer)
{
    public async Task<string> GetSummaryAsync(string markdown)
    {
        var result = await summarizer.SummarizeMarkdownAsync(markdown);
        return result.ExecutiveSummary;
    }
}
```

## Configuration

### Basic Configuration

```csharp
builder.Services.AddDocSummarizer(options =>
{
    // Use local ONNX embeddings (default, no external services)
    options.EmbeddingBackend = EmbeddingBackend.Onnx;

    // Or use Ollama for embeddings
    options.EmbeddingBackend = EmbeddingBackend.Ollama;
    options.Ollama.BaseUrl = "http://localhost:11434";
    options.Ollama.EmbedModel = "nomic-embed-text";
});
```

### From Configuration File

```json
{
  "DocSummarizer": {
    "EmbeddingBackend": "Onnx",
    "BertRag": {
      "VectorStore": "DuckDB",
      "ReindexOnStartup": false,
      "CollectionName": "my-documents"
    },
    "Onnx": {
      "EmbeddingModel": "AllMiniLmL6V2"
    }
  }
}
```

```csharp
builder.Services.AddDocSummarizer(
    builder.Configuration.GetSection("DocSummarizer"));
```

## Embedding Models

| Model | Dimensions | Max Tokens | Size | Use Case |
|-------|-----------|------------|------|----------|
| `AllMiniLmL6V2` | 384 | 256 | ~23MB | Fast general-purpose (default) |
| `BgeSmallEnV15` | 384 | 512 | ~34MB | Best quality for size |
| `GteSmall` | 384 | 512 | ~34MB | Good all-around |
| `MultiQaMiniLm` | 384 | 512 | ~23MB | QA-optimized |
| `ParaphraseMiniLmL3` | 384 | 128 | ~17MB | Smallest/fastest |

## Summarization Modes

| Mode | LLM Required | Best For |
|------|-------------|----------|
| `Bert` | No | Fast extraction, offline use |
| `BertHybrid` | Yes | Balance of speed and fluency |
| `BertRag` | Yes | Production systems, large documents |
| `Auto` | Varies | Automatic mode selection |

```csharp
// Pure BERT - no LLM needed, fastest
var summary = await summarizer.SummarizeMarkdownAsync(
    markdown,
    mode: SummarizationMode.Bert);

// BertRag - full pipeline with LLM synthesis
var summary = await summarizer.SummarizeMarkdownAsync(
    markdown,
    focusQuery: "What are the key architectural decisions?",
    mode: SummarizationMode.BertRag);
```

## Vector Store Backends

```csharp
// In-memory (no persistence, fastest)
options.BertRag.VectorStore = VectorStoreBackend.InMemory;

// DuckDB (embedded file-based, default)
options.BertRag.VectorStore = VectorStoreBackend.DuckDB;

// Qdrant (external server, best for production)
options.BertRag.VectorStore = VectorStoreBackend.Qdrant;
options.Qdrant.Host = "localhost";
options.Qdrant.Port = 6334;
```

## Output Models

### DocumentSummary

```csharp
record DocumentSummary(
    string ExecutiveSummary,           // Main summary text
    List<TopicSummary> TopicSummaries, // Topic-by-topic breakdown
    List<string> OpenQuestions,        // Questions that couldn't be answered
    SummarizationTrace Trace,          // Processing metadata
    ExtractedEntities? Entities);      // Named entities (people, places, etc.)
```

## Query Mode

Ask questions about documents with evidence-grounded answers:

```csharp
var answer = await summarizer.QueryAsync(
    markdown: documentContent,
    question: "What database technology is recommended?");

Console.WriteLine(answer.Answer);
Console.WriteLine($"Confidence: {answer.Confidence}");

foreach (var evidence in answer.Evidence)
{
    Console.WriteLine($"  [{evidence.SegmentId}] {evidence.Text}");
}
```

## Segment Extraction

Extract segments without summarizing - useful for building search indexes:

```csharp
var extraction = await summarizer.ExtractSegmentsAsync(markdown);

foreach (var segment in extraction.TopBySalience)
{
    Console.WriteLine($"[{segment.Type}] {segment.Text}");
    Console.WriteLine($"  Salience: {segment.SalienceScore:F2}");
}
```

## OpenTelemetry Observability

The library includes built-in OpenTelemetry instrumentation for distributed tracing and metrics. All instrumentation follows OpenTelemetry semantic conventions.

### Activity Sources (Distributed Tracing)

| Source Name | Activities | Description |
|-------------|------------|-------------|
| `Mostlylucid.DocSummarizer` | `SummarizeMarkdown`, `SummarizeFile`, `SummarizeUrl`, `Query` | Main summarization operations |
| `Mostlylucid.DocSummarizer.Ollama` | `OllamaGenerate`, `OllamaEmbed` | LLM API calls |
| `Mostlylucid.DocSummarizer.WebFetcher` | `WebFetch`, `FetchWithSecurity` | Web content fetching |

Each activity includes relevant tags (e.g., `url.host`, `http.response.status_code`, `error.type`) for filtering and analysis.

### Metrics

#### DocumentSummarizerService Metrics

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `docsummarizer.summarizations` | Counter | requests | Total summarization requests |
| `docsummarizer.queries` | Counter | requests | Total query requests |
| `docsummarizer.summarization.duration` | Histogram | ms | Summarization duration |
| `docsummarizer.document.size` | Histogram | bytes | Document sizes processed |
| `docsummarizer.errors` | Counter | errors | Total errors by type |

#### OllamaService Metrics

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `docsummarizer.ollama.generate.requests` | Counter | requests | LLM generation requests |
| `docsummarizer.ollama.embed.requests` | Counter | requests | Embedding requests |
| `docsummarizer.ollama.generate.duration` | Histogram | ms | Generation duration |
| `docsummarizer.ollama.embed.duration` | Histogram | ms | Embedding duration |
| `docsummarizer.ollama.prompt.tokens` | Histogram | tokens | Prompt token counts |
| `docsummarizer.ollama.response.tokens` | Histogram | tokens | Response token counts |
| `docsummarizer.ollama.errors` | Counter | errors | LLM errors by type |
| `docsummarizer.ollama.circuit_breaker` | Counter | transitions | Circuit breaker state changes |

#### WebFetcher Metrics

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `docsummarizer.webfetch.requests` | Counter | requests | Web fetch requests |
| `docsummarizer.webfetch.duration` | Histogram | ms | Fetch duration |
| `docsummarizer.webfetch.errors` | Counter | errors | Fetch errors by type |
| `docsummarizer.webfetch.retries` | Counter | retries | Retry attempts |
| `docsummarizer.webfetch.ratelimits` | Counter | responses | HTTP 429 rate limit hits |
| `docsummarizer.webfetch.circuit_breaker` | Counter | transitions | Circuit breaker state changes |

### Metric Dimensions (Tags)

Common tags available on metrics:

| Tag | Metrics | Values |
|-----|---------|--------|
| `mode` | summarizations, webfetch | `Bert`, `BertRag`, `MapReduce`, `Simple`, `Playwright` |
| `error.type` | errors | `security`, `http`, `timeout`, `operation`, `unknown` |
| `url.host` | webfetch | Target hostname |
| `model` | ollama | LLM model name |
| `state` | circuit_breaker | `opened`, `closed`, `half-opened` |
| `attempt` | retries | Retry attempt number |

### Example: Wire up in ASP.NET Core

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("Mostlylucid.DocSummarizer")
        .AddSource("Mostlylucid.DocSummarizer.Ollama")
        .AddSource("Mostlylucid.DocSummarizer.WebFetcher")
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter("Mostlylucid.DocSummarizer")
        .AddMeter("Mostlylucid.DocSummarizer.Ollama")
        .AddMeter("Mostlylucid.DocSummarizer.WebFetcher")
        .AddOtlpExporter());
```

### Example: Console Application

```csharp
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource("Mostlylucid.DocSummarizer")
    .AddSource("Mostlylucid.DocSummarizer.Ollama")
    .AddSource("Mostlylucid.DocSummarizer.WebFetcher")
    .AddConsoleExporter()
    .Build();

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter("Mostlylucid.DocSummarizer")
    .AddMeter("Mostlylucid.DocSummarizer.Ollama")
    .AddMeter("Mostlylucid.DocSummarizer.WebFetcher")
    .AddConsoleExporter()
    .Build();
```

### Grafana/Jaeger Integration

Send telemetry to Jaeger or Grafana Tempo:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("Mostlylucid.DocSummarizer")
        .AddSource("Mostlylucid.DocSummarizer.Ollama")
        .AddSource("Mostlylucid.DocSummarizer.WebFetcher")
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://jaeger:4317")))
    .WithMetrics(metrics => metrics
        .AddMeter("Mostlylucid.DocSummarizer")
        .AddMeter("Mostlylucid.DocSummarizer.Ollama")
        .AddMeter("Mostlylucid.DocSummarizer.WebFetcher")
        .AddPrometheusExporter());  // For Prometheus/Grafana
```

## HTTP Resilience

The WebFetcher service includes Polly-based resilience:

- **Circuit Breaker**: Opens after 5 failures in 30s, stays open for 60s
- **Retry**: Exponential backoff with jitter (3 attempts, 0.5s-30s)
- **Rate Limiting**: Respects HTTP 429 Retry-After headers
- **Permanent Failures**: 403/404/401 fail immediately with clear messages

## Dependencies

- **Supported**: .NET 8.0+, .NET 9.0+, .NET 10.0+
- **Included**: ONNX Runtime, Markdig, PdfPig, OpenXml, AngleSharp, Polly, OpenTelemetry.Api
- **Optional**: Ollama (for LLM synthesis), Docling (for complex PDF conversion)

## License

MIT
