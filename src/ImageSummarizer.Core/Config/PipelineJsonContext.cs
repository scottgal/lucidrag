using System.Text.Json.Serialization;

namespace Mostlylucid.DocSummarizer.Images.Config;

/// <summary>
/// JSON source generation context for pipeline configurations
/// Provides zero-allocation, AOT-compatible serialization
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowTrailingCommas = true,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(PipelinesConfig))]
[JsonSerializable(typeof(PipelineConfig))]
[JsonSerializable(typeof(PipelinePhase))]
[JsonSerializable(typeof(PipelineGlobalSettings))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<string>))]
public partial class PipelineJsonContext : JsonSerializerContext
{
}
