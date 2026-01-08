using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Mostlylucid.DataSummarizer.Models;
using OllamaSharp;
using OllamaSharp.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Uses LLM to generate analytical queries based on statistical profile.
/// The profile grounds the LLM - preventing hallucination about column names/types.
/// Supports tool invocation for capabilities beyond SQL (segmentation, anomaly detection, etc.)
/// </summary>
public class LlmInsightGenerator : IDisposable
{
    private readonly OllamaApiClient _ollama;
    private readonly string _model;
    private readonly string _clarifierSentinelModel;
    private readonly bool _verbose;
    private readonly bool _enableClarifierSentinel;

    private readonly bool _ollamaAvailable;
    private DuckDBConnection? _connection;
    private readonly AnalyticsToolRegistry _toolRegistry = new();
    
    /// <summary>
    /// Callback to update the profile with new cached queries/aggregate stats
    /// </summary>
    public Action<DataProfile, CachedQueryResult>? OnQueryCached { get; set; }

    public LlmInsightGenerator(
        string model = "qwen2.5-coder:7b",
        string ollamaUrl = "http://localhost:11434",
        bool verbose = false,
        bool enableClarifierSentinel = true,
        string clarifierSentinelModel = "qwen2.5:1.5b")
    {
        _model = model;
        _clarifierSentinelModel = clarifierSentinelModel;
        _verbose = verbose;
        _enableClarifierSentinel = enableClarifierSentinel;

        // Try to create client; if fails, mark unavailable
        try
        {
            _ollama = new OllamaApiClient(new Uri(ollamaUrl));
            _ollamaAvailable = true;
        }
        catch
        {
            _ollamaAvailable = false;
            _ollama = new OllamaApiClient(new Uri("http://localhost:11434")); // placeholder; guarded by _ollamaAvailable
        }
    }


    /// <summary>
    /// Generate LLM-powered insights using the statistical profile as grounding context
    /// </summary>
    public async Task<List<DataInsight>> GenerateInsightsAsync(
        string filePath, 
        DataProfile profile,
        int maxInsights = 5)
    {
        _connection = new DuckDBConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        
        // Install extensions if needed
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".xlsx" or ".xls")
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSTALL excel; LOAD excel;";
            await cmd.ExecuteNonQueryAsync();
        }

        var readExpr = GetReadExpression(filePath, profile.SheetName);
        
        // Create a view 't' so the LLM can use simple table reference
        await CreateTableViewAsync(readExpr);
        
        var insights = new List<DataInsight>();

        // Step 1: Ask LLM to generate analytical questions based on the profile
        var questions = await GenerateQuestionsAsync(profile);
        
        if (_verbose)
        {
            Console.WriteLine($"[LLM] Generated {questions.Count} analytical questions");
        }

        // Step 2: For each question, generate and execute SQL
        foreach (var question in questions.Take(maxInsights))
        {
            try
            {
                var insight = await GenerateAndExecuteInsightAsync(readExpr, profile, question);
                if (insight != null)
                {
                    insights.Add(insight);
                }
            }
            catch (Exception ex)
            {
                if (_verbose) Console.WriteLine($"[LLM] Failed: {question} - {ex.Message}");
            }
        }

        return insights;
    }

    /// <summary>
    /// Ask a specific question about the data
    /// </summary>
    public async Task<DataInsight?> AskAsync(string filePath, DataProfile profile, string question, string conversationContext = "", bool skipPrecomputedStats = false)
    {
        // GUARDRAIL: Check question for malicious intent FIRST
        var questionGuardrail = CheckQuestionGuardrails(question);
        if (questionGuardrail != null)
        {
            if (_verbose) Console.WriteLine($"[Guardrail] Question blocked: {questionGuardrail}");
            return CreateGuardrailResponse(questionGuardrail);
        }
        
        // If the user is replying with a clarifier selection (e.g., "overall" or "by rating"), rewrite the question to drive SQL
        var selection = ParseAverageSelection(question);
        if (selection == null)
        {
            // Auto-select if only one option exists
            var autoOpt = TryAutoSelectAverageOption(profile, question);
            if (autoOpt != null)
            {
                selection = autoOpt;
            }
        }

        if (selection != null)
        {
            skipPrecomputedStats = true; // force SQL/LLM path
            question = $"average {selection}";
        }
        else
        {
            // Early disambiguation: ambiguous average without column/group -> ask for dimension
            var avgClarify = TryBuildAverageClarifier(profile, question);
            if (avgClarify != null)
            {
                var refined = await RefineClarifierWithSentinelAsync(avgClarify, question, profile);
                return refined;
            }
        }

        // For broad descriptive questions, return an LLM summary without SQL
        if (!skipPrecomputedStats && IsBroadSummaryQuestion(question))
        {
            return await GenerateProfileSummaryAsync(profile, question, conversationContext);
        }
        
        // Try to answer from pre-computed stats first (faster, no SQL needed)
        if (!skipPrecomputedStats)
        {
            var statsAnswer = TryAnswerFromStats(profile, question);
            if (statsAnswer != null)
            {
                return statsAnswer;
            }
        }

        _connection = new DuckDBConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".xlsx" or ".xls")
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSTALL excel; LOAD excel;";
            await cmd.ExecuteNonQueryAsync();
        }

        var readExpr = GetReadExpression(filePath, profile.SheetName);
        
        // Create a view 't' so the LLM can use simple table reference
        await CreateTableViewAsync(readExpr);
        
        return await GenerateAndExecuteInsightAsync(readExpr, profile, question);
    }
    
    /// <summary>
    /// Check question for malicious intent before processing.
    /// Returns violation message or null if OK.
    /// </summary>
    private static string? CheckQuestionGuardrails(string question)
    {
        var lower = question.ToLowerInvariant();
        
        // Destructive intent patterns
        var destructivePatterns = new[]
        {
            ("delete", "delete data"),
            ("drop", "drop tables"),
            ("truncate", "truncate data"),
            ("destroy", "destroy data"),
            ("remove all", "remove data"),
            ("wipe", "wipe data"),
            ("erase", "erase data"),
            ("clear the database", "clear database"),
            ("reset the data", "reset data")
        };
        
        foreach (var (pattern, description) in destructivePatterns)
        {
            if (lower.Contains(pattern))
            {
                return $"I can't help with requests to {description}. I only support read-only data analysis.";
            }
        }
        
        // Injection attempt patterns
        if (lower.Contains("';") || lower.Contains("\";") || lower.Contains("--") || 
            lower.Contains("/*") || lower.Contains("union select") || lower.Contains("1=1"))
        {
            return "This looks like an SQL injection attempt. I only answer natural language questions about data.";
        }
        
        return null;
    }
    
    /// <summary>
    /// Create a helpful response when guardrails are triggered
    /// </summary>
    private static DataInsight CreateGuardrailResponse(string message)
    {
        return new DataInsight
        {
            Title = "Request Not Allowed",
            Description = $"⚠️ {message}\n\n" +
                         "**What I can help with:**\n" +
                         "- Analyzing data patterns and statistics\n" +
                         "- Finding averages, totals, and distributions\n" +
                         "- Comparing groups and segments\n" +
                         "- Identifying outliers and anomalies\n" +
                         "- Answering questions about the data structure",
            Source = InsightSource.Statistical,
            RelatedColumns = []
        };
    }
    
    /// <summary>
    /// Try to answer questions directly from pre-computed profile statistics.
    /// Returns null if SQL is needed.
    /// IMPORTANT: Only use pre-computed stats for GLOBAL questions (entire dataset).
    /// If the question has filters (e.g., "for red cars", "in 2023"), SQL is needed.
    /// </summary>
    private DataInsight? TryAnswerFromStats(DataProfile profile, string question)
    {
        var q = question.ToLowerInvariant();
        
        // Follow-up questions (it/that/this) should NOT use precomputed stats; force SQL/LLM
        if (IsFollowUpQuestion(q))
        {
            return null;
        }
        
        // Early: ambiguous average -> clarify dimension (before filter checks)
        if ((q.Contains("average") || q.Contains("mean")) && !q.Contains("most average") && !q.Contains("closest"))
        {
            var mentionsColumn = profile.Columns.Any(c => q.Contains(c.Name.ToLowerInvariant()));
            var hasEntity = HasAnyEntityMention(q, profile);
            if (!mentionsColumn && !hasEntity && !q.Contains("overall"))
            {
                var options = BuildAverageClarificationOptions(profile);
                if (options.Count > 0)
                {
                    var sbClarify = new StringBuilder();
                    sbClarify.AppendLine("Average over which dimension? Pick one:");
                    foreach (var opt in options)
                    {
                        sbClarify.AppendLine($"- {opt}");
                    }
                    return new DataInsight
                    {
                        Title = "Clarify Average Dimension",
                        Description = sbClarify.ToString().Trim(),
                        Source = InsightSource.Statistical,
                        RelatedColumns = profile.Columns.Select(c => c.Name).Take(8).ToList(),
                        Sql = "/* clarification required: average dimension not specified (no SQL executed) */",
                        Result = options
                    };
                }
            }
        }

        // Check for filter indicators - if present, we need SQL, not pre-computed stats
        // "average price for Electronics" != global average price
        if (HasFilterIndicators(q, profile))
        {
            return null; // Need SQL to filter first
        }
        
        // "Most average" / closest-to-mean/median questions require SQL to rank by distance
        if (q.Contains("most average") || q.Contains("closest to average") || q.Contains("closest to mean") || q.Contains("closest to median") || q.Contains("nearest to average") || q.Contains("typical") || q.Contains("mose average"))
        {
            return null;
        }
        
        // Questions about overall/global averages/means we already have
        if ((q.Contains("average") || q.Contains("mean")) && !q.Contains("most average") && !q.Contains("closest"))
        {
            // If no specific column mentioned and no entity mention, ask for clarification of dimension
            var mentionsColumn = profile.Columns.Any(c => q.Contains(c.Name.ToLowerInvariant()));
            var hasEntity = HasAnyEntityMention(q, profile);
            if (!mentionsColumn && !hasEntity && !q.Contains("overall"))
            {
                var options = BuildAverageClarificationOptions(profile);
                if (options.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Average over which dimension? Pick one:");
                    foreach (var opt in options)
                    {
                        sb.AppendLine($"- {opt}");
                    }
                    return new DataInsight
                    {
                        Title = "Clarify Average Dimension",
                        Description = sb.ToString().Trim(),
                        Source = InsightSource.Statistical,
                        RelatedColumns = profile.Columns.Select(c => c.Name).Take(8).ToList(),
                        Sql = "/* clarification required: average dimension not specified (no SQL executed) */",
                        Result = options
                    };
                }
            }

            // Check if asking about a specific column (global stat)
            var matchedCol = profile.Columns
                .Where(c => c.InferredType == ColumnType.Numeric && c.Mean.HasValue)
                .FirstOrDefault(c => q.Contains(c.Name.ToLowerInvariant()));
            
            if (matchedCol != null)
            {
                return new DataInsight
                {
                    Title = $"Overall Average {matchedCol.Name}",
                    Description = $"The overall average {matchedCol.Name} across all {profile.RowCount:N0} rows is **{matchedCol.Mean:F2}** (median: {matchedCol.Median:F2}, std dev: {matchedCol.StdDev:F2})",
                    Source = InsightSource.Statistical,
                    RelatedColumns = new List<string> { matchedCol.Name },
                    Sql = "/* precomputed stats: global mean/median/stddev (no SQL executed) */"
                };
            }
            
            // General "what are the averages" question (global)
            if ((q.Contains("what") || q.Contains("show")) && (q.Contains("average") || q.Contains("mean")) && q.Contains("overall") || !HasAnyEntityMention(q, profile))
            {
                var numericCols = profile.Columns.Where(c => c.Mean.HasValue).ToList();
                if (numericCols.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"**Overall averages (across all {profile.RowCount:N0} rows):**");
                    foreach (var col in numericCols)
                    {
                        sb.AppendLine($"- **{col.Name}**: mean={col.Mean:F2}, median={col.Median:F2}, range=[{col.Min:F2}, {col.Max:F2}]");
                    }
                    return new DataInsight
                    {
                        Title = "Numeric Column Statistics",
                        Description = sb.ToString().Trim(),
                        Source = InsightSource.Statistical,
                        RelatedColumns = numericCols.Select(c => c.Name).ToList(),
                        Sql = "/* precomputed stats: global numeric summaries (no SQL executed) */"
                    };
                }
            }
        }
        
        // Questions about overall distribution (no filtering)
        if ((q.Contains("distribution") || q.Contains("distributed") || q.Contains("skewed") || q.Contains("normal")) 
            && (q.Contains("overall") || !HasFilterIndicators(q, profile)))
        {
            var distCols = profile.Columns
                .Where(c => c.Distribution.HasValue && c.Distribution != DistributionType.Unknown)
                .ToList();
            
            if (distCols.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("**Overall distribution analysis:**");
                foreach (var col in distCols)
                {
                    var skewDesc = col.Skewness switch
                    {
                        > 1 => "right-skewed",
                        < -1 => "left-skewed",
                        _ => "approximately symmetric"
                    };
                    sb.AppendLine($"- **{col.Name}**: {col.Distribution} ({skewDesc}, skewness={col.Skewness:F2}, kurtosis={col.Kurtosis:F2})");
                }
                return new DataInsight
                {
                    Title = "Distribution Analysis",
                    Description = sb.ToString().Trim(),
                    Source = InsightSource.Statistical,
                    RelatedColumns = distCols.Select(c => c.Name).ToList(),
                    Sql = "/* precomputed stats: distribution/skewness/kurtosis (no SQL executed) */"
                };
            }
        }
        
        // Questions about outliers (global detection)
        if (q.Contains("outlier") && !HasFilterIndicators(q, profile))
        {
            var outlierCols = profile.Columns.Where(c => c.OutlierCount > 0).ToList();
            if (outlierCols.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("**Outlier detection (IQR method, all data):**");
                foreach (var col in outlierCols)
                {
                    var pct = col.Count > 0 ? col.OutlierCount * 100.0 / col.Count : 0;
                    var iqr = (col.Q75 ?? 0) - (col.Q25 ?? 0);
                    sb.AppendLine($"- **{col.Name}**: {col.OutlierCount} outliers ({pct:F1}%) outside [{(col.Q25 ?? 0) - 1.5*iqr:F2}, {(col.Q75 ?? 0) + 1.5*iqr:F2}]");
                }
                return new DataInsight
                {
                    Title = "Outlier Analysis",
                    Description = sb.ToString().Trim(),
                    Source = InsightSource.Statistical,
                    RelatedColumns = outlierCols.Select(c => c.Name).ToList()
                };
            }
            else
            {
                return new DataInsight
                {
                    Title = "Outlier Analysis",
                    Description = "No significant outliers detected in numeric columns using the IQR method.",
                    Source = InsightSource.Statistical
                };
            }
        }
        
        // Questions about correlation (always global)
        if (q.Contains("correlat") && profile.Correlations.Count > 0)
        {
            var strongCorrs = profile.Correlations.Where(c => Math.Abs(c.Correlation) >= 0.5).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("**Correlations (computed across all data):**");
            if (strongCorrs.Count > 0)
            {
                foreach (var corr in strongCorrs.OrderByDescending(c => Math.Abs(c.Correlation)).Take(10))
                {
                    var direction = corr.Correlation > 0 ? "positive" : "negative";
                    sb.AppendLine($"- **{corr.Column1}** ↔ **{corr.Column2}**: r={corr.Correlation:F3} ({corr.Strength} {direction})");
                }
            }
            else
            {
                sb.AppendLine("No strong correlations (|r| ≥ 0.5) found between numeric columns.");
            }
            return new DataInsight
            {
                Title = "Correlation Analysis",
                Description = sb.ToString().Trim(),
                Source = InsightSource.Statistical,
                RelatedColumns = strongCorrs.SelectMany(c => new[] { c.Column1, c.Column2 }).Distinct().ToList()
            };
        }
        
        return null; // Need SQL for this question
    }
    
    /// <summary>
    /// Check if the question contains filter indicators that require SQL.
    /// E.g., "for Electronics", "in North region", "where price > 100"
    /// </summary>
    private static bool HasFilterIndicators(string q, DataProfile profile)
    {
        // Common filter phrases
        var filterPhrases = new[] { 
            " for ", " in ", " where ", " when ", " by ", " per ", " among ", " within ",
            " only ", " just ", " specific", " particular", " certain"
        };
        
        if (filterPhrases.Any(f => q.Contains(f)))
        {
            // Check if any categorical values are mentioned (indicates filtering)
            foreach (var col in profile.Columns.Where(c => c.TopValues?.Count > 0))
            {
                foreach (var val in col.TopValues!.Take(10))
                {
                    if (q.Contains(val.Value.ToLowerInvariant()))
                    {
                        return true; // Filtering by a specific category value
                    }
                }
            }
            
            // Check for comparison operators suggesting WHERE clause
            if (q.Contains(">") || q.Contains("<") || q.Contains("greater") || q.Contains("less") || 
                q.Contains("more than") || q.Contains("less than") || q.Contains("above") || q.Contains("below"))
            {
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Check if the question mentions specific entities (products, regions, etc.)
    /// that would require grouping/filtering.
    /// </summary>
    private static bool HasAnyEntityMention(string q, DataProfile profile)
    {
        // Check if categorical column names are mentioned in a "by X" or "per X" context
        foreach (var col in profile.Columns.Where(c => c.InferredType == ColumnType.Categorical))
        {
            var colLower = col.Name.ToLowerInvariant();
            if (q.Contains($"by {colLower}") || q.Contains($"per {colLower}") || 
                q.Contains($"each {colLower}") || q.Contains($"for each"))
            {
                return true;
            }
        }
        return false;
    }

    private DataInsight? TryBuildAverageClarifier(DataProfile profile, string question)
    {
        var q = question.ToLowerInvariant();
        if (!(q.Contains("average") || q.Contains("mean"))) return null;
        if (q.Contains("most average") || q.Contains("closest")) return null;

        var mentionsColumn = profile.Columns.Any(c => q.Contains(c.Name.ToLowerInvariant()));
        var hasEntity = HasAnyEntityMention(q, profile);
        if (mentionsColumn || hasEntity || q.Contains("overall")) return null;

        var options = BuildAverageClarificationOptions(profile);
        if (options.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Average over which dimension? Pick one:");
        foreach (var opt in options)
        {
            sb.AppendLine($"- {opt}");
        }

        return new DataInsight
        {
            Title = "Clarify Average Dimension",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            RelatedColumns = profile.Columns.Select(c => c.Name).Take(8).ToList(),
            Sql = "/* clarification required: average dimension not specified (no SQL executed) */",
            Result = options
        };
    }

    private static List<string> BuildAverageClarificationOptions(DataProfile profile)
    {
        var options = new List<string>();
        options.Add("overall");

        // Prefer obvious groupings by name (year/genre) if present (case-insensitive, substring match)
        var preferred = profile.Columns
            .Where(c => c.InferredType == ColumnType.Categorical && c.UniqueCount > 1 && c.UniqueCount <= 5000 && c.NullPercent < 90)
            .OrderBy(c => c.Name.Length)
            .ToList();

        bool NameLike(ColumnProfile c, string token)
            => c.Name.Contains(token, StringComparison.OrdinalIgnoreCase);

        void AddIfPresent(Func<ColumnProfile, bool> predicate)
        {
            var col = preferred.FirstOrDefault(predicate);
            if (col != null) options.Add($"by {col.Name}");
        }
        AddIfPresent(c => NameLike(c, "year") || NameLike(c, "yr"));
        AddIfPresent(c => NameLike(c, "genre"));

        // Fill remaining slots with top categorical columns by distinct count and coverage
        var candidates = preferred
            .Where(c => !options.Any(o => o.EndsWith(c.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c => c.UniqueCount)
            .ThenBy(c => c.NullPercent)
            .Take(6)
            .Select(c => c.Name)
            .ToList();

        foreach (var c in candidates)
        {
            options.Add($"by {c}");
        }

        // Offer a numeric binning option if there are numeric columns
        var numeric = profile.Columns.FirstOrDefault(c => c.InferredType == ColumnType.Numeric);
        if (numeric != null)
        {
            options.Add($"by {numeric.Name} quartiles");
        }

        return options.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ParseAverageSelection(string question)
    {
        var q = question.Trim().ToLowerInvariant();
        if (q == "overall" || q == "overall (no grouping)") return "overall";
        if (q.StartsWith("by ")) return q;
        if (q.EndsWith(" quartiles") && q.StartsWith("by ")) return q;
        return null;
    }

    private static string? TryAutoSelectAverageOption(DataProfile profile, string question)
    {
        var q = question.ToLowerInvariant();
        if (!(q.Contains("average") || q.Contains("mean"))) return null;
        if (q.Contains("most average") || q.Contains("closest")) return null;

        var mentionsColumn = profile.Columns.Any(c => q.Contains(c.Name.ToLowerInvariant()));
        var hasEntity = HasAnyEntityMention(q, profile);
        if (mentionsColumn || hasEntity || q.Contains("overall")) return null;

        var options = BuildAverageClarificationOptions(profile);
        // Always at least overall; auto-select only when exactly 1 option (rare) or exactly 2 with overall + one grouping
        var distinctOpts = options.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinctOpts.Count == 1)
            return distinctOpts[0];
        if (distinctOpts.Count == 2 && distinctOpts.Contains("overall", StringComparer.OrdinalIgnoreCase))
            return distinctOpts.First(o => !o.Equals("overall", StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private async Task<DataInsight> RefineClarifierWithSentinelAsync(DataInsight clarifier, string question, DataProfile profile)
    {
        // Use a tiny model if available; otherwise return as-is
        if (!_enableClarifierSentinel || !_ollamaAvailable)
            return clarifier;

        var options = clarifier.Result as IEnumerable<string> ?? Array.Empty<string>();
        var optList = options.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        if (optList.Count <= 2) return clarifier; // nothing to refine

        // Force a very small sentinel model; prefer explicit tiny/mini, otherwise use qwen2.5:1.5b as best-effort
        var modelToUse = (!string.IsNullOrEmpty(_model) && (_model.Contains("1b", StringComparison.OrdinalIgnoreCase) || _model.Contains("mini", StringComparison.OrdinalIgnoreCase)))
            ? _model
            : _clarifierSentinelModel; // best-effort; may not exist, caught below

        try
        {
            var prompt = $"""
You are selecting the best grouping options for an average/mean question.
Given the options below, pick up to 4 that are most relevant for grouping:
{string.Join("\n", optList.Select(o => "- " + o))}

Return them as a comma-separated list, no prose.
""";
            var resp = await _ollama.GenerateAsync(new OllamaSharp.Models.GenerateRequest
            {
                Model = modelToUse,
                Prompt = prompt
            }).StreamToEndAsync();
            var text = (resp?.Response ?? "").ToLowerInvariant();
            var refined = text.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => optList.Any(o => o.Equals(s, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
            if (refined.Count == 0) return clarifier;

            var sb = new StringBuilder();
            sb.AppendLine("Average over which dimension? Pick one:");
            foreach (var opt in refined)
            {
                sb.AppendLine($"- {opt}");
            }

            return new DataInsight
            {
                Title = clarifier.Title,
                Description = sb.ToString().Trim(),
                Source = clarifier.Source,
                RelatedColumns = clarifier.RelatedColumns,
                Sql = clarifier.Sql,
                Result = refined
            };
        }
        catch
        {
            return clarifier; // graceful fallback if small model unavailable
        }
    }

    private static bool IsBroadSummaryQuestion(string question)
    {
        var q = question.ToLowerInvariant();
        
        // Check if this is a follow-up question referencing previous context
        // These should NOT be treated as broad summary questions
        if (IsFollowUpQuestion(q))
        {
            return false;
        }
        
        // Only treat as broad summary if asking about the dataset in general
        return (q.Contains("tell me about the data") || 
                q.Contains("tell me about this data") || 
                q.Contains("tell me about the dataset") ||
                q.Contains("overview of the data") ||
                q.Contains("summarize the data") || 
                q.Contains("data summary") ||
                q.Contains("dataset summary") ||
                q.Contains("what is in this data") ||
                q.Contains("what's in this data") ||
                q.Contains("describe the data") ||
                q.Contains("describe this data"));
    }
    
    /// <summary>
    /// Detect if a question is a follow-up referencing previous context.
    /// These questions should NOT trigger the profile summary path.
    /// </summary>
    private static bool IsFollowUpQuestion(string question)
    {
        var q = question.ToLowerInvariant().Trim();
        
        // Short questions with pronouns typically reference previous context
        var pronounPatterns = new[]
        {
            "tell me about it",
            "tell me more about it",
            "what about it",
            "describe it",
            "show me it",
            "more about it",
            "details about it",
            "info about it",
            "information about it",
            "what information do we have about it",
            "what do we know about it",
            "what is it",
            "what's it",
            "its details",
            "its name",
            "its price",
            "its info",
            // "that" references
            "tell me about that",
            "what is that",
            "what's that",
            "describe that",
            "more about that",
            "info about that",
            "information about that",
            // "this" references  
            "tell me about this",
            "what is this",
            "what's this",
            "describe this",
            "more about this",
            "info about this",
            "information about this",
            // "the" + noun (referencing previous result)
            "the wine",
            "the product",
            "the item",
            "the result",
            "the record",
            "the row"
        };
        
        foreach (var pattern in pronounPatterns)
        {
            if (q.Contains(pattern))
                return true;
        }
        
        // Very short questions starting with pronouns are likely follow-ups
        if (q.StartsWith("it ") || q.StartsWith("its ") || q.StartsWith("that ") || q.StartsWith("this "))
            return true;
            
        // Questions that are just asking for more info
        if (q == "tell me more" || q == "more details" || q == "more info" || q == "continue" || q == "go on")
            return true;
            
        return false;
    }

    private async Task<DataInsight> GenerateProfileSummaryAsync(DataProfile profile, string question, string conversationContext)
    {
        var prompt = BuildProfileSummaryPrompt(profile, question, conversationContext);
        var req = new GenerateRequest { Model = _model, Prompt = prompt };
        var resp = await _ollama.GenerateAsync(req).StreamToEndAsync();
        var text = (resp?.Response ?? "").Trim();

        return new DataInsight
        {
            Title = "Dataset summary",
            Description = text,
            Source = InsightSource.LlmGenerated,
            RelatedColumns = profile.Columns.Select(c => c.Name).Take(10).ToList()
        };
    }

    private string BuildProfileSummaryPrompt(DataProfile profile, string question, string conversationContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a precise data analyst. Give a short, factual summary of the dataset using ONLY the provided profile. Do not speculate or invent columns. Keep it to 3-5 sentences.");
        sb.AppendLine();
        sb.AppendLine($"Question: {question}");
        if (!string.IsNullOrWhiteSpace(conversationContext))
        {
            sb.AppendLine();
            sb.AppendLine("Prior conversation (for continuity, do not invent new facts):");
            sb.AppendLine(conversationContext);
        }
        sb.AppendLine();
        sb.AppendLine($"Rows: {profile.RowCount:N0}, Columns: {profile.ColumnCount}");
        sb.AppendLine("Column types:");
        sb.AppendLine($"- Numeric: {profile.Columns.Count(c => c.InferredType == ColumnType.Numeric)}");
        sb.AppendLine($"- Categorical: {profile.Columns.Count(c => c.InferredType == ColumnType.Categorical)}");
        sb.AppendLine($"- Date/Time: {profile.Columns.Count(c => c.InferredType == ColumnType.DateTime)}");
        sb.AppendLine();
        sb.AppendLine("Columns:");
        foreach (var col in profile.Columns.Take(12))
        {
            var parts = new List<string> { col.InferredType.ToString() };
            if (col.Mean.HasValue) parts.Add($"mean {col.Mean:F1}");
            if (col.StdDev.HasValue) parts.Add($"std {col.StdDev:F1}");
            if (col.Mad.HasValue) parts.Add($"mad {col.Mad:F1}");
            if (col.Min.HasValue && col.Max.HasValue) parts.Add($"range {col.Min:F1}-{col.Max:F1}");
            if (col.Skewness.HasValue) parts.Add($"skew {col.Skewness:F2}");
            if (col.OutlierCount > 0) parts.Add($"outliers {col.OutlierCount}");
            if (col.TopValues?.Count > 0) parts.Add($"top {col.TopValues[0].Value}");
            if (col.TextPatterns.Count > 0) parts.Add($"pattern {col.TextPatterns[0].PatternType}");
            if (col.Distribution.HasValue && col.Distribution != DistributionType.Unknown) parts.Add($"dist {col.Distribution}");
            if (col.Trend?.Direction is TrendDirection.Increasing or TrendDirection.Decreasing) parts.Add($"trend {col.Trend.Direction} (R2={col.Trend.RSquared:F2})");
            if (col.TimeSeries != null) parts.Add($"ts {col.TimeSeries.Granularity}");
            sb.AppendLine($"- {col.Name}: {string.Join(", ", parts)}");
        }
        sb.AppendLine();
        if (profile.Correlations.Count > 0)
        {
            sb.AppendLine("Correlations (|r|>=0.3):");
            foreach (var corr in profile.Correlations.Take(5))
                sb.AppendLine($"- {corr.Column1} ↔ {corr.Column2}: {corr.Correlation:F2}");
        }
        if (profile.Alerts.Count > 0)
        {
            sb.AppendLine("Alerts:");
            foreach (var alert in profile.Alerts.Take(5))
                sb.AppendLine($"- {alert.Column}: {alert.Message}");
        }
        if (profile.Patterns.Count > 0)
        {
            sb.AppendLine("Patterns:");
            foreach (var p in profile.Patterns.Take(5))
                sb.AppendLine($"- {p.Type}: {p.Description}");
        }
        return sb.ToString();
    }

    public class ReportNarrative
    {
        public string Summary { get; set; } = string.Empty;
        public Dictionary<string, string> FocusAnswers { get; set; } = new();
    }

    public async Task<ReportNarrative> GenerateReportNarrativeAsync(DataProfile profile, IReadOnlyCollection<string>? focusQuestions)
    {
        var prompt = BuildNarrativePrompt(profile, focusQuestions ?? Array.Empty<string>());
        var request = new GenerateRequest { Model = _model, Prompt = prompt };
        var response = await _ollama.GenerateAsync(request).StreamToEndAsync();
        var text = (response?.Response ?? string.Empty).Trim();
        return ParseNarrative(text, focusQuestions ?? Array.Empty<string>());
    }

    private string BuildNarrativePrompt(DataProfile profile, IReadOnlyCollection<string> focusQuestions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a senior data analyst. Using only the profile below, write a concise summary (3 sentences) and answer each focus question directly.");
        sb.AppendLine("Do NOT invent columns or values. Ground every statement in the stats provided.");
        sb.AppendLine();
        sb.AppendLine("Return valid JSON with this schema:");
        sb.AppendLine("{ \"summary\": \"...\", \"focus\": [ { \"question\": \"...\", \"answer\": \"...\" } ] }");
        sb.AppendLine();
        sb.AppendLine($"Rows: {profile.RowCount:N0}, Columns: {profile.ColumnCount}");
        sb.AppendLine("Columns:");
        foreach (var col in profile.Columns.Take(15))
        {
            var parts = new List<string> { col.InferredType.ToString() };
            if (col.Mean.HasValue) parts.Add($"mean {col.Mean:F1}");
            if (col.StdDev.HasValue) parts.Add($"std {col.StdDev:F1}");
            if (col.Min.HasValue && col.Max.HasValue) parts.Add($"range {col.Min:F1}-{col.Max:F1}");
            if (col.NullPercent > 0) parts.Add($"nulls {col.NullPercent:F1}%");
            if (col.TopValues?.Count > 0) parts.Add($"top {col.TopValues[0].Value}");
            sb.AppendLine($"- {col.Name}: {string.Join(", ", parts)}");
        }
        if (profile.Target != null)
        {
            sb.AppendLine();
            sb.AppendLine("Target distribution:");
            foreach (var kvp in profile.Target.ClassDistribution)
            {
                sb.AppendLine($"- {kvp.Key}: {kvp.Value:P1}");
            }
        }
        if (focusQuestions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("FocusQuestions:");
            foreach (var question in focusQuestions)
            {
                sb.AppendLine($"- {question}");
            }
        }
        return sb.ToString();
    }

    private ReportNarrative ParseNarrative(string rawResponse, IReadOnlyCollection<string> focusQuestions)
    {
        var narrative = new ReportNarrative();
        var json = ExtractJsonBlock(rawResponse);
        if (json != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("summary", out var summary))
                {
                    narrative.Summary = summary.GetString() ?? string.Empty;
                }
                if (doc.RootElement.TryGetProperty("focus", out var focusArray) && focusArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in focusArray.EnumerateArray())
                    {
                        var question = item.TryGetProperty("question", out var qEl) ? qEl.GetString() : null;
                        var answer = item.TryGetProperty("answer", out var aEl) ? aEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(question) && !string.IsNullOrWhiteSpace(answer))
                        {
                            narrative.FocusAnswers[question!] = answer!;
                        }
                    }
                }
            }
            catch
            {
                narrative.Summary = rawResponse;
            }
        }
        else
        {
            narrative.Summary = rawResponse;
        }

        if (focusQuestions.Count > 0 && narrative.FocusAnswers.Count == 0)
        {
            foreach (var question in focusQuestions)
            {
                narrative.FocusAnswers[question] = "Not enough information to answer without LLM output.";
            }
        }

        return narrative;
    }

    private static string? ExtractJsonBlock(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return response[start..(end + 1)];
        }
        return null;
    }

    private async Task<List<string>> GenerateQuestionsAsync(DataProfile profile)
    {
        var prompt = BuildQuestionGenerationPrompt(profile);
        
        var request = new GenerateRequest { Model = _model, Prompt = prompt };
        var response = await _ollama.GenerateAsync(request).StreamToEndAsync();
        
        // Parse questions from response (one per line)
        var questions = (response?.Response ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(q => q.Trim().TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' '))
            .Where(q => q.Length > 10 && !q.StartsWith("```"))
            .Take(10)
            .ToList();

        return questions;
    }

    private string BuildQuestionGenerationPrompt(DataProfile profile)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("You are a data analyst. Based on the dataset profile below, generate 5-7 insightful analytical questions that would reveal interesting patterns or business insights.");
        sb.AppendLine();
        sb.AppendLine("DATASET PROFILE:");
        sb.AppendLine($"- Rows: {profile.RowCount:N0}");
        sb.AppendLine($"- Source: {profile.SourcePath}");
        sb.AppendLine();
        
        sb.AppendLine("COLUMNS:");
        foreach (var col in profile.Columns)
        {
            sb.Append($"  - {col.Name} ({col.InferredType})");
            
            if (col.InferredType == ColumnType.Numeric && col.Mean.HasValue)
            {
                var madText = col.Mad.HasValue ? $", MAD: {col.Mad:F1}" : "";
                var skewText = col.Skewness.HasValue ? $", skew: {col.Skewness:F2}" : "";
                sb.Append($" [range: {col.Min:F0}-{col.Max:F0}, avg: {col.Mean:F1}, std: {col.StdDev:F1}{madText}{skewText}]");
            }
            else if (col.InferredType == ColumnType.Categorical && col.TopValues?.Count > 0)
            {
                var topVals = string.Join(", ", col.TopValues.Take(3).Select(v => v.Value));
                sb.Append($" [values: {topVals}...]");
            }
            else if (col.InferredType == ColumnType.DateTime)
            {
                sb.Append($" [range: {col.MinDate:yyyy-MM-dd} to {col.MaxDate:yyyy-MM-dd}]");
            }
            
            if (col.NullPercent > 5)
            {
                sb.Append($" ({col.NullPercent:F0}% null)");
            }
            
            sb.AppendLine();
        }

        // Include alerts as hints
        if (profile.Alerts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("DATA QUALITY NOTES:");
            foreach (var alert in profile.Alerts.Take(5))
            {
                sb.AppendLine($"  - {alert.Column}: {alert.Message}");
            }
        }

        // Include correlations as hints
        if (profile.Correlations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("CORRELATIONS DETECTED:");
            foreach (var corr in profile.Correlations.Take(3))
            {
                sb.AppendLine($"  - {corr.Column1} ↔ {corr.Column2}: {corr.Correlation:F2} ({corr.Strength})");
            }
        }

        // Alerts overview
        if (profile.Alerts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("DATA QUALITY:");
            var warn = profile.Alerts.Count(a => a.Severity == AlertSeverity.Warning);
            var info = profile.Alerts.Count(a => a.Severity == AlertSeverity.Info);
            sb.AppendLine($"  - {profile.Alerts.Count} alerts (warnings: {warn}, info: {info})");
            foreach (var alert in profile.Alerts.Take(3))
            {
                sb.AppendLine($"    * {alert.Column}: {alert.Message}");
            }
        }

        // Include detected patterns
        AppendPatternContext(sb, profile);

        sb.AppendLine();
        sb.AppendLine("Generate 5-7 analytical questions. Focus on:");
        sb.AppendLine("- Trends and patterns");
        sb.AppendLine("- Comparisons between categories");
        sb.AppendLine("- Distributions and outliers");
        sb.AppendLine("- Relationships between columns");
        sb.AppendLine("- Aggregations that reveal business insights");
        
        if (profile.Columns.Any(c => c.TimeSeries != null))
            sb.AppendLine("- Time-based analysis and seasonality");
        
        if (profile.Columns.Any(c => c.Trend != null))
            sb.AppendLine("- Growth/decline rates and projections");
            
        sb.AppendLine();
        sb.AppendLine("Return ONLY the questions, one per line, no numbering:");

        return sb.ToString();
    }

    private static void AppendPatternContext(StringBuilder sb, DataProfile profile)
    {
        var hasPatterns = false;

        // Time series info
        var dateCol = profile.Columns.FirstOrDefault(c => c.TimeSeries != null);
        if (dateCol?.TimeSeries != null)
        {
            if (!hasPatterns)
            {
                sb.AppendLine();
                sb.AppendLine("DETECTED PATTERNS:");
                hasPatterns = true;
            }
            
            var ts = dateCol.TimeSeries;
            sb.Append($"  - TIME SERIES: {ts.Granularity} data indexed by '{dateCol.Name}'");
            if (!ts.IsContiguous)
                sb.Append($" ({ts.GapCount} gaps)");
            if (ts.HasSeasonality)
                sb.Append($" [seasonal period: {ts.SeasonalPeriod}]");
            sb.AppendLine();
        }

        // Distribution info
        var distributedCols = profile.Columns
            .Where(c => c.Distribution.HasValue && c.Distribution != DistributionType.Unknown)
            .Take(3)
            .ToList();
        
        if (distributedCols.Any())
        {
            if (!hasPatterns)
            {
                sb.AppendLine();
                sb.AppendLine("DETECTED PATTERNS:");
                hasPatterns = true;
            }
            
            foreach (var col in distributedCols)
            {
                sb.AppendLine($"  - DISTRIBUTION: '{col.Name}' is {col.Distribution}");
            }
        }

        // Trend info
        var trendCols = profile.Columns
            .Where(c => c.Trend != null && c.Trend.Direction != TrendDirection.None)
            .Take(3)
            .ToList();
        
        if (trendCols.Any())
        {
            if (!hasPatterns)
            {
                sb.AppendLine();
                sb.AppendLine("DETECTED PATTERNS:");
                hasPatterns = true;
            }
            
            foreach (var col in trendCols)
            {
                var t = col.Trend!;
                sb.AppendLine($"  - TREND: '{col.Name}' is {t.Direction} (R²={t.RSquared:F2})");
            }
        }

        // Text patterns
        var textPatternCols = profile.Columns
            .Where(c => c.TextPatterns.Count > 0)
            .Take(3)
            .ToList();
        
        if (textPatternCols.Any())
        {
            if (!hasPatterns)
            {
                sb.AppendLine();
                sb.AppendLine("DETECTED PATTERNS:");
                hasPatterns = true;
            }
            
            foreach (var col in textPatternCols)
            {
                var pattern = col.TextPatterns.First();
                sb.AppendLine($"  - TEXT FORMAT: '{col.Name}' contains {pattern.PatternType} values ({pattern.MatchPercent:F0}%)");
            }
        }

        // Dataset-level patterns
        if (profile.Patterns.Count > 0)
        {
            if (!hasPatterns)
            {
                sb.AppendLine();
                sb.AppendLine("DETECTED PATTERNS:");
            }
            
            foreach (var pattern in profile.Patterns.Take(3))
            {
                sb.AppendLine($"  - {pattern.Type}: {pattern.Description}");
            }
        }
    }

    /// <summary>
    /// Appends information about pre-computed advanced analytics to help LLM
    /// understand what tools are available beyond SQL.
    /// </summary>
    private static void AppendAdvancedAnalyticsContext(StringBuilder sb, DataProfile profile)
    {
        var hasTools = false;
        
        // Correlation analysis
        var strongCorrelations = profile.Correlations
            .Where(c => Math.Abs(c.Correlation) >= 0.5)
            .OrderByDescending(c => Math.Abs(c.Correlation))
            .Take(5)
            .ToList();
        
        if (strongCorrelations.Count > 0)
        {
            if (!hasTools)
            {
                sb.AppendLine();
                sb.AppendLine("PRE-COMPUTED ANALYSIS (reference these instead of computing via SQL):");
                hasTools = true;
            }
            
            sb.AppendLine("  CORRELATIONS:");
            foreach (var corr in strongCorrelations)
            {
                var direction = corr.Correlation > 0 ? "positive" : "negative";
                sb.AppendLine($"    - \"{corr.Column1}\" ↔ \"{corr.Column2}\": {corr.Correlation:F2} ({corr.Strength} {direction})");
            }
        }
        
        // Trend analysis
        var trendedCols = profile.Columns
            .Where(c => c.Trend != null && c.Trend.Direction != TrendDirection.None && c.Trend.RSquared >= 0.5)
            .Take(3)
            .ToList();
        
        if (trendedCols.Count > 0)
        {
            if (!hasTools)
            {
                sb.AppendLine();
                sb.AppendLine("PRE-COMPUTED ANALYSIS (reference these instead of computing via SQL):");
                hasTools = true;
            }
            
            sb.AppendLine("  TRENDS:");
            foreach (var col in trendedCols)
            {
                var t = col.Trend!;
                var slopeDir = t.Slope > 0 ? "+" : "";
                sb.AppendLine($"    - \"{col.Name}\": {t.Direction} trend (slope={slopeDir}{t.Slope:F4}, R²={t.RSquared:F2})");
            }
        }
        
        // Periodicity / Seasonality
        var periodicCols = profile.Columns
            .Where(c => c.Periodicity != null && c.Periodicity.HasPeriodicity && c.Periodicity.Confidence >= 0.5)
            .Take(3)
            .ToList();
        
        if (periodicCols.Count > 0)
        {
            if (!hasTools)
            {
                sb.AppendLine();
                sb.AppendLine("PRE-COMPUTED ANALYSIS (reference these instead of computing via SQL):");
                hasTools = true;
            }
            
            sb.AppendLine("  PERIODICITY/SEASONALITY:");
            foreach (var col in periodicCols)
            {
                var p = col.Periodicity!;
                sb.AppendLine($"    - \"{col.Name}\": {p.SuggestedInterpretation} (confidence={p.Confidence:F2})");
            }
        }
        
        // Time series info
        var tsCol = profile.Columns.FirstOrDefault(c => c.TimeSeries != null);
        if (tsCol?.TimeSeries != null)
        {
            if (!hasTools)
            {
                sb.AppendLine();
                sb.AppendLine("PRE-COMPUTED ANALYSIS (reference these instead of computing via SQL):");
                hasTools = true;
            }
            
            var ts = tsCol.TimeSeries;
            sb.AppendLine("  TIME SERIES:");
            sb.Append($"    - Indexed by \"{tsCol.Name}\": {ts.Granularity} granularity");
            if (!ts.IsContiguous)
                sb.Append($", {ts.GapCount} gaps detected");
            if (ts.HasSeasonality)
                sb.Append($", seasonal period={ts.SeasonalPeriod}");
            sb.AppendLine();
        }
        
        // Distribution types (useful for knowing data shape)
        var distributedCols = profile.Columns
            .Where(c => c.Distribution.HasValue && c.Distribution != DistributionType.Unknown)
            .Take(5)
            .ToList();
        
        if (distributedCols.Count > 0)
        {
            if (!hasTools)
            {
                sb.AppendLine();
                sb.AppendLine("PRE-COMPUTED ANALYSIS (reference these instead of computing via SQL):");
                hasTools = true;
            }
            
            sb.AppendLine("  DISTRIBUTIONS:");
            foreach (var col in distributedCols)
            {
                sb.AppendLine($"    - \"{col.Name}\": {col.Distribution}");
            }
        }
        
        // Outlier counts (pre-computed via IQR)
        var outlierCols = profile.Columns
            .Where(c => c.OutlierCount > 0)
            .OrderByDescending(c => c.OutlierCount)
            .Take(3)
            .ToList();
        
        if (outlierCols.Count > 0)
        {
            if (!hasTools)
            {
                sb.AppendLine();
                sb.AppendLine("PRE-COMPUTED ANALYSIS (reference these instead of computing via SQL):");
                hasTools = true;
            }
            
            sb.AppendLine("  OUTLIERS (IQR method):");
            foreach (var col in outlierCols)
            {
                var pct = col.Count > 0 ? (col.OutlierCount * 100.0 / col.Count) : 0;
                sb.AppendLine($"    - \"{col.Name}\": {col.OutlierCount} outliers ({pct:F1}%)");
            }
        }
        
        // Target analysis (if available)
        if (profile.Target != null)
        {
            if (!hasTools)
            {
                sb.AppendLine();
                sb.AppendLine("PRE-COMPUTED ANALYSIS (reference these instead of computing via SQL):");
                hasTools = true;
            }
            
            sb.AppendLine($"  TARGET ANALYSIS (column=\"{profile.Target.ColumnName}\"):");
            sb.AppendLine($"    - Type: {(profile.Target.IsBinary ? "Binary" : "Multiclass")}");
            
            var distStr = string.Join(", ", profile.Target.ClassDistribution
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => $"{kv.Key}={kv.Value:F1}%"));
            sb.AppendLine($"    - Distribution: {distStr}");
            
            if (profile.Target.FeatureEffects.Count > 0)
            {
                sb.AppendLine("    - Top feature drivers:");
                foreach (var effect in profile.Target.FeatureEffects.Take(5))
                {
                    sb.AppendLine($"      * \"{effect.Feature}\": {effect.Summary}");
                }
            }
        }
        
        // Detected patterns (clustering, etc.)
        var clusterPatterns = profile.Patterns
            .Where(p => p.Type == PatternType.Clustering)
            .Take(2)
            .ToList();
        
        if (clusterPatterns.Count > 0)
        {
            if (!hasTools)
            {
                sb.AppendLine();
                sb.AppendLine("PRE-COMPUTED ANALYSIS (reference these instead of computing via SQL):");
                hasTools = true;
            }
            
            sb.AppendLine("  CLUSTERING:");
            foreach (var pattern in clusterPatterns)
            {
                sb.AppendLine($"    - {pattern.Description} (confidence={pattern.Confidence:F2})");
            }
        }
        
        // PII detection alerts
        var piiAlerts = profile.Alerts
            .Where(a => a.Type == AlertType.PiiDetected)
            .Take(3)
            .ToList();
        
        if (piiAlerts.Count > 0)
        {
            if (!hasTools)
            {
                sb.AppendLine();
                sb.AppendLine("PRE-COMPUTED ANALYSIS (reference these instead of computing via SQL):");
                hasTools = true;
            }
            
            sb.AppendLine("  PII/SENSITIVE DATA DETECTED:");
            foreach (var alert in piiAlerts)
            {
                sb.AppendLine($"    - \"{alert.Column}\": {alert.Message}");
            }
        }
        
        if (hasTools)
        {
            sb.AppendLine();
            sb.AppendLine("NOTE: For questions about correlations, trends, seasonality, outliers, or clustering,");
            sb.AppendLine("reference the pre-computed analysis above. SQL cannot easily compute these.");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Appends pre-computed aggregate statistics (per-category breakdowns) to the prompt.
    /// This allows the LLM to answer filtered aggregate questions without SQL.
    /// </summary>
    private static void AppendAggregateStatsContext(StringBuilder sb, DataProfile profile)
    {
        if (profile.AggregateStats.Count == 0) return;
        
        sb.AppendLine();
        sb.AppendLine("PRE-COMPUTED AGGREGATES (use these for per-category questions):");
        
        // Group by measure column for cleaner output
        var byMeasure = profile.AggregateStats
            .Where(a => a.AggregateFunction == "AVG") // Focus on averages first
            .GroupBy(a => a.MeasureColumn)
            .Take(5);
        
        foreach (var measureGroup in byMeasure)
        {
            var measure = measureGroup.Key;
            sb.AppendLine($"  {measure}:");
            
            foreach (var stat in measureGroup.Take(3)) // Top 3 groupings per measure
            {
                var topResults = stat.Results
                    .OrderByDescending(r => r.Value)
                    .Take(5)
                    .Select(r => $"{r.Key}={r.Value:F2}");
                sb.AppendLine($"    by {stat.GroupByColumn}: {string.Join(", ", topResults)}");
            }
        }
        
        // Also show counts if available
        var countStats = profile.AggregateStats
            .Where(a => a.AggregateFunction == "COUNT" && a.MeasureColumn == "*")
            .Take(3);
        
        if (countStats.Any())
        {
            sb.AppendLine("  ROW COUNTS:");
            foreach (var stat in countStats)
            {
                var topResults = stat.Results
                    .OrderByDescending(r => r.Value)
                    .Take(5)
                    .Select(r => $"{r.Key}={r.Value:N0}");
                sb.AppendLine($"    by {stat.GroupByColumn}: {string.Join(", ", topResults)}");
            }
        }
        
        sb.AppendLine();
    }

    private async Task<DataInsight?> GenerateAndExecuteInsightAsync(
        string readExpr, 
        DataProfile profile, 
        string question)
    {
        // Generate SQL using profile as grounding
        var sql = await GenerateSqlAsync(readExpr, profile, question);
        
        if (string.IsNullOrWhiteSpace(sql))
            return null;

        if (_verbose)
        {
            Console.WriteLine($"[LLM] Q: {question}");
            Console.WriteLine($"[LLM] SQL: {sql}");
        }

        // Validate SQL
        if (!await ValidateSqlAsync(sql))
        {
            // Retry once with error feedback
            sql = await GenerateSqlAsync(readExpr, profile, question, "Previous SQL was invalid");
            if (string.IsNullOrWhiteSpace(sql) || !await ValidateSqlAsync(sql))
                return null;
        }

        // Execute and format result
        var result = await ExecuteQueryAsync(sql);
        
        if (result == null)
            return null;

        // Generate natural language summary of result
        var summary = await SummarizeResultAsync(question, result);
        
        var relatedColumns = ExtractColumnNames(sql, profile);

        // ENRICHMENT: Profile the result and cache for future queries
        var resultProfiler = new QueryResultProfiler(_verbose);
        var cachedResult = resultProfiler.ProfileQueryResult(
            question, sql, summary, result, relatedColumns);
        
        // Notify callback to enrich the profile
        if (cachedResult.DerivedStats != null)
        {
            OnQueryCached?.Invoke(profile, cachedResult);
            
            if (_verbose)
            {
                var numericCount = cachedResult.DerivedStats.NumericStats.Count;
                var catCount = cachedResult.DerivedStats.CategoryDistributions.Count;
                Console.WriteLine($"[Enrichment] Cached stats: {numericCount} numeric, {catCount} categorical columns");
                if (cachedResult.DerivedStats.DetectedPatterns?.Count > 0)
                {
                    Console.WriteLine($"[Enrichment] Patterns: {string.Join("; ", cachedResult.DerivedStats.DetectedPatterns)}");
                }
            }
        }

        return new DataInsight
        {
            Title = TruncateQuestion(question),
            Description = summary,
            Sql = sql,
            Result = result,
            Source = InsightSource.LlmGenerated,
            RelatedColumns = relatedColumns
        };
    }

    // Use "__DATA__" - less likely to be mangled by LLM than brackets
    private const string TablePlaceholder = "__DATA__";
    
    // Guardrails: SQL operations that are NOT allowed
    private static readonly string[] ForbiddenSqlPatterns = new[]
    {
        "DELETE", "DROP", "TRUNCATE", "UPDATE", "INSERT", "ALTER", "CREATE TABLE",
        "EXEC", "EXECUTE", "GRANT", "REVOKE", "--", "/*", "*/", ";"
    };
    
    private async Task<string> GenerateSqlAsync(
        string readExpr, 
        DataProfile profile, 
        string question,
        string? errorHint = null)
    {
        var prompt = BuildSqlGenerationPrompt(profile, question, errorHint);
        
        var request = new GenerateRequest { Model = _model, Prompt = prompt };
        var response = await _ollama.GenerateAsync(request).StreamToEndAsync();
        
        var sql = CleanSqlResponse(response?.Response ?? "");
        
        // GUARDRAIL: Check for forbidden operations
        var guardrailViolation = CheckSqlGuardrails(sql);
        if (guardrailViolation != null)
        {
            if (_verbose) Console.WriteLine($"[Guardrail] Blocked: {guardrailViolation}");
            return ""; // Return empty to trigger "could not answer" flow
        }
        
        // Post-process: Replace placeholder with actual table expression
        // Handle both with and without quotes/brackets since LLM might mangle it
        sql = sql.Replace(TablePlaceholder, readExpr, StringComparison.OrdinalIgnoreCase);
        sql = sql.Replace("__data__", readExpr, StringComparison.OrdinalIgnoreCase);
        sql = sql.Replace("[__DATA__]", readExpr, StringComparison.OrdinalIgnoreCase);
        sql = sql.Replace("\"__DATA__\"", readExpr, StringComparison.OrdinalIgnoreCase);
        // Also catch if LLM uses TABLE literally
        sql = System.Text.RegularExpressions.Regex.Replace(
            sql, 
            @"\bFROM\s+TABLE\b", 
            $"FROM {readExpr}", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Post-process: Ensure column names are properly quoted
        sql = EnsureColumnQuoting(sql, profile);
        
        return sql;
    }
    
    /// <summary>
    /// Check SQL against guardrails. Returns violation message or null if OK.
    /// </summary>
    private static string? CheckSqlGuardrails(string sql)
    {
        var upper = sql.ToUpperInvariant();
        
        foreach (var forbidden in ForbiddenSqlPatterns)
        {
            // Check for forbidden keywords at word boundaries
            var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(forbidden)}\b";
            if (System.Text.RegularExpressions.Regex.IsMatch(upper, pattern))
            {
                return $"Forbidden operation: {forbidden}";
            }
        }
        
        // Must start with SELECT (read-only)
        if (!upper.TrimStart().StartsWith("SELECT"))
        {
            return "Only SELECT queries are allowed";
        }
        
        return null;
    }
    
    /// <summary>
    /// Ensure column names are properly quoted with double quotes.
    /// This is deterministic - we know the column names from the profile.
    /// </summary>
    private static string EnsureColumnQuoting(string sql, DataProfile profile)
    {
        foreach (var col in profile.Columns)
        {
            var name = col.Name;
            
            // Skip if already quoted
            if (sql.Contains($"\"{name}\"", StringComparison.OrdinalIgnoreCase))
                continue;
            
            // Replace unquoted column references with quoted ones
            // Match word boundaries to avoid partial matches
            var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b";
            sql = System.Text.RegularExpressions.Regex.Replace(
                sql, 
                pattern, 
                $"\"{name}\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        
        return sql;
    }

    private string BuildSqlGenerationPrompt(
        DataProfile profile, 
        string question,
        string? errorHint)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("Generate a DuckDB SQL query to answer the question below.");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine($"1. Use {TablePlaceholder} as the table name (we replace it automatically)");
        sb.AppendLine("2. Use SINGLE quotes for string values: WHERE \"Category\" = 'Electronics'");
        sb.AppendLine("3. DuckDB syntax: LIMIT not TOP, || for concat, ILIKE for case-insensitive");
        sb.AppendLine("4. Return ONLY the SQL query - no markdown, no explanation");
        sb.AppendLine("5. ALWAYS include aggregates in SELECT with GROUP BY");
        sb.AppendLine();
        sb.AppendLine("PRE-COMPUTED STATS (use these values directly):");
        
        // Provide pre-computed stats that can be used directly in SQL
        foreach (var col in profile.Columns.Where(c => c.InferredType == ColumnType.Numeric && c.Mean.HasValue))
        {
            sb.AppendLine($"  \"{col.Name}\": mean={col.Mean:F4}, median={col.Median:F4}, min={col.Min:F4}, max={col.Max:F4}");
        }
        
        sb.AppendLine();
        sb.AppendLine("TIPS FOR COMMON QUESTIONS:");
        sb.AppendLine("- 'most average' or 'closest to median': ORDER BY ABS(\"Col\" - <median_value>) ASC LIMIT 1");
        sb.AppendLine("- 'above/below average': WHERE \"Col\" > <mean_value> or WHERE \"Col\" < <mean_value>");
        sb.AppendLine("- 'outliers': WHERE \"Col\" < <Q1 - 1.5*IQR> OR \"Col\" > <Q3 + 1.5*IQR>");
        sb.AppendLine();
        
        // Add pre-computed advanced analysis tools section
        AppendAdvancedAnalyticsContext(sb, profile);
        
        // Add pre-computed aggregate statistics (per-category breakdowns)
        AppendAggregateStatsContext(sb, profile);
        
        // Add available analytics tools
        sb.Append(_toolRegistry.FormatToolsForPrompt(profile));
        
        sb.AppendLine($"TABLE: {TablePlaceholder}");
        sb.AppendLine($"ROWS: {profile.RowCount:N0}");
        sb.AppendLine();
        
        // Build rich schema with data context
        sb.AppendLine("COLUMNS:");
        foreach (var col in profile.Columns)
        {
            sb.Append($"  \"{col.Name}\" ({col.InferredType})");
            
            // Add rich context based on column type
            switch (col.InferredType)
            {
                case ColumnType.Categorical:
                    if (col.TopValues?.Count > 0)
                    {
                        var topVals = col.TopValues.Take(5)
                            .Select(v => $"'{v.Value}'")
                            .ToList();
                        sb.Append($" [values: {string.Join(", ", topVals)}]");
                    }
                    break;
                    
                case ColumnType.DateTime:
                    if (col.MinDate.HasValue && col.MaxDate.HasValue)
                    {
                        sb.Append($" [range: {col.MinDate:yyyy-MM-dd} to {col.MaxDate:yyyy-MM-dd}]");
                    }
                    break;
                    
                case ColumnType.Boolean:
                    if (col.TopValues?.Count > 0)
                    {
                        var dist = string.Join(", ", col.TopValues.Select(v => $"{v.Value}={v.Percent:F0}%"));
                        sb.Append($" [{dist}]");
                    }
                    break;
            }
            
            sb.AppendLine();
        }

        if (errorHint != null)
        {
            sb.AppendLine();
            sb.AppendLine($"ERROR FROM PREVIOUS ATTEMPT: {errorHint}");
            sb.AppendLine("Fix the SQL based on this error.");
        }

        sb.AppendLine();
        sb.AppendLine($"QUESTION: {question}");
        sb.AppendLine();
        sb.AppendLine("SQL:");

        return sb.ToString();
    }

    private async Task<bool> ValidateSqlAsync(string sql)
    {
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = $"EXPLAIN {sql}";
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"[SQL Validation Error] {ex.Message}");
            return false;
        }
    }

    private async Task<object?> ExecuteQueryAsync(string sql)
    {
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync();
            
            var columns = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync() && rows.Count < 20)
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }

            return new { columns, rows, rowCount = rows.Count };
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"[SQL Error] {ex.Message}");
            return null;
        }
    }

    private async Task<string> SummarizeResultAsync(string question, object result)
    {
        var resultJson = JsonSerializer.Serialize(result, new JsonSerializerOptions 
        { 
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        // Truncate if too long
        if (resultJson.Length > 2000)
        {
            resultJson = resultJson[..2000] + "...";
        }

        var prompt = $"""
            Summarize this query result in 1-2 sentences. Be specific with numbers.
            
            Question: {question}
            Result: {resultJson}
            
            Summary (1-2 sentences, include key numbers):
            """;

        var request = new GenerateRequest { Model = _model, Prompt = prompt };
        var response = await _ollama.GenerateAsync(request).StreamToEndAsync();
        
        return (response?.Response ?? "").Trim();
    }

    private static string CleanSqlResponse(string response)
    {
        var sql = response.Trim();

        // Remove markdown code blocks
        if (sql.StartsWith("```"))
        {
            var lines = sql.Split('\n').ToList();
            lines.RemoveAt(0);
            if (lines.Count > 0 && lines[^1].Trim().StartsWith("```"))
            {
                lines.RemoveAt(lines.Count - 1);
            }
            sql = string.Join('\n', lines);
        }

        return sql.Trim('`', ' ', '\n', '\r');
    }

    private static string TruncateQuestion(string question)
    {
        if (question.Length <= 60) return question;
        return question[..57] + "...";
    }

    private static List<string> ExtractColumnNames(string sql, DataProfile profile)
    {
        var colNames = profile.Columns.Select(c => c.Name).ToList();
        return colNames.Where(c => sql.Contains($"\"{c}\"", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string GetReadExpression(string filePath, string? sheetName)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var escaped = filePath.Replace("'", "''").Replace("\\", "/");
        
        return ext switch
        {
            ".xlsx" or ".xls" when sheetName != null => 
                $"read_xlsx('{escaped}', sheet = '{sheetName}', header = true)",
            ".xlsx" or ".xls" => 
                $"read_xlsx('{escaped}', header = true)",
            ".parquet" => 
                $"read_parquet('{escaped}')",
            ".json" => 
                $"read_json_auto('{escaped}')",
            _ => 
                $"read_csv_auto('{escaped}')"
        };
    }

    /// <summary>
    /// Create a view 't' that wraps the read expression.
    /// This allows the LLM to use simple 'FROM t' syntax in generated SQL.
    /// </summary>
    private async Task CreateTableViewAsync(string readExpr)
    {
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = $"CREATE OR REPLACE VIEW t AS SELECT * FROM {readExpr}";
            await cmd.ExecuteNonQueryAsync();
            
            if (_verbose)
            {
                Console.WriteLine($"[SQL] Created view 't' from {readExpr}");
            }
        }
        catch (Exception ex)
        {
            if (_verbose)
            {
                Console.WriteLine($"[SQL] Failed to create view: {ex.Message}");
            }
            throw;
        }
    }

    /// <summary>
    /// Use LLM to analyze a novel pattern and generate a better regex and description
    /// </summary>
    public async Task<NovelPatternAnalysis?> AnalyzeNovelPatternAsync(string columnName, List<string> examples, string? basicRegex)
    {
        if (examples.Count == 0) return null;

        var examplesText = string.Join("\n", examples.Take(10).Select(e => $"- {e}"));
        var jsonSchema = """{"pattern_name": "short name", "description": "description", "regex": "regex pattern", "is_identifier": true/false, "is_sensitive": true/false, "validation_rules": ["rule1"]}""";
        var prompt = $"""
            Analyze these example values from a data column named "{columnName}":
            
            Examples:
            {examplesText}
            
            Basic detected pattern: {basicRegex ?? "unknown"}
            
            Analyze the pattern and respond with valid JSON matching this schema:
            {jsonSchema}
            
            Respond ONLY with the JSON, no markdown or explanation:
            """;

        try
        {
            var request = new GenerateRequest { Model = _model, Prompt = prompt };
            var response = await _ollama.GenerateAsync(request).StreamToEndAsync();
            var text = (response?.Response ?? "").Trim();

            var json = ExtractJsonBlock(text);
            if (json == null) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new NovelPatternAnalysis
            {
                PatternName = root.TryGetProperty("pattern_name", out var pn) ? pn.GetString() ?? "Unknown" : "Unknown",
                Description = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                ImprovedRegex = root.TryGetProperty("regex", out var rx) ? rx.GetString() : basicRegex,
                IsIdentifier = root.TryGetProperty("is_identifier", out var isId) && isId.GetBoolean(),
                IsSensitive = root.TryGetProperty("is_sensitive", out var isSens) && isSens.GetBoolean(),
                ValidationRules = root.TryGetProperty("validation_rules", out var rules) && rules.ValueKind == JsonValueKind.Array
                    ? rules.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => !string.IsNullOrEmpty(r)).ToList()
                    : new List<string>()
            };
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"[LLM] Novel pattern analysis failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Generate synthetic examples for a novel pattern using LLM
    /// </summary>
    public async Task<List<string>> GeneratePatternExamplesAsync(string patternDescription, string? regex, int count = 10)
    {
        var prompt = $"""
            Generate {count} realistic example values that match this pattern:
            
            Pattern: {patternDescription}
            {(regex != null ? $"Regex: {regex}" : "")}
            
            Return ONLY the examples, one per line, no numbering or explanations:
            """;

        try
        {
            var request = new GenerateRequest { Model = _model, Prompt = prompt };
            var response = await _ollama.GenerateAsync(request).StreamToEndAsync();
            var text = (response?.Response ?? "").Trim();

            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().TrimStart('-', '*', ' '))
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length < 200)
                .Take(count)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}

/// <summary>
/// Result of LLM analysis of a novel pattern
/// </summary>
public class NovelPatternAnalysis
{
    public string PatternName { get; set; } = "";
    public string Description { get; set; } = "";
    public string? ImprovedRegex { get; set; }
    public bool IsIdentifier { get; set; }
    public bool IsSensitive { get; set; }
    public List<string> ValidationRules { get; set; } = new();
}
