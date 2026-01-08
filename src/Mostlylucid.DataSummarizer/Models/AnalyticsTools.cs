namespace Mostlylucid.DataSummarizer.Models;

/// <summary>
/// Defines an analytics tool that the LLM can invoke.
/// Tools represent capabilities beyond SQL - clustering, segmentation, anomaly detection, etc.
/// </summary>
public class AnalyticsTool
{
    /// <summary>Unique tool identifier (e.g., "segment_audience", "detect_anomalies")</summary>
    public required string Id { get; init; }
    
    /// <summary>Human-readable name</summary>
    public required string Name { get; init; }
    
    /// <summary>Description for LLM to understand when to use this tool</summary>
    public required string Description { get; init; }
    
    /// <summary>Example questions this tool can answer</summary>
    public List<string> ExampleQuestions { get; init; } = [];
    
    /// <summary>Required columns/features for this tool</summary>
    public ToolRequirements Requirements { get; init; } = new();
    
    /// <summary>Parameters the LLM can provide when invoking</summary>
    public List<ToolParameter> Parameters { get; init; } = [];
    
    /// <summary>Category of tool</summary>
    public ToolCategory Category { get; init; }
    
    /// <summary>Whether this tool's results should be cached in the profile</summary>
    public bool CacheResults { get; init; } = true;
}

/// <summary>
/// Requirements for a tool to be available
/// </summary>
public class ToolRequirements
{
    /// <summary>Minimum number of numeric columns needed</summary>
    public int MinNumericColumns { get; init; }
    
    /// <summary>Minimum number of categorical columns needed</summary>
    public int MinCategoricalColumns { get; init; }
    
    /// <summary>Minimum row count</summary>
    public int MinRows { get; init; } = 100;
    
    /// <summary>Requires a date/time column</summary>
    public bool RequiresDateColumn { get; init; }
    
    /// <summary>Requires a target column to be specified</summary>
    public bool RequiresTarget { get; init; }
    
    /// <summary>Specific column types required</summary>
    public List<ColumnType> RequiredColumnTypes { get; init; } = [];
}

/// <summary>
/// A parameter that can be passed to a tool
/// </summary>
public class ToolParameter
{
    /// <summary>Parameter name</summary>
    public required string Name { get; init; }
    
    /// <summary>Description for LLM</summary>
    public required string Description { get; init; }
    
    /// <summary>Parameter type</summary>
    public ToolParameterType Type { get; init; }
    
    /// <summary>Whether this parameter is required</summary>
    public bool Required { get; init; }
    
    /// <summary>Default value if not provided</summary>
    public object? DefaultValue { get; init; }
    
    /// <summary>Valid options for enum/choice parameters</summary>
    public List<string>? Options { get; init; }
}

public enum ToolParameterType
{
    String,
    Integer,
    Decimal,
    Boolean,
    ColumnName,
    ColumnList,
    Choice
}

public enum ToolCategory
{
    /// <summary>Segmentation, clustering, grouping</summary>
    Segmentation,
    
    /// <summary>Anomaly detection, outlier analysis</summary>
    AnomalyDetection,
    
    /// <summary>Time series analysis, forecasting</summary>
    TimeSeries,
    
    /// <summary>Feature importance, correlation analysis</summary>
    FeatureAnalysis,
    
    /// <summary>Data quality, validation</summary>
    DataQuality,
    
    /// <summary>Comparison between groups</summary>
    Comparison,
    
    /// <summary>Statistical tests</summary>
    StatisticalTest
}

/// <summary>
/// Result of invoking an analytics tool
/// </summary>
public class ToolResult
{
    /// <summary>The tool that was invoked</summary>
    public required string ToolId { get; init; }
    
    /// <summary>Whether the tool executed successfully</summary>
    public bool Success { get; init; }
    
    /// <summary>Error message if failed</summary>
    public string? Error { get; init; }
    
    /// <summary>Human-readable summary of results</summary>
    public string Summary { get; init; } = "";
    
    /// <summary>Structured result data</summary>
    public Dictionary<string, object> Data { get; init; } = new();
    
    /// <summary>Visualization hint (table, chart type, etc.)</summary>
    public string? VisualizationType { get; init; }
    
    /// <summary>Columns involved in the analysis</summary>
    public List<string> RelatedColumns { get; init; } = [];
    
    /// <summary>When this was computed</summary>
    public DateTime ComputedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Request from LLM to invoke a tool
/// </summary>
public class ToolInvocation
{
    /// <summary>The tool to invoke</summary>
    public required string ToolId { get; init; }
    
    /// <summary>Parameters provided by LLM</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();
    
    /// <summary>Original question that triggered this</summary>
    public string? OriginalQuestion { get; init; }
}

/// <summary>
/// Response format for LLM - either SQL or tool invocation
/// </summary>
public class LlmActionResponse
{
    /// <summary>Type of action: "sql" or "tool"</summary>
    public required string ActionType { get; init; }
    
    /// <summary>SQL query if ActionType is "sql"</summary>
    public string? Sql { get; init; }
    
    /// <summary>Tool invocation if ActionType is "tool"</summary>
    public ToolInvocation? ToolInvocation { get; init; }
    
    /// <summary>Direct answer if no query/tool needed</summary>
    public string? DirectAnswer { get; init; }
}
