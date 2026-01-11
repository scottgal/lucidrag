using YamlDotNet.Serialization;

namespace LucidRAG.Manifests;

/// <summary>
/// YAML manifest for a document processor component.
/// Processors handle document parsing, chunking, embedding, and enrichment.
/// Follows StyloFlow manifest pattern.
/// </summary>
public sealed class ProcessorManifest
{
    /// <summary>
    /// Unique processor identifier (e.g., "pdf_parser", "markdown_chunker", "entity_extractor")
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Display name for UI
    /// </summary>
    [YamlMember(Alias = "display_name")]
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Processor description
    /// </summary>
    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// Semantic version
    /// </summary>
    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Priority for execution ordering
    /// </summary>
    [YamlMember(Alias = "priority")]
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Whether this processor is enabled
    /// </summary>
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Tags for categorization
    /// </summary>
    [YamlMember(Alias = "tags")]
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Taxonomy classification
    /// </summary>
    [YamlMember(Alias = "taxonomy")]
    public ProcessorTaxonomy Taxonomy { get; set; } = new();

    /// <summary>
    /// Input contract - document types this processor accepts
    /// </summary>
    [YamlMember(Alias = "input")]
    public ProcessorInputContract Input { get; set; } = new();

    /// <summary>
    /// Output contract - what this processor produces
    /// </summary>
    [YamlMember(Alias = "output")]
    public ProcessorOutputContract Output { get; set; } = new();

    /// <summary>
    /// Capabilities of this processor
    /// </summary>
    [YamlMember(Alias = "capabilities")]
    public ProcessorCapabilities Capabilities { get; set; } = new();

    /// <summary>
    /// Default configuration values
    /// </summary>
    [YamlMember(Alias = "defaults")]
    public ProcessorDefaultsConfig Defaults { get; set; } = new();

    /// <summary>
    /// Runtime type binding
    /// </summary>
    [YamlMember(Alias = "runtime")]
    public RuntimeConfig? Runtime { get; set; }
}

/// <summary>
/// Processor taxonomy
/// </summary>
public sealed class ProcessorTaxonomy
{
    /// <summary>
    /// Processor kind
    /// </summary>
    [YamlMember(Alias = "kind")]
    public ProcessorKind Kind { get; set; } = ProcessorKind.Parser;

    /// <summary>
    /// Processing stage
    /// </summary>
    [YamlMember(Alias = "stage")]
    public ProcessorStage Stage { get; set; } = ProcessorStage.Preprocessing;

    /// <summary>
    /// Determinism level
    /// </summary>
    [YamlMember(Alias = "determinism")]
    public WaveDeterminism Determinism { get; set; } = WaveDeterminism.Deterministic;

    /// <summary>
    /// Persistence strategy
    /// </summary>
    [YamlMember(Alias = "persistence")]
    public WavePersistence Persistence { get; set; } = WavePersistence.DirectWrite;
}

/// <summary>
/// Processor kind
/// </summary>
public enum ProcessorKind
{
    /// <summary>
    /// Document parsing (PDF, DOCX, etc.)
    /// </summary>
    Parser,

    /// <summary>
    /// Text chunking/segmentation
    /// </summary>
    Chunker,

    /// <summary>
    /// Embedding generation
    /// </summary>
    Embedder,

    /// <summary>
    /// Entity extraction
    /// </summary>
    Extractor,

    /// <summary>
    /// Content enrichment (metadata, tags)
    /// </summary>
    Enricher,

    /// <summary>
    /// Validation/quality check
    /// </summary>
    Validator
}

/// <summary>
/// Processing stage
/// </summary>
public enum ProcessorStage
{
    /// <summary>
    /// Pre-processing (before main processing)
    /// </summary>
    Preprocessing,

    /// <summary>
    /// Main processing stage
    /// </summary>
    Processing,

    /// <summary>
    /// Post-processing (enrichment, validation)
    /// </summary>
    Postprocessing
}

/// <summary>
/// Processor input contract
/// </summary>
public sealed class ProcessorInputContract
{
    /// <summary>
    /// Supported MIME types
    /// </summary>
    [YamlMember(Alias = "mime_types")]
    public List<string> MimeTypes { get; set; } = new();

    /// <summary>
    /// Supported file extensions
    /// </summary>
    [YamlMember(Alias = "file_extensions")]
    public List<string> FileExtensions { get; set; } = new();

    /// <summary>
    /// Entity types this processor accepts
    /// </summary>
    [YamlMember(Alias = "accepts")]
    public List<string> Accepts { get; set; } = new();

    /// <summary>
    /// Maximum file size in bytes
    /// </summary>
    [YamlMember(Alias = "max_file_size")]
    public long? MaxFileSize { get; set; }
}

/// <summary>
/// Processor output contract
/// </summary>
public sealed class ProcessorOutputContract
{
    /// <summary>
    /// Entity types this processor produces
    /// </summary>
    [YamlMember(Alias = "produces")]
    public List<string> Produces { get; set; } = new();

    /// <summary>
    /// Metadata fields emitted
    /// </summary>
    [YamlMember(Alias = "metadata")]
    public List<string> Metadata { get; set; } = new();

    /// <summary>
    /// Signals emitted
    /// </summary>
    [YamlMember(Alias = "signals")]
    public List<WaveSignalSpec> Signals { get; set; } = new();
}

/// <summary>
/// Processor capabilities
/// </summary>
public sealed class ProcessorCapabilities
{
    /// <summary>
    /// Supports batch processing
    /// </summary>
    [YamlMember(Alias = "batch_processing")]
    public bool BatchProcessing { get; set; } = false;

    /// <summary>
    /// Supports streaming/incremental processing
    /// </summary>
    [YamlMember(Alias = "streaming")]
    public bool Streaming { get; set; } = false;

    /// <summary>
    /// Supports OCR for scanned documents
    /// </summary>
    [YamlMember(Alias = "ocr")]
    public bool Ocr { get; set; } = false;

    /// <summary>
    /// Supports table extraction
    /// </summary>
    [YamlMember(Alias = "table_extraction")]
    public bool TableExtraction { get; set; } = false;

    /// <summary>
    /// Supports image extraction
    /// </summary>
    [YamlMember(Alias = "image_extraction")]
    public bool ImageExtraction { get; set; } = false;

    /// <summary>
    /// Supports metadata extraction
    /// </summary>
    [YamlMember(Alias = "metadata_extraction")]
    public bool MetadataExtraction { get; set; } = true;
}

/// <summary>
/// Processor defaults configuration
/// </summary>
public sealed class ProcessorDefaultsConfig
{
    /// <summary>
    /// Chunking parameters
    /// </summary>
    [YamlMember(Alias = "chunking")]
    public Dictionary<string, object> Chunking { get; set; } = new();

    /// <summary>
    /// Embedding parameters
    /// </summary>
    [YamlMember(Alias = "embedding")]
    public Dictionary<string, object> Embedding { get; set; } = new();

    /// <summary>
    /// Extraction parameters
    /// </summary>
    [YamlMember(Alias = "extraction")]
    public Dictionary<string, object> Extraction { get; set; } = new();

    /// <summary>
    /// Quality parameters
    /// </summary>
    [YamlMember(Alias = "quality")]
    public Dictionary<string, object> Quality { get; set; } = new();

    /// <summary>
    /// Timing parameters
    /// </summary>
    [YamlMember(Alias = "timing")]
    public Dictionary<string, object> Timing { get; set; } = new();

    /// <summary>
    /// Feature flags
    /// </summary>
    [YamlMember(Alias = "features")]
    public Dictionary<string, object> Features { get; set; } = new();

    /// <summary>
    /// Custom parameters
    /// </summary>
    [YamlMember(Alias = "parameters")]
    public Dictionary<string, object> Parameters { get; set; } = new();
}
