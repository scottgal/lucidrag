using System.Text.Json;
using System.Text.Json.Serialization;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.Config;

/// <summary>
///     JSON serialization context for AOT compilation
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
// Config types
[JsonSerializable(typeof(DocSummarizerConfig))]
[JsonSerializable(typeof(OllamaConfig))]
[JsonSerializable(typeof(DoclingConfig))]
[JsonSerializable(typeof(QdrantConfig))]
[JsonSerializable(typeof(ProcessingConfig))]
[JsonSerializable(typeof(OutputConfig))]
[JsonSerializable(typeof(BatchConfig))]
[JsonSerializable(typeof(OutputFormat))]
[JsonSerializable(typeof(ChunkCacheConfig))]
// Document types
[JsonSerializable(typeof(DocumentSummary))]

[JsonSerializable(typeof(DocumentChunk))]
[JsonSerializable(typeof(ChunkSummary))]
[JsonSerializable(typeof(TopicSummary))]
[JsonSerializable(typeof(SummarizationTrace))]
[JsonSerializable(typeof(ChunkIndexEntry))]
[JsonSerializable(typeof(List<ChunkIndexEntry>))]
[JsonSerializable(typeof(ValidationResult))]
[JsonSerializable(typeof(SummarizationMode))]
[JsonSerializable(typeof(BatchResult))]
[JsonSerializable(typeof(BatchSummary))]
// Docling types
[JsonSerializable(typeof(DoclingResponse))]
[JsonSerializable(typeof(DoclingDocument))]
[JsonSerializable(typeof(DoclingTaskResponse))]
[JsonSerializable(typeof(DoclingStatusResponse))]
[JsonSerializable(typeof(DoclingResultResponse))]
[JsonSerializable(typeof(ModelInfo), TypeInfoPropertyName = "DocSummarizerModelInfo")]
// Ollama HTTP client types (replacing OllamaSharp)
[JsonSerializable(typeof(OllamaGenerateRequest))]
[JsonSerializable(typeof(OllamaGenerateResponse))]
[JsonSerializable(typeof(OllamaEmbedRequest))]
[JsonSerializable(typeof(OllamaEmbedResponse))]
[JsonSerializable(typeof(OllamaShowRequest))]
[JsonSerializable(typeof(OllamaShowResponse))]
[JsonSerializable(typeof(OllamaModelDetails))]
[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSerializable(typeof(OllamaModelInfo))]
[JsonSerializable(typeof(OllamaOptions))]
// Qdrant HTTP client types
[JsonSerializable(typeof(QdrantUpsertRequest))]
[JsonSerializable(typeof(QdrantPoint))]
[JsonSerializable(typeof(QdrantSearchRequest))]
[JsonSerializable(typeof(QdrantSearchResult))]
[JsonSerializable(typeof(QdrantCollectionsResponse))]
[JsonSerializable(typeof(QdrantCollectionsResult))]
[JsonSerializable(typeof(QdrantCollectionInfo))]
[JsonSerializable(typeof(QdrantSearchResponse))]
[JsonSerializable(typeof(QdrantCreateCollectionRequest))]
[JsonSerializable(typeof(QdrantVectorConfig))]
[JsonSerializable(typeof(QdrantCollectionDetailsResponse))]
[JsonSerializable(typeof(QdrantCollectionDetails))]
// Template types
[JsonSerializable(typeof(SummaryTemplate))]
[JsonSerializable(typeof(OutputStyle))]
[JsonSerializable(typeof(SummaryTone))]
[JsonSerializable(typeof(AudienceLevel))]
[JsonSerializable(typeof(ChunkCacheMetadata))]
// Web fetch types
[JsonSerializable(typeof(WebFetchConfig))]
[JsonSerializable(typeof(WebFetchMode))]
// Tool output types (for LLM integration)
[JsonSerializable(typeof(ToolOutput))]
[JsonSerializable(typeof(ToolSummary))]
[JsonSerializable(typeof(ToolAnswer))]
[JsonSerializable(typeof(ToolTopic))]
[JsonSerializable(typeof(ToolEntities))]
[JsonSerializable(typeof(ToolMetadata))]
[JsonSerializable(typeof(GroundedClaim))]
[JsonSerializable(typeof(List<GroundedClaim>))]
[JsonSerializable(typeof(List<ToolTopic>))]
// Common types
[JsonSerializable(typeof(List<OllamaModelInfo>))]
[JsonSerializable(typeof(List<QdrantPoint>))]
[JsonSerializable(typeof(List<QdrantSearchResult>))]
[JsonSerializable(typeof(List<QdrantCollectionInfo>))]
[JsonSerializable(typeof(List<double>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(float[]))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
public partial class DocSummarizerJsonContext : JsonSerializerContext
{
}