using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Configuration;

public class DataSummarizerSettings
{
    public string Name => "DataSummarizer";
    public ProfileOptions ProfileOptions { get; set; } = new();
    public MarkdownReportSettings MarkdownReport { get; set; } = new();
    public ConsoleOutputSettings ConsoleOutput { get; set; } = new();
    
    /// <summary>
    /// ONNX embedding configuration (auto-downloads models on first run)
    /// </summary>
    public OnnxConfig Onnx { get; set; } = new();
    
    /// <summary>
    /// PII display configuration (controls privacy-safe output)
    /// </summary>
    public PiiDisplayConfig PiiDisplay { get; set; } = new();
    
    /// <summary>
    /// Ollama LLM configuration
    /// </summary>
    public OllamaSettings Ollama { get; set; } = new();
    public bool EnableClarifierSentinel { get; set; } = true;
    public string ClarifierSentinelModel { get; set; } = "qwen2.5:1.5b";
    
    /// <summary>
    /// Vector store configuration for conversation memory and semantic search
    /// </summary>
    public VectorStoreSettings VectorStore { get; set; } = new();
    
    /// <summary>
    /// Active analysis profile name (controls what analysis is performed)
    /// </summary>
    public string AnalysisProfile { get; set; } = "Default";
    
    /// <summary>
    /// Named analysis profiles - control WHAT gets analyzed
    /// </summary>
    public Dictionary<string, AnalysisProfileConfig> AnalysisProfiles { get; set; } = new()
    {
        ["Default"] = AnalysisProfileConfig.Default,
        ["Fast"] = AnalysisProfileConfig.Fast,
        ["Full"] = AnalysisProfileConfig.Full
    };
    
    /// <summary>
    /// Active output profile name (Default, Tool, Brief, Detailed, Markdown)
    /// </summary>
    public string OutputProfile { get; set; } = "Default";
    
    /// <summary>
    /// Named output profiles - control HOW results are displayed
    /// </summary>
    public Dictionary<string, OutputProfileConfig> OutputProfiles { get; set; } = new()
    {
        ["Default"] = OutputProfileConfig.Default,
        ["Tool"] = OutputProfileConfig.Tool,
        ["Brief"] = OutputProfileConfig.Brief,
        ["Detailed"] = OutputProfileConfig.Detailed,
        ["Markdown"] = OutputProfileConfig.MarkdownFocus
    };
    
    /// <summary>
    /// Get the active analysis profile configuration
    /// </summary>
    public AnalysisProfileConfig GetActiveAnalysisProfile()
    {
        if (AnalysisProfiles.TryGetValue(AnalysisProfile, out var profile))
            return profile;
        return AnalysisProfileConfig.Default;
    }
    
    /// <summary>
    /// Get the active output profile configuration
    /// </summary>
    public OutputProfileConfig GetActiveProfile()
    {
        if (OutputProfiles.TryGetValue(OutputProfile, out var profile))
            return profile;
        return OutputProfileConfig.Default;
    }
}

public class MarkdownReportSettings
{
    public bool Enabled { get; set; } = true;
    public bool UseLlm { get; set; } = true;
    public string? OutputDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "reports");
    public bool IncludeFocusQuestions { get; set; } = false;
    public List<string> FocusQuestions { get; set; } = new();
    
    /// <summary>
    /// If true, use LLM to generate data-appropriate focus questions
    /// </summary>
    public bool GenerateFocusQuestions { get; set; } = false;
}

/// <summary>
/// Ollama LLM configuration
/// </summary>
public class OllamaSettings
{
    /// <summary>
    /// Model name for LLM inference (e.g., llama3.2:3b, mistral, codellama)
    /// </summary>
    public string Model { get; set; } = "llama3.2:3b";
    
    /// <summary>
    /// Ollama API base URL
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";
}

/// <summary>
/// Vector store configuration for conversation memory
/// </summary>
public class VectorStoreSettings
{
    /// <summary>
    /// Enable vector store for conversation memory and semantic search
    /// </summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    /// Path to the SQLite vector database file
    /// </summary>
    public string DatabasePath { get; set; } = "datasummarizer.db";
}

/// <summary>
/// Analysis profile - controls WHAT analysis is performed
/// </summary>
public class AnalysisProfileConfig
{
    public string Description { get; set; } = "";
    
    /// <summary>Use LLM for insights (requires Ollama)</summary>
    public bool UseLlm { get; set; } = true;
    
    /// <summary>Compute column correlations</summary>
    public bool ComputeCorrelations { get; set; } = true;
    
    /// <summary>Detect text patterns (can be slow on large datasets)</summary>
    public bool DetectPatterns { get; set; } = true;
    
    /// <summary>Generate markdown report</summary>
    public bool GenerateReport { get; set; } = true;
    
    /// <summary>Use ONNX embeddings for semantic analysis</summary>
    public bool UseOnnx { get; set; } = true;
    
    /// <summary>Default - balanced analysis with LLM if available</summary>
    public static AnalysisProfileConfig Default => new()
    {
        Description = "Balanced analysis - LLM if available, all features enabled",
        UseLlm = true,
        ComputeCorrelations = true,
        DetectPatterns = true,
        GenerateReport = true,
        UseOnnx = true
    };
    
    /// <summary>Fast - quick stats only, no expensive operations</summary>
    public static AnalysisProfileConfig Fast => new()
    {
        Description = "Quick stats only - no LLM, no correlations, no patterns",
        UseLlm = false,
        ComputeCorrelations = false,
        DetectPatterns = false,
        GenerateReport = false,
        UseOnnx = false
    };
    
    /// <summary>Full - everything enabled, comprehensive analysis</summary>
    public static AnalysisProfileConfig Full => new()
    {
        Description = "Comprehensive analysis - all features enabled",
        UseLlm = true,
        ComputeCorrelations = true,
        DetectPatterns = true,
        GenerateReport = true,
        UseOnnx = true
    };
}

/// <summary>
/// Configuration for console output sections
/// </summary>
public class ConsoleOutputSettings
{
    /// <summary>Show the executive summary section</summary>
    public bool ShowSummary { get; set; } = true;
    
    /// <summary>Show the column details table</summary>
    public bool ShowColumnTable { get; set; } = true;
    
    /// <summary>Show mini bar charts for categorical distributions</summary>
    public bool ShowCharts { get; set; } = true;
    
    /// <summary>Show data quality alerts</summary>
    public bool ShowAlerts { get; set; } = true;
    
    /// <summary>Show statistical insights</summary>
    public bool ShowInsights { get; set; } = true;
    
    /// <summary>Show correlation analysis</summary>
    public bool ShowCorrelations { get; set; } = true;
    
    /// <summary>Show focus question findings (requires LLM)</summary>
    public bool ShowFocusFindings { get; set; } = false;
    
    /// <summary>Maximum number of insights to display</summary>
    public int MaxInsights { get; set; } = 5;
    
    /// <summary>Maximum number of alerts to display</summary>
    public int MaxAlerts { get; set; } = 10;
}

/// <summary>
/// Complete output profile configuration
/// </summary>
public class OutputProfileConfig
{
    /// <summary>
    /// Human-readable description of this profile
    /// </summary>
    public string Description { get; set; } = "";
    
    /// <summary>
    /// Console output settings for this profile
    /// </summary>
    public ConsoleOutputSettings Console { get; set; } = new();
    
    /// <summary>
    /// JSON output settings for this profile
    /// </summary>
    public JsonOutputSettings Json { get; set; } = new();
    
    /// <summary>
    /// Markdown output settings for this profile
    /// </summary>
    public MarkdownOutputSettings Markdown { get; set; } = new();
    
    /// <summary>
    /// Default profile - balanced output for interactive use
    /// </summary>
    public static OutputProfileConfig Default => new()
    {
        Description = "Balanced output for interactive use",
        Console = new()
        {
            ShowSummary = true,
            ShowColumnTable = true,
            ShowAlerts = true,
            ShowInsights = true,
            ShowCorrelations = true,
            ShowFocusFindings = false,
            MaxInsights = 5,
            MaxAlerts = 10
        },
        Json = new() { Enabled = false, Pretty = true },
        Markdown = new() { Enabled = true }
    };
    
    /// <summary>
    /// Tool profile - minimal output for MCP/agent consumption
    /// </summary>
    public static OutputProfileConfig Tool => new()
    {
        Description = "Minimal output for MCP/agent consumption - JSON only",
        Console = new()
        {
            ShowSummary = false,
            ShowColumnTable = false,
            ShowAlerts = false,
            ShowInsights = false,
            ShowCorrelations = false,
            ShowFocusFindings = false,
            MaxInsights = 0,
            MaxAlerts = 0
        },
        Json = new() { Enabled = true, Pretty = false, IncludeProfile = true, IncludeInsights = true },
        Markdown = new() { Enabled = false }
    };
    
    /// <summary>
    /// Brief profile - quick overview with summary and alerts only
    /// </summary>
    public static OutputProfileConfig Brief => new()
    {
        Description = "Quick overview - summary and alerts only",
        Console = new()
        {
            ShowSummary = true,
            ShowColumnTable = false,
            ShowAlerts = true,
            ShowInsights = false,
            ShowCorrelations = false,
            ShowFocusFindings = false,
            MaxInsights = 0,
            MaxAlerts = 5
        },
        Json = new() { Enabled = false },
        Markdown = new() { Enabled = false }
    };
    
    /// <summary>
    /// Detailed profile - full analysis with all sections
    /// </summary>
    public static OutputProfileConfig Detailed => new()
    {
        Description = "Full analysis with all sections",
        Console = new()
        {
            ShowSummary = true,
            ShowColumnTable = true,
            ShowAlerts = true,
            ShowInsights = true,
            ShowCorrelations = true,
            ShowFocusFindings = true,
            MaxInsights = 20,
            MaxAlerts = 50
        },
        Json = new() { Enabled = true, Pretty = true, IncludeProfile = true, IncludeInsights = true, IncludeAlerts = true, IncludeCorrelations = true },
        Markdown = new() { Enabled = true, IncludeCharts = true }
    };
    
    /// <summary>
    /// Markdown profile - focus on markdown report generation
    /// </summary>
    public static OutputProfileConfig MarkdownFocus => new()
    {
        Description = "Focus on markdown report generation",
        Console = new()
        {
            ShowSummary = true,
            ShowColumnTable = false,
            ShowAlerts = false,
            ShowInsights = false,
            ShowCorrelations = false,
            ShowFocusFindings = false,
            MaxInsights = 0,
            MaxAlerts = 0
        },
        Json = new() { Enabled = false },
        Markdown = new() { Enabled = true, IncludeCharts = true }
    };
}

/// <summary>
/// JSON output configuration
/// </summary>
public class JsonOutputSettings
{
    /// <summary>Enable JSON output</summary>
    public bool Enabled { get; set; } = false;
    
    /// <summary>Include full data profile in JSON output</summary>
    public bool IncludeProfile { get; set; } = true;
    
    /// <summary>Include insights in JSON output</summary>
    public bool IncludeInsights { get; set; } = true;
    
    /// <summary>Include alerts in JSON output</summary>
    public bool IncludeAlerts { get; set; } = true;
    
    /// <summary>Include correlations in JSON output</summary>
    public bool IncludeCorrelations { get; set; } = true;
    
    /// <summary>Pretty-print JSON output</summary>
    public bool Pretty { get; set; } = true;
}

/// <summary>
/// Markdown output configuration
/// </summary>
public class MarkdownOutputSettings
{
    /// <summary>Enable markdown report generation</summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>Include executive summary section</summary>
    public bool IncludeExecutiveSummary { get; set; } = true;
    
    /// <summary>Include column details section</summary>
    public bool IncludeColumnDetails { get; set; } = true;
    
    /// <summary>Include ASCII charts in markdown</summary>
    public bool IncludeCharts { get; set; } = false;
}
