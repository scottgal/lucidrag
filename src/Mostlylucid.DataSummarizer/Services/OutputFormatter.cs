using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mostlylucid.DataSummarizer.Configuration;
using Mostlylucid.DataSummarizer.Models;
using Spectre.Console;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Formats DataSummarizer output in various formats based on output profiles.
/// Designed with LLM consumption in mind - structured, consistent, actionable.
/// </summary>
public static class OutputFormatter
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Format a data summary report as structured JSON for LLM/tool consumption
    /// </summary>
    public static string FormatJson(DataSummaryReport report, OutputProfileConfig profile, string fileName)
    {
        var jsonSettings = profile.Json;
        var options = jsonSettings.Pretty ? PrettyJsonOptions : CompactJsonOptions;
        
        var output = new LlmFriendlyOutput
        {
            Success = true,
            FileName = Path.GetFileName(fileName),
            FilePath = fileName,
            GeneratedAt = DateTime.UtcNow,
            
            // Quick facts for LLM context window efficiency
            QuickFacts = BuildQuickFacts(report),
            
            // Actionable recommendations
            Recommendations = BuildRecommendations(report),
            
            // Detailed sections based on profile
            Profile = jsonSettings.IncludeProfile ? BuildProfileSummary(report.Profile) : null,
            Alerts = jsonSettings.IncludeAlerts ? BuildAlerts(report.Profile) : null,
            Insights = jsonSettings.IncludeInsights ? BuildInsights(report.Profile) : null,
            Correlations = jsonSettings.IncludeCorrelations ? BuildCorrelations(report.Profile) : null,
            
            // LLM-specific helpers
            SuggestedQueries = BuildSuggestedQueries(report.Profile),
            DataDictionary = BuildDataDictionary(report.Profile),
            QualityIssues = BuildQualityIssues(report.Profile),
            
            ExecutiveSummary = !string.IsNullOrEmpty(report.ExecutiveSummary) ? report.ExecutiveSummary : null
        };
        
        return JsonSerializer.Serialize(output, options);
    }

    /// <summary>
    /// Format just the data profile as JSON (for tool command)
    /// </summary>
    public static string FormatProfileJson(DataProfile profile, bool pretty = false)
    {
        var options = pretty ? PrettyJsonOptions : CompactJsonOptions;
        
        var report = new DataSummaryReport { Profile = profile };
        var output = new LlmFriendlyOutput
        {
            Success = true,
            FileName = Path.GetFileName(profile.SourcePath),
            FilePath = profile.SourcePath,
            GeneratedAt = DateTime.UtcNow,
            QuickFacts = BuildQuickFacts(report),
            Recommendations = BuildRecommendations(report),
            Profile = BuildProfileSummary(profile),
            Alerts = BuildAlerts(profile),
            Insights = BuildInsights(profile),
            Correlations = BuildCorrelations(profile),
            SuggestedQueries = BuildSuggestedQueries(profile),
            DataDictionary = BuildDataDictionary(profile),
            QualityIssues = BuildQualityIssues(profile)
        };
        
        return JsonSerializer.Serialize(output, options);
    }

    /// <summary>
    /// Format markdown report
    /// </summary>
    public static string FormatMarkdown(DataSummaryReport report, OutputProfileConfig profile, string fileName)
    {
        var sb = new StringBuilder();
        var mdSettings = profile.Markdown;
        
        sb.AppendLine($"# Data Profile: {Path.GetFileName(fileName)}");
        sb.AppendLine();
        sb.AppendLine($"*Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}*");
        sb.AppendLine();
        
        // Executive Summary
        if (mdSettings.IncludeExecutiveSummary && !string.IsNullOrEmpty(report.ExecutiveSummary))
        {
            sb.AppendLine("## Executive Summary");
            sb.AppendLine();
            sb.AppendLine(report.ExecutiveSummary);
            sb.AppendLine();
        }
        
        // Quick Facts box
        sb.AppendLine("## Quick Facts");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|--------|-------|");
        sb.AppendLine($"| Rows | {report.Profile.RowCount:N0} |");
        sb.AppendLine($"| Columns | {report.Profile.ColumnCount} |");
        sb.AppendLine($"| Numeric Columns | {report.Profile.Columns.Count(c => c.InferredType == ColumnType.Numeric)} |");
        sb.AppendLine($"| Categorical Columns | {report.Profile.Columns.Count(c => c.InferredType == ColumnType.Categorical)} |");
        sb.AppendLine($"| DateTime Columns | {report.Profile.Columns.Count(c => c.InferredType == ColumnType.DateTime)} |");
        var avgNullPct = report.Profile.Columns.Average(c => c.NullPercent);
        sb.AppendLine($"| Avg Null % | {avgNullPct:F1}% |");
        sb.AppendLine($"| Alerts | {report.Profile.Alerts.Count} |");
        sb.AppendLine();
        
        // Alerts
        if (report.Profile.Alerts.Count > 0)
        {
            sb.AppendLine("## Data Quality Alerts");
            sb.AppendLine();
            foreach (var alert in report.Profile.Alerts.OrderByDescending(a => a.Severity))
            {
                var icon = alert.Severity switch
                {
                    AlertSeverity.Error => "🔴",
                    AlertSeverity.Warning => "🟡",
                    _ => "🔵"
                };
                sb.AppendLine($"- {icon} **{alert.Severity}** [{alert.Type}]: {alert.Message}");
                if (!string.IsNullOrEmpty(alert.Column))
                    sb.AppendLine($"  - Column: `{alert.Column}`");
            }
            sb.AppendLine();
        }
        
        // Column Details
        if (mdSettings.IncludeColumnDetails)
        {
            sb.AppendLine("## Column Details");
            sb.AppendLine();
            sb.AppendLine("| Column | Type | Role | Nulls | Unique | Notes |");
            sb.AppendLine("|--------|------|------|-------|--------|-------|");
            foreach (var col in report.Profile.Columns.Take(50))
            {
                var role = col.SemanticRole != SemanticRole.Unknown ? col.SemanticRole.ToString() : "-";
                var notes = GetColumnNotes(col);
                sb.AppendLine($"| {col.Name} | {col.InferredType} | {role} | {col.NullPercent:F1}% | {col.UniqueCount:N0} | {notes} |");
            }
            if (report.Profile.Columns.Count > 50)
                sb.AppendLine($"\n*...and {report.Profile.Columns.Count - 50} more columns*");
            sb.AppendLine();
        }
        
        // Insights
        if (report.Profile.Insights.Count > 0)
        {
            sb.AppendLine("## Insights");
            sb.AppendLine();
            foreach (var insight in report.Profile.Insights.OrderByDescending(i => i.Score).Take(10))
            {
                sb.AppendLine($"### {insight.Title}");
                sb.AppendLine();
                sb.AppendLine(insight.Description);
                if (insight.RelatedColumns.Count > 0)
                    sb.AppendLine($"\n*Related columns: {string.Join(", ", insight.RelatedColumns)}*");
                sb.AppendLine();
            }
        }
        
        // Correlations
        if (report.Profile.Correlations.Count > 0)
        {
            sb.AppendLine("## Top Correlations");
            sb.AppendLine();
            sb.AppendLine("| Column A | Column B | Correlation | Strength |");
            sb.AppendLine("|----------|----------|-------------|----------|");
            foreach (var corr in report.Profile.Correlations.OrderByDescending(c => Math.Abs(c.Correlation)).Take(15))
            {
                sb.AppendLine($"| {corr.Column1} | {corr.Column2} | {corr.Correlation:F3} | {corr.Strength} |");
            }
            sb.AppendLine();
        }
        
        // Suggested SQL queries
        var queries = BuildSuggestedQueries(report.Profile);
        if (queries.Count > 0)
        {
            sb.AppendLine("## Suggested Exploration Queries");
            sb.AppendLine();
            foreach (var q in queries.Take(5))
            {
                sb.AppendLine($"### {q.Purpose}");
                sb.AppendLine();
                sb.AppendLine("```sql");
                sb.AppendLine(q.Sql);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }
        
        return sb.ToString();
    }

    /// <summary>
    /// Write console output using Spectre.Console based on profile settings
    /// </summary>
    public static void WriteConsole(DataSummaryReport report, OutputProfileConfig profile, bool verbose = false)
    {
        var console = profile.Console;
        
        // Summary
        if (console.ShowSummary)
        {
            AnsiConsole.Write(new Rule("[green]Data Summary[/]").LeftJustified());
            
            var summaryTable = new Table().Border(TableBorder.Rounded);
            summaryTable.AddColumn("Metric");
            summaryTable.AddColumn("Value");
            summaryTable.AddRow("Rows", $"{report.Profile.RowCount:N0}");
            summaryTable.AddRow("Columns", report.Profile.ColumnCount.ToString());
            summaryTable.AddRow("Numeric", report.Profile.Columns.Count(c => c.InferredType == ColumnType.Numeric).ToString());
            summaryTable.AddRow("Categorical", report.Profile.Columns.Count(c => c.InferredType == ColumnType.Categorical).ToString());
            summaryTable.AddRow("DateTime", report.Profile.Columns.Count(c => c.InferredType == ColumnType.DateTime).ToString());
            summaryTable.AddRow("Avg Null %", $"{report.Profile.Columns.Average(c => c.NullPercent):F1}%");
            AnsiConsole.Write(summaryTable);
            AnsiConsole.WriteLine();
            
            if (!string.IsNullOrEmpty(report.ExecutiveSummary))
            {
                AnsiConsole.MarkupLine("[dim]Summary:[/]");
                AnsiConsole.WriteLine(report.ExecutiveSummary);
                AnsiConsole.WriteLine();
            }
        }
        
        // Alerts
        if (console.ShowAlerts && report.Profile.Alerts.Count > 0)
        {
            AnsiConsole.Write(new Rule("[yellow]Alerts[/]").LeftJustified());
            var alertsToShow = report.Profile.Alerts.OrderByDescending(a => a.Severity).Take(console.MaxAlerts);
            foreach (var alert in alertsToShow)
            {
                var color = alert.Severity switch
                {
                    AlertSeverity.Error => "red",
                    AlertSeverity.Warning => "yellow",
                    _ => "blue"
                };
                AnsiConsole.MarkupLine($"[{color}]{alert.Severity}:[/] [{color}]{alert.Type}[/] - {Markup.Escape(alert.Message)}");
                if (!string.IsNullOrEmpty(alert.Column))
                    AnsiConsole.MarkupLine($"  [dim]Column: {alert.Column}[/]");
            }
            if (report.Profile.Alerts.Count > console.MaxAlerts)
                AnsiConsole.MarkupLine($"[dim]...and {report.Profile.Alerts.Count - console.MaxAlerts} more alerts[/]");
            AnsiConsole.WriteLine();
        }
        
        // Column Table
        if (console.ShowColumnTable)
        {
            AnsiConsole.Write(new Rule("[cyan]Columns[/]").LeftJustified());
            var colTable = new Table().Border(TableBorder.Rounded);
            colTable.AddColumn("Column");
            colTable.AddColumn("Type");
            colTable.AddColumn("Role");
            colTable.AddColumn("Nulls");
            colTable.AddColumn("Unique");
            
            foreach (var col in report.Profile.Columns.Take(30))
            {
                var role = col.SemanticRole != SemanticRole.Unknown ? col.SemanticRole.ToString() : "-";
                colTable.AddRow(
                    col.Name,
                    col.InferredType.ToString(),
                    role,
                    $"{col.NullPercent:F1}%",
                    col.UniqueCount.ToString("N0")
                );
            }
            AnsiConsole.Write(colTable);
            if (report.Profile.Columns.Count > 30)
                AnsiConsole.MarkupLine($"[dim]...and {report.Profile.Columns.Count - 30} more columns[/]");
            AnsiConsole.WriteLine();
        }
        
        // Insights
        if (console.ShowInsights && report.Profile.Insights.Count > 0)
        {
            AnsiConsole.Write(new Rule("[green]Insights[/]").LeftJustified());
            var insightsToShow = report.Profile.Insights.OrderByDescending(i => i.Score).Take(console.MaxInsights);
            foreach (var insight in insightsToShow)
            {
                AnsiConsole.MarkupLine($"[bold]{Markup.Escape(insight.Title)}[/]");
                AnsiConsole.MarkupLine($"  {Markup.Escape(insight.Description)}");
            }
            if (report.Profile.Insights.Count > console.MaxInsights)
                AnsiConsole.MarkupLine($"[dim]...and {report.Profile.Insights.Count - console.MaxInsights} more insights[/]");
            AnsiConsole.WriteLine();
        }
        
        // Correlations
        if (console.ShowCorrelations && report.Profile.Correlations.Count > 0)
        {
            AnsiConsole.Write(new Rule("[magenta]Correlations[/]").LeftJustified());
            var corrTable = new Table().Border(TableBorder.Rounded);
            corrTable.AddColumn("Column A");
            corrTable.AddColumn("Column B");
            corrTable.AddColumn("Correlation");
            corrTable.AddColumn("Strength");
            
            foreach (var corr in report.Profile.Correlations.OrderByDescending(c => Math.Abs(c.Correlation)).Take(10))
            {
                var color = Math.Abs(corr.Correlation) > 0.7 ? "green" : "white";
                corrTable.AddRow(corr.Column1, corr.Column2, $"[{color}]{corr.Correlation:F3}[/]", corr.Strength);
            }
            AnsiConsole.Write(corrTable);
            AnsiConsole.WriteLine();
        }
    }

    /// <summary>
    /// Write output to file
    /// </summary>
    public static async Task WriteToFileAsync(string content, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(filePath, content);
    }

    #region LLM-Friendly Builders
    
    /// <summary>
    /// Build quick facts - a compact summary an LLM can quickly parse
    /// </summary>
    private static QuickFacts BuildQuickFacts(DataSummaryReport report)
    {
        var p = report.Profile;
        var numericCols = p.Columns.Where(c => c.InferredType == ColumnType.Numeric).ToList();
        var catCols = p.Columns.Where(c => c.InferredType == ColumnType.Categorical).ToList();
        var dateCols = p.Columns.Where(c => c.InferredType == ColumnType.DateTime).ToList();
        var highNullCols = p.Columns.Where(c => c.NullPercent > 20).ToList();
        
        return new QuickFacts
        {
            RowCount = p.RowCount,
            ColumnCount = p.ColumnCount,
            NumericColumns = numericCols.Count,
            CategoricalColumns = catCols.Count,
            DateTimeColumns = dateCols.Count,
            TextColumns = p.Columns.Count(c => c.InferredType == ColumnType.Text),
            BooleanColumns = p.Columns.Count(c => c.InferredType == ColumnType.Boolean),
            IdColumns = p.Columns.Count(c => c.InferredType == ColumnType.Id),
            HighNullColumns = highNullCols.Count,
            ConstantColumns = p.Columns.Count(c => c.UniqueCount <= 1),
            HighCardinalityColumns = p.Columns.Count(c => c.UniquePercent > 90),
            AlertCount = p.Alerts.Count,
            ErrorAlerts = p.Alerts.Count(a => a.Severity == AlertSeverity.Error),
            WarningAlerts = p.Alerts.Count(a => a.Severity == AlertSeverity.Warning),
            HasTargetColumn = p.Target != null,
            TargetColumn = p.Target?.ColumnName,
            IsTargetImbalanced = p.Target?.ClassDistribution.Values.Max() > 0.8,
            StrongCorrelations = p.Correlations.Count(c => Math.Abs(c.Correlation) > 0.7),
            DatasetShape = ClassifyDatasetShape(p),
            PrimaryUseCase = InferPrimaryUseCase(p)
        };
    }
    
    /// <summary>
    /// Build actionable recommendations based on data quality issues
    /// </summary>
    private static List<Recommendation> BuildRecommendations(DataSummaryReport report)
    {
        var recs = new List<Recommendation>();
        var p = report.Profile;
        
        // High null columns
        var highNullCols = p.Columns.Where(c => c.NullPercent > 30).ToList();
        if (highNullCols.Count > 0)
        {
            recs.Add(new Recommendation
            {
                Priority = "High",
                Category = "DataQuality",
                Issue = $"{highNullCols.Count} columns have >30% null values",
                Action = "Consider imputation, dropping, or flagging missing data",
                AffectedColumns = highNullCols.Select(c => c.Name).ToList(),
                Sql = $"SELECT {string.Join(", ", highNullCols.Take(3).Select(c => $"COUNT(*) FILTER (WHERE \"{c.Name}\" IS NULL) AS {c.Name}_nulls"))} FROM data"
            });
        }
        
        // Constant columns
        var constantCols = p.Columns.Where(c => c.UniqueCount <= 1).ToList();
        if (constantCols.Count > 0)
        {
            recs.Add(new Recommendation
            {
                Priority = "Medium",
                Category = "FeatureEngineering",
                Issue = $"{constantCols.Count} columns have constant values",
                Action = "Remove these columns - they provide no information",
                AffectedColumns = constantCols.Select(c => c.Name).ToList()
            });
        }
        
        // High cardinality categorical
        var highCardCat = p.Columns.Where(c => c.InferredType == ColumnType.Categorical && c.UniquePercent > 50).ToList();
        if (highCardCat.Count > 0)
        {
            recs.Add(new Recommendation
            {
                Priority = "Medium",
                Category = "FeatureEngineering",
                Issue = $"{highCardCat.Count} categorical columns have high cardinality",
                Action = "Consider grouping rare categories, embedding, or treating as text",
                AffectedColumns = highCardCat.Select(c => c.Name).ToList()
            });
        }
        
        // Potential ID columns being used as features
        var idCols = p.Columns.Where(c => c.SemanticRole == SemanticRole.Identifier || c.InferredType == ColumnType.Id).ToList();
        if (idCols.Count > 0)
        {
            recs.Add(new Recommendation
            {
                Priority = "High",
                Category = "ModelingRisk",
                Issue = $"{idCols.Count} columns appear to be identifiers",
                Action = "Exclude from modeling to prevent data leakage",
                AffectedColumns = idCols.Select(c => c.Name).ToList()
            });
        }
        
        // Highly correlated features
        var highCorr = p.Correlations.Where(c => Math.Abs(c.Correlation) > 0.9 && c.Column1 != c.Column2).ToList();
        if (highCorr.Count > 0)
        {
            recs.Add(new Recommendation
            {
                Priority = "Medium",
                Category = "Multicollinearity",
                Issue = $"{highCorr.Count} column pairs have correlation > 0.9",
                Action = "Consider removing one column from each highly correlated pair",
                AffectedColumns = highCorr.SelectMany(c => new[] { c.Column1, c.Column2 }).Distinct().ToList()
            });
        }
        
        // Skewed numeric columns
        var skewedCols = p.Columns.Where(c => c.InferredType == ColumnType.Numeric && Math.Abs(c.Skewness ?? 0) > 2).ToList();
        if (skewedCols.Count > 0)
        {
            recs.Add(new Recommendation
            {
                Priority = "Low",
                Category = "FeatureEngineering",
                Issue = $"{skewedCols.Count} numeric columns are highly skewed",
                Action = "Consider log transform, Box-Cox, or quantile transform",
                AffectedColumns = skewedCols.Select(c => c.Name).ToList()
            });
        }
        
        // Outliers
        var outlierCols = p.Columns.Where(c => c.OutlierCount > p.RowCount * 0.05).ToList();
        if (outlierCols.Count > 0)
        {
            recs.Add(new Recommendation
            {
                Priority = "Medium",
                Category = "DataQuality",
                Issue = $"{outlierCols.Count} columns have >5% outliers",
                Action = "Investigate outliers - may need capping, removal, or separate modeling",
                AffectedColumns = outlierCols.Select(c => c.Name).ToList()
            });
        }
        
        // Target imbalance
        if (p.Target != null)
        {
            var maxClass = p.Target.ClassDistribution.Values.Max();
            if (maxClass > 0.8)
            {
                recs.Add(new Recommendation
                {
                    Priority = "High",
                    Category = "ClassImbalance",
                    Issue = $"Target '{p.Target.ColumnName}' is imbalanced ({maxClass:P0} majority class)",
                    Action = "Use stratified sampling, SMOTE, class weights, or threshold tuning",
                    AffectedColumns = [p.Target.ColumnName]
                });
            }
        }
        
        return recs.OrderBy(r => r.Priority == "High" ? 0 : r.Priority == "Medium" ? 1 : 2).ToList();
    }
    
    /// <summary>
    /// Build suggested DuckDB queries for exploration
    /// </summary>
    private static List<SuggestedQuery> BuildSuggestedQueries(DataProfile profile)
    {
        var queries = new List<SuggestedQuery>();
        var tableName = "data"; // DuckDB convention
        
        // Basic overview
        queries.Add(new SuggestedQuery
        {
            Purpose = "Row count and shape",
            Sql = $"SELECT COUNT(*) as rows, {profile.ColumnCount} as columns FROM {tableName}"
        });
        
        // Null analysis
        var nullCols = profile.Columns.Where(c => c.NullPercent > 0).Take(5).ToList();
        if (nullCols.Count > 0)
        {
            var nullChecks = string.Join(", ", nullCols.Select(c => $"SUM(CASE WHEN \"{c.Name}\" IS NULL THEN 1 ELSE 0 END) AS \"{c.Name}_nulls\""));
            queries.Add(new SuggestedQuery
            {
                Purpose = "Null value distribution",
                Sql = $"SELECT {nullChecks} FROM {tableName}"
            });
        }
        
        // Numeric stats
        var numericCols = profile.Columns.Where(c => c.InferredType == ColumnType.Numeric).Take(3).ToList();
        if (numericCols.Count > 0)
        {
            var stats = string.Join(", ", numericCols.Select(c => $"AVG(\"{c.Name}\") AS \"{c.Name}_avg\", STDDEV(\"{c.Name}\") AS \"{c.Name}_std\""));
            queries.Add(new SuggestedQuery
            {
                Purpose = "Numeric column statistics",
                Sql = $"SELECT {stats} FROM {tableName}"
            });
        }
        
        // Categorical value counts
        var catCols = profile.Columns.Where(c => c.InferredType == ColumnType.Categorical && c.UniqueCount < 20).Take(2).ToList();
        foreach (var col in catCols)
        {
            queries.Add(new SuggestedQuery
            {
                Purpose = $"Value distribution for '{col.Name}'",
                Sql = $"SELECT \"{col.Name}\", COUNT(*) as count, ROUND(100.0 * COUNT(*) / SUM(COUNT(*)) OVER(), 2) as pct FROM {tableName} GROUP BY \"{col.Name}\" ORDER BY count DESC LIMIT 10"
            });
        }
        
        // Date range
        var dateCols = profile.Columns.Where(c => c.InferredType == ColumnType.DateTime).Take(1).ToList();
        foreach (var col in dateCols)
        {
            queries.Add(new SuggestedQuery
            {
                Purpose = $"Date range for '{col.Name}'",
                Sql = $"SELECT MIN(\"{col.Name}\") as earliest, MAX(\"{col.Name}\") as latest, DATEDIFF('day', MIN(\"{col.Name}\"), MAX(\"{col.Name}\")) as span_days FROM {tableName}"
            });
        }
        
        // Outlier detection
        var outlierCol = profile.Columns.FirstOrDefault(c => c.InferredType == ColumnType.Numeric && c.OutlierCount > 0);
        if (outlierCol != null)
        {
            queries.Add(new SuggestedQuery
            {
                Purpose = $"Potential outliers in '{outlierCol.Name}'",
                Sql = $"WITH stats AS (SELECT PERCENTILE_CONT(0.25) WITHIN GROUP (ORDER BY \"{outlierCol.Name}\") AS q1, PERCENTILE_CONT(0.75) WITHIN GROUP (ORDER BY \"{outlierCol.Name}\") AS q3 FROM {tableName}) SELECT * FROM {tableName}, stats WHERE \"{outlierCol.Name}\" < q1 - 1.5*(q3-q1) OR \"{outlierCol.Name}\" > q3 + 1.5*(q3-q1) LIMIT 20"
            });
        }
        
        // Correlation check (if target exists)
        if (profile.Target != null)
        {
            var targetCol = profile.Target.ColumnName;
            var featureCols = profile.Columns.Where(c => c.Name != targetCol && c.InferredType == ColumnType.Numeric).Take(3).ToList();
            if (featureCols.Count > 0)
            {
                var corrChecks = string.Join(", ", featureCols.Select(c => $"CORR(\"{c.Name}\", \"{targetCol}\") AS \"{c.Name}_corr\""));
                queries.Add(new SuggestedQuery
                {
                    Purpose = $"Feature correlations with target '{targetCol}'",
                    Sql = $"SELECT {corrChecks} FROM {tableName}"
                });
            }
        }
        
        return queries;
    }
    
    /// <summary>
    /// Build a data dictionary for LLM context
    /// </summary>
    private static List<DataDictionaryEntry> BuildDataDictionary(DataProfile profile)
    {
        return profile.Columns.Select(c => new DataDictionaryEntry
        {
            Name = c.Name,
            Type = c.InferredType.ToString(),
            SemanticRole = c.SemanticRole != SemanticRole.Unknown ? c.SemanticRole.ToString() : null,
            Description = c.Description,
            NullPercent = c.NullPercent,
            UniqueCount = c.UniqueCount,
            UniquePercent = c.UniquePercent,
            SampleValues = c.TopValues?.Take(5).Select(v => v.Value).ToList(),
            Stats = c.InferredType == ColumnType.Numeric ? new NumericStats
            {
                Min = c.Min,
                Max = c.Max,
                Mean = c.Mean,
                Median = c.Median,
                StdDev = c.StdDev,
                Skewness = c.Skewness,
                OutlierCount = c.OutlierCount
            } : null,
            DateRange = c.InferredType == ColumnType.DateTime ? new DateRange
            {
                Min = c.MinDate?.ToString("yyyy-MM-dd"),
                Max = c.MaxDate?.ToString("yyyy-MM-dd"),
                SpanDays = c.DateSpanDays
            } : null
        }).ToList();
    }
    
    /// <summary>
    /// Build a summary of quality issues for quick LLM parsing
    /// </summary>
    private static QualityIssues BuildQualityIssues(DataProfile profile)
    {
        return new QualityIssues
        {
            HighNullColumns = profile.Columns.Where(c => c.NullPercent > 20).Select(c => new ColumnIssue { Name = c.Name, Value = c.NullPercent, Detail = $"{c.NullPercent:F1}% null" }).ToList(),
            ConstantColumns = profile.Columns.Where(c => c.UniqueCount <= 1).Select(c => new ColumnIssue { Name = c.Name, Detail = "constant value" }).ToList(),
            HighCardinalityColumns = profile.Columns.Where(c => c.UniquePercent > 90).Select(c => new ColumnIssue { Name = c.Name, Value = c.UniquePercent, Detail = $"{c.UniquePercent:F1}% unique" }).ToList(),
            OutlierColumns = profile.Columns.Where(c => c.OutlierCount > profile.RowCount * 0.01).Select(c => new ColumnIssue { Name = c.Name, Value = c.OutlierCount, Detail = $"{c.OutlierCount} outliers" }).ToList(),
            SkewedColumns = profile.Columns.Where(c => Math.Abs(c.Skewness ?? 0) > 2).Select(c => new ColumnIssue { Name = c.Name, Value = c.Skewness, Detail = $"skewness {c.Skewness:F2}" }).ToList(),
            PotentialIdColumns = profile.Columns.Where(c => c.SemanticRole == SemanticRole.Identifier || c.InferredType == ColumnType.Id).Select(c => new ColumnIssue { Name = c.Name, Detail = "likely identifier" }).ToList()
        };
    }
    
    private static LlmProfileSummary BuildProfileSummary(DataProfile profile)
    {
        return new LlmProfileSummary
        {
            SourcePath = profile.SourcePath,
            RowCount = profile.RowCount,
            ColumnCount = profile.ColumnCount,
            ProfileTimeMs = (long)profile.ProfileTime.TotalMilliseconds,
            Columns = profile.Columns.Select(c => new LlmColumnSummary
            {
                Name = c.Name,
                InferredType = c.InferredType.ToString(),
                SemanticRole = c.SemanticRole != SemanticRole.Unknown ? c.SemanticRole.ToString() : null,
                NullCount = c.NullCount,
                NullPercent = c.NullPercent,
                UniqueCount = c.UniqueCount,
                UniquePercent = c.UniquePercent,
                Min = c.Min,
                Max = c.Max,
                Mean = c.Mean,
                Median = c.Median,
                StdDev = c.StdDev,
                Skewness = c.Skewness,
                OutlierCount = c.OutlierCount > 0 ? c.OutlierCount : null,
                TopValues = c.TopValues?.Take(5).Select(v => new TopValue { Value = v.Value, Count = v.Count, Percent = v.Percent }).ToList()
            }).ToList()
        };
    }
    
    private static List<LlmAlertSummary>? BuildAlerts(DataProfile profile)
    {
        if (profile.Alerts.Count == 0) return null;
        return profile.Alerts.Select(a => new LlmAlertSummary
        {
            Type = a.Type.ToString(),
            Severity = a.Severity.ToString(),
            Column = a.Column,
            Message = a.Message
        }).ToList();
    }
    
    private static List<LlmInsightSummary>? BuildInsights(DataProfile profile)
    {
        if (profile.Insights.Count == 0) return null;
        return profile.Insights.Select(i => new LlmInsightSummary
        {
            Title = i.Title,
            Description = i.Description,
            Source = i.Source.ToString(),
            Score = i.Score,
            RelatedColumns = i.RelatedColumns.Count > 0 ? i.RelatedColumns : null,
            Sql = i.Sql
        }).ToList();
    }
    
    private static List<LlmCorrelationSummary>? BuildCorrelations(DataProfile profile)
    {
        if (profile.Correlations.Count == 0) return null;
        return profile.Correlations
            .OrderByDescending(c => Math.Abs(c.Correlation))
            .Take(20)
            .Select(c => new LlmCorrelationSummary
            {
                Column1 = c.Column1,
                Column2 = c.Column2,
                Correlation = c.Correlation,
                Strength = c.Strength
            }).ToList();
    }
    
    private static string GetColumnNotes(ColumnProfile col)
    {
        var notes = new List<string>();
        if (col.NullPercent > 20) notes.Add("high nulls");
        if (col.OutlierCount > 0) notes.Add($"{col.OutlierCount} outliers");
        if (Math.Abs(col.Skewness ?? 0) > 2) notes.Add("skewed");
        if (col.UniqueCount <= 1) notes.Add("constant");
        return notes.Count > 0 ? string.Join(", ", notes) : "-";
    }
    
    private static string ClassifyDatasetShape(DataProfile profile)
    {
        var ratio = (double)profile.Columns.Count / profile.RowCount;
        if (ratio > 0.1) return "Wide (many features, few rows)";
        if (profile.RowCount > 1_000_000) return "Large (>1M rows)";
        if (profile.RowCount < 1000) return "Small (<1K rows)";
        return "Medium";
    }
    
    private static string InferPrimaryUseCase(DataProfile profile)
    {
        if (profile.Target != null)
        {
            if (profile.Target.IsBinary) return "Binary Classification";
            if (profile.Target.ClassDistribution.Count <= 10) return "Multi-class Classification";
            return "Regression";
        }
        
        var dateCols = profile.Columns.Count(c => c.InferredType == ColumnType.DateTime);
        var numericCols = profile.Columns.Count(c => c.InferredType == ColumnType.Numeric);
        
        if (dateCols > 0 && numericCols > 0) return "Time Series Analysis";
        if (profile.Columns.Count > 20) return "Exploratory Data Analysis";
        return "General Analysis";
    }
    
    #endregion
}

#region LLM-Friendly Output DTOs

/// <summary>
/// Root output structure optimized for LLM consumption
/// </summary>
public class LlmFriendlyOutput
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string? ExecutiveSummary { get; set; }
    
    /// <summary>Compact summary for quick context</summary>
    public QuickFacts? QuickFacts { get; set; }
    
    /// <summary>Actionable recommendations</summary>
    public List<Recommendation>? Recommendations { get; set; }
    
    /// <summary>Detailed profile data</summary>
    public LlmProfileSummary? Profile { get; set; }
    
    /// <summary>Data quality alerts</summary>
    public List<LlmAlertSummary>? Alerts { get; set; }
    
    /// <summary>Statistical insights</summary>
    public List<LlmInsightSummary>? Insights { get; set; }
    
    /// <summary>Column correlations</summary>
    public List<LlmCorrelationSummary>? Correlations { get; set; }
    
    /// <summary>Suggested DuckDB queries for exploration</summary>
    public List<SuggestedQuery>? SuggestedQueries { get; set; }
    
    /// <summary>Column-by-column data dictionary</summary>
    public List<DataDictionaryEntry>? DataDictionary { get; set; }
    
    /// <summary>Categorized quality issues</summary>
    public QualityIssues? QualityIssues { get; set; }
}

public class QuickFacts
{
    public long RowCount { get; set; }
    public int ColumnCount { get; set; }
    public int NumericColumns { get; set; }
    public int CategoricalColumns { get; set; }
    public int DateTimeColumns { get; set; }
    public int TextColumns { get; set; }
    public int BooleanColumns { get; set; }
    public int IdColumns { get; set; }
    public int HighNullColumns { get; set; }
    public int ConstantColumns { get; set; }
    public int HighCardinalityColumns { get; set; }
    public int AlertCount { get; set; }
    public int ErrorAlerts { get; set; }
    public int WarningAlerts { get; set; }
    public bool HasTargetColumn { get; set; }
    public string? TargetColumn { get; set; }
    public bool? IsTargetImbalanced { get; set; }
    public int StrongCorrelations { get; set; }
    public string? DatasetShape { get; set; }
    public string? PrimaryUseCase { get; set; }
}

public class Recommendation
{
    public string Priority { get; set; } = "";
    public string Category { get; set; } = "";
    public string Issue { get; set; } = "";
    public string Action { get; set; } = "";
    public List<string>? AffectedColumns { get; set; }
    public string? Sql { get; set; }
}

public class SuggestedQuery
{
    public string Purpose { get; set; } = "";
    public string Sql { get; set; } = "";
}

public class DataDictionaryEntry
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? SemanticRole { get; set; }
    public string? Description { get; set; }
    public double NullPercent { get; set; }
    public long UniqueCount { get; set; }
    public double UniquePercent { get; set; }
    public List<string?>? SampleValues { get; set; }
    public NumericStats? Stats { get; set; }
    public DateRange? DateRange { get; set; }
}

public class NumericStats
{
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Mean { get; set; }
    public double? Median { get; set; }
    public double? StdDev { get; set; }
    public double? Skewness { get; set; }
    public long? OutlierCount { get; set; }
}

public class DateRange
{
    public string? Min { get; set; }
    public string? Max { get; set; }
    public int? SpanDays { get; set; }
}

public class QualityIssues
{
    public List<ColumnIssue> HighNullColumns { get; set; } = [];
    public List<ColumnIssue> ConstantColumns { get; set; } = [];
    public List<ColumnIssue> HighCardinalityColumns { get; set; } = [];
    public List<ColumnIssue> OutlierColumns { get; set; } = [];
    public List<ColumnIssue> SkewedColumns { get; set; } = [];
    public List<ColumnIssue> PotentialIdColumns { get; set; } = [];
}

public class ColumnIssue
{
    public string Name { get; set; } = "";
    public double? Value { get; set; }
    public string Detail { get; set; } = "";
}

public class LlmProfileSummary
{
    public string? SourcePath { get; set; }
    public long RowCount { get; set; }
    public int ColumnCount { get; set; }
    public long ProfileTimeMs { get; set; }
    public List<LlmColumnSummary> Columns { get; set; } = [];
}

public class LlmColumnSummary
{
    public string Name { get; set; } = "";
    public string InferredType { get; set; } = "";
    public string? SemanticRole { get; set; }
    public long NullCount { get; set; }
    public double NullPercent { get; set; }
    public long UniqueCount { get; set; }
    public double UniquePercent { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Mean { get; set; }
    public double? Median { get; set; }
    public double? StdDev { get; set; }
    public double? Skewness { get; set; }
    public long? OutlierCount { get; set; }
    public List<TopValue>? TopValues { get; set; }
}

public class TopValue
{
    public string? Value { get; set; }
    public long Count { get; set; }
    public double Percent { get; set; }
}

public class LlmAlertSummary
{
    public string Type { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Column { get; set; } = "";
    public string Message { get; set; } = "";
}

public class LlmInsightSummary
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Source { get; set; } = "";
    public double Score { get; set; }
    public List<string>? RelatedColumns { get; set; }
    public string? Sql { get; set; }
}

public class LlmCorrelationSummary
{
    public string Column1 { get; set; } = "";
    public string Column2 { get; set; } = "";
    public double Correlation { get; set; }
    public string Strength { get; set; } = "";
}

#endregion
