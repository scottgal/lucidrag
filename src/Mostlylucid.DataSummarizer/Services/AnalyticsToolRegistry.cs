using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Registry of available analytics tools.
/// Tools are capabilities beyond SQL that the LLM can invoke.
/// </summary>
public class AnalyticsToolRegistry
{
    private readonly Dictionary<string, AnalyticsTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    
    public AnalyticsToolRegistry()
    {
        RegisterBuiltInTools();
    }
    
    /// <summary>
    /// Get all registered tools
    /// </summary>
    public IReadOnlyList<AnalyticsTool> GetAllTools() => _tools.Values.ToList();
    
    /// <summary>
    /// Get a tool by ID
    /// </summary>
    public AnalyticsTool? GetTool(string toolId) => 
        _tools.TryGetValue(toolId, out var tool) ? tool : null;
    
    /// <summary>
    /// Get tools available for a given profile (based on data characteristics)
    /// </summary>
    public List<AnalyticsTool> GetAvailableTools(DataProfile profile)
    {
        var available = new List<AnalyticsTool>();
        
        var numericCount = profile.Columns.Count(c => c.InferredType == ColumnType.Numeric);
        var categoricalCount = profile.Columns.Count(c => c.InferredType == ColumnType.Categorical);
        var hasDateColumn = profile.Columns.Any(c => c.InferredType == ColumnType.DateTime);
        var hasTarget = profile.Target != null;
        
        foreach (var tool in _tools.Values)
        {
            var req = tool.Requirements;
            
            // Check requirements
            if (numericCount < req.MinNumericColumns) continue;
            if (categoricalCount < req.MinCategoricalColumns) continue;
            if (profile.RowCount < req.MinRows) continue;
            if (req.RequiresDateColumn && !hasDateColumn) continue;
            if (req.RequiresTarget && !hasTarget) continue;
            
            available.Add(tool);
        }
        
        return available;
    }
    
    /// <summary>
    /// Format tools for LLM prompt
    /// </summary>
    public string FormatToolsForPrompt(DataProfile profile)
    {
        var tools = GetAvailableTools(profile);
        if (tools.Count == 0) return "";
        
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("AVAILABLE TOOLS (use instead of SQL for these capabilities):");
        sb.AppendLine("To invoke a tool, respond with: TOOL:<tool_id> [parameters]");
        sb.AppendLine();
        
        foreach (var tool in tools)
        {
            sb.AppendLine($"  {tool.Id}: {tool.Description}");
            if (tool.Parameters.Count > 0)
            {
                var paramStr = string.Join(", ", tool.Parameters.Select(p => 
                    p.Required ? p.Name : $"[{p.Name}]"));
                sb.AppendLine($"    Parameters: {paramStr}");
            }
            if (tool.ExampleQuestions.Count > 0)
            {
                sb.AppendLine($"    Examples: \"{tool.ExampleQuestions[0]}\"");
            }
        }
        
        sb.AppendLine();
        return sb.ToString();
    }
    
    /// <summary>
    /// Parse LLM response to detect tool invocation
    /// </summary>
    public ToolInvocation? ParseToolInvocation(string response, string originalQuestion)
    {
        // Look for TOOL:tool_id pattern
        var toolMatch = System.Text.RegularExpressions.Regex.Match(
            response, 
            @"TOOL:(\w+)(?:\s+(.*))?", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        if (!toolMatch.Success) return null;
        
        var toolId = toolMatch.Groups[1].Value;
        var paramString = toolMatch.Groups[2].Value.Trim();
        
        var tool = GetTool(toolId);
        if (tool == null) return null;
        
        var parameters = ParseParameters(paramString, tool.Parameters);
        
        return new ToolInvocation
        {
            ToolId = toolId,
            Parameters = parameters,
            OriginalQuestion = originalQuestion
        };
    }
    
    private Dictionary<string, object> ParseParameters(string paramString, List<ToolParameter> paramDefs)
    {
        var result = new Dictionary<string, object>();
        
        if (string.IsNullOrWhiteSpace(paramString))
        {
            // Apply defaults
            foreach (var param in paramDefs.Where(p => p.DefaultValue != null))
            {
                result[param.Name] = param.DefaultValue!;
            }
            return result;
        }
        
        // Parse key=value pairs or positional params
        var parts = paramString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var positionalIndex = 0;
        
        foreach (var part in parts)
        {
            if (part.Contains('='))
            {
                var kv = part.Split('=', 2);
                result[kv[0]] = kv[1];
            }
            else if (positionalIndex < paramDefs.Count)
            {
                result[paramDefs[positionalIndex].Name] = part;
                positionalIndex++;
            }
        }
        
        // Apply defaults for missing params
        foreach (var param in paramDefs.Where(p => p.DefaultValue != null && !result.ContainsKey(p.Name)))
        {
            result[param.Name] = param.DefaultValue!;
        }
        
        return result;
    }
    
    private void RegisterBuiltInTools()
    {
        // Audience Segmentation / Clustering
        Register(new AnalyticsTool
        {
            Id = "segment_audience",
            Name = "Audience Segmentation",
            Description = "Automatically segment data into distinct groups based on numeric features using k-means clustering",
            Category = ToolCategory.Segmentation,
            ExampleQuestions = [
                "Show me the audience segments",
                "What customer segments exist in this data?",
                "Cluster the customers",
                "Find natural groupings in the data"
            ],
            Requirements = new ToolRequirements
            {
                MinNumericColumns = 2,
                MinRows = 50
            },
            Parameters = [
                new ToolParameter
                {
                    Name = "num_segments",
                    Description = "Number of segments to create (2-10)",
                    Type = ToolParameterType.Integer,
                    Required = false,
                    DefaultValue = 4
                },
                new ToolParameter
                {
                    Name = "features",
                    Description = "Columns to use for segmentation (comma-separated, or 'auto')",
                    Type = ToolParameterType.ColumnList,
                    Required = false,
                    DefaultValue = "auto"
                }
            ]
        });
        
        // Anomaly Detection
        Register(new AnalyticsTool
        {
            Id = "detect_anomalies",
            Name = "Anomaly Detection",
            Description = "Find unusual records that deviate significantly from normal patterns",
            Category = ToolCategory.AnomalyDetection,
            ExampleQuestions = [
                "Find anomalies in the data",
                "What records are unusual?",
                "Detect outliers across all columns",
                "Show me suspicious transactions"
            ],
            Requirements = new ToolRequirements
            {
                MinNumericColumns = 1,
                MinRows = 30
            },
            Parameters = [
                new ToolParameter
                {
                    Name = "method",
                    Description = "Detection method",
                    Type = ToolParameterType.Choice,
                    Options = ["zscore", "iqr", "isolation_forest"],
                    DefaultValue = "zscore"
                },
                new ToolParameter
                {
                    Name = "threshold",
                    Description = "Sensitivity threshold (lower = more anomalies)",
                    Type = ToolParameterType.Decimal,
                    DefaultValue = 3.0
                }
            ]
        });
        
        // Time Series Decomposition
        Register(new AnalyticsTool
        {
            Id = "decompose_timeseries",
            Name = "Time Series Decomposition",
            Description = "Break down time series into trend, seasonality, and residual components",
            Category = ToolCategory.TimeSeries,
            ExampleQuestions = [
                "Show me the trend over time",
                "Is there seasonality in the data?",
                "Decompose the time series",
                "What's the underlying pattern?"
            ],
            Requirements = new ToolRequirements
            {
                RequiresDateColumn = true,
                MinNumericColumns = 1,
                MinRows = 30
            },
            Parameters = [
                new ToolParameter
                {
                    Name = "date_column",
                    Description = "Date column to use",
                    Type = ToolParameterType.ColumnName,
                    Required = false,
                    DefaultValue = "auto"
                },
                new ToolParameter
                {
                    Name = "value_column",
                    Description = "Numeric column to decompose",
                    Type = ToolParameterType.ColumnName,
                    Required = false,
                    DefaultValue = "auto"
                }
            ]
        });
        
        // Feature Importance
        Register(new AnalyticsTool
        {
            Id = "feature_importance",
            Name = "Feature Importance",
            Description = "Rank features by their importance in predicting the target variable",
            Category = ToolCategory.FeatureAnalysis,
            ExampleQuestions = [
                "What drives the target?",
                "Which features are most important?",
                "What predicts churn?",
                "Show feature importance"
            ],
            Requirements = new ToolRequirements
            {
                RequiresTarget = true,
                MinNumericColumns = 2,
                MinRows = 100
            },
            Parameters = [
                new ToolParameter
                {
                    Name = "method",
                    Description = "Importance calculation method",
                    Type = ToolParameterType.Choice,
                    Options = ["correlation", "mutual_info", "permutation"],
                    DefaultValue = "correlation"
                }
            ]
        });
        
        // Group Comparison
        Register(new AnalyticsTool
        {
            Id = "compare_groups",
            Name = "Group Comparison",
            Description = "Compare statistics between different groups/segments",
            Category = ToolCategory.Comparison,
            ExampleQuestions = [
                "Compare churners vs non-churners",
                "How do regions differ?",
                "Compare male vs female customers",
                "What's different between groups?"
            ],
            Requirements = new ToolRequirements
            {
                MinCategoricalColumns = 1,
                MinNumericColumns = 1,
                MinRows = 50
            },
            Parameters = [
                new ToolParameter
                {
                    Name = "group_by",
                    Description = "Column to group by",
                    Type = ToolParameterType.ColumnName,
                    Required = true
                },
                new ToolParameter
                {
                    Name = "metrics",
                    Description = "Numeric columns to compare (comma-separated, or 'all')",
                    Type = ToolParameterType.ColumnList,
                    DefaultValue = "all"
                }
            ]
        });
        
        // Statistical Test
        Register(new AnalyticsTool
        {
            Id = "statistical_test",
            Name = "Statistical Significance Test",
            Description = "Test if differences between groups are statistically significant",
            Category = ToolCategory.StatisticalTest,
            ExampleQuestions = [
                "Is the difference significant?",
                "Run a statistical test",
                "Is this correlation real?",
                "Test the hypothesis"
            ],
            Requirements = new ToolRequirements
            {
                MinNumericColumns = 1,
                MinRows = 30
            },
            Parameters = [
                new ToolParameter
                {
                    Name = "test_type",
                    Description = "Type of test to run",
                    Type = ToolParameterType.Choice,
                    Options = ["ttest", "chi_square", "anova", "correlation"],
                    DefaultValue = "ttest"
                },
                new ToolParameter
                {
                    Name = "column1",
                    Description = "First column",
                    Type = ToolParameterType.ColumnName,
                    Required = true
                },
                new ToolParameter
                {
                    Name = "column2",
                    Description = "Second column (for correlation) or group column",
                    Type = ToolParameterType.ColumnName,
                    Required = false
                }
            ]
        });
        
        // Data Quality Report
        Register(new AnalyticsTool
        {
            Id = "data_quality",
            Name = "Data Quality Report",
            Description = "Generate a comprehensive data quality assessment",
            Category = ToolCategory.DataQuality,
            ExampleQuestions = [
                "What's the data quality?",
                "Are there data issues?",
                "Check data quality",
                "Show me data problems"
            ],
            Requirements = new ToolRequirements
            {
                MinRows = 1
            },
            Parameters = []
        });
        
        // Correlation Analysis
        Register(new AnalyticsTool
        {
            Id = "correlation_analysis",
            Name = "Correlation Analysis",
            Description = "Analyze relationships between all numeric columns",
            Category = ToolCategory.FeatureAnalysis,
            ExampleQuestions = [
                "What columns are correlated?",
                "Show me relationships between features",
                "Find correlated columns",
                "What's related to what?"
            ],
            Requirements = new ToolRequirements
            {
                MinNumericColumns = 2,
                MinRows = 30
            },
            Parameters = [
                new ToolParameter
                {
                    Name = "min_correlation",
                    Description = "Minimum correlation to report (0-1)",
                    Type = ToolParameterType.Decimal,
                    DefaultValue = 0.3
                }
            ]
        });
    }
    
    private void Register(AnalyticsTool tool)
    {
        _tools[tool.Id] = tool;
    }
}
