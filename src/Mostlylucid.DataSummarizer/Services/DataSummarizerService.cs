using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Markdig;
using Mostlylucid.DataSummarizer.Configuration;
using Mostlylucid.DataSummarizer.Models;
using OllamaSharp;
using OllamaSharp.Models;


namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Main orchestrator for data summarization.
/// Combines statistical profiling with optional LLM insights.
/// </summary>
public class DataSummarizerService : IDisposable
{
    private readonly bool _verbose;
    private readonly string? _ollamaModel;
    private readonly string _ollamaUrl;
    private readonly string? _onnxSentinelPath;
    private readonly bool _enableClarifierSentinel;
    private readonly string _clarifierSentinelModel;

    private readonly OnnxConfig? _onnxConfig;
    private readonly string? _vectorStorePath;
    private readonly string _sessionId;
    private readonly ProfileOptions _profileOptions;
    private readonly ReportOptions _reportOptions;
    private VectorStoreService? _vectorStore;
    private bool _vectorStoreInitialized;


    public DataSummarizerService(
        bool verbose = false,
        string? ollamaModel = null,
        string ollamaUrl = "http://localhost:11434",
        string? onnxSentinelPath = null,
        OnnxConfig? onnxConfig = null,
        string? vectorStorePath = null,
        string? sessionId = null,
        ProfileOptions? profileOptions = null,
        ReportOptions? reportOptions = null,
        bool enableClarifierSentinel = true,
        string clarifierSentinelModel = "qwen2.5:1.5b")
    {
        _verbose = verbose;
        _ollamaModel = ollamaModel;
        _ollamaUrl = ollamaUrl;
        _onnxSentinelPath = onnxSentinelPath;
        _onnxConfig = onnxConfig;
        _vectorStorePath = vectorStorePath;
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
        _profileOptions = profileOptions ?? new ProfileOptions();
        _reportOptions = reportOptions ?? new ReportOptions();
        _enableClarifierSentinel = enableClarifierSentinel;
        _clarifierSentinelModel = clarifierSentinelModel;
    }

    /// <summary>
    /// Report status via callback (compatible with Spectre.Console spinners)
    /// </summary>
    private void Status(string message)
    {
        _profileOptions.OnStatusUpdate?.Invoke(message);
    }


    /// <summary>
    /// Summarize a data file (CSV, Excel, Parquet, JSON)
    /// </summary>
    public async Task<DataSummaryReport> SummarizeAsync(
        string filePath,
        string? sheetName = null,
        bool useLlm = true,
        int maxLlmInsights = 5)
    {
        var profile = await ProfileWithExtrasAsync(filePath, sheetName, useLlm, maxLlmInsights);
        var report = await GenerateReportAsync(profile, useLlm && _reportOptions.UseLlm);
        return report;
    }


    private async Task AppendTurnAsync(string role, string content)
    {
        await EnsureVectorStoreAsync();
        if (_vectorStore is null || !_vectorStore.IsAvailable) return;
        await _vectorStore.AppendConversationTurnAsync(_sessionId, role, content);
    }

    /// <summary>
    /// Ingest multiple files into the registry (vector store) without running LLMs.
    /// </summary>
    public async Task IngestAsync(IEnumerable<string> filePaths, int maxLlmInsights = 0)
    {
        foreach (var path in filePaths.Distinct())
        {
            try
            {
                await ProfileWithExtrasAsync(path, sheetName: null, useLlm: maxLlmInsights > 0 && !string.IsNullOrEmpty(_ollamaModel), maxLlmInsights: maxLlmInsights);
            }
            catch
            {
                Status($"Ingest failed: {path}");
            }
        }
    }

    /// <summary>
    /// Ask a specific question about a data file.
    /// Will first attempt to answer from the profile without LLM, falling back to LLM if needed.
    /// </summary>
    public async Task<DataInsight?> AskAsync(string filePath, string question, string? sheetName = null)
    {
        var profile = await ProfileWithExtrasAsync(filePath, sheetName, useLlm: false, maxLlmInsights: 0);

        // Decide if we should skip precomputed/profile shortcuts (follow-ups / most-average, etc.)
        // Build context early so the intent probe can use it
        var context = await GetConversationContextInternalAsync(question);
        var contextText = context.Count > 0 ? string.Join('\n', context.Select(t => $"[{t.Role}] {t.Content}")) : "";
        bool skipPrecomputed = await ShouldSkipPrecomputedStatsAsync(question, contextText, profile);

        // Try to answer from profile first (no LLM needed) unless we must skip
        if (!skipPrecomputed)
        {
            var profileAnswer = TryAnswerFromProfile(profile, question);
            if (profileAnswer != null)
            {
                await AppendTurnAsync("user", question);
                await AppendTurnAsync("assistant", profileAnswer.Description);
                return profileAnswer;
            }
        }

        // Fall back to LLM if available
        if (string.IsNullOrEmpty(_ollamaModel))
        {
            // No LLM and couldn't answer from profile - return a helpful message
            return new DataInsight
            {
                Title = "Cannot answer without LLM",
                Description = $"This question requires LLM analysis. Profile summary: {profile.RowCount:N0} rows, {profile.ColumnCount} columns. " +
                             $"Try asking about: missing values, outliers, correlations, distributions, schema, or target analysis.",
                Source = InsightSource.Statistical,
                RelatedColumns = profile.Columns.Take(5).Select(c => c.Name).ToList()
            };
        }

        using var llm = new LlmInsightGenerator(_ollamaModel ?? "qwen2.5-coder:7b", _ollamaUrl, _verbose, _enableClarifierSentinel, _clarifierSentinelModel);
        var insight = await llm.AskAsync(filePath, profile, question, contextText, skipPrecomputed);



        if (insight != null)
        {
            await AppendTurnAsync("user", question);
            await AppendTurnAsync("assistant", insight.Description);
        }

        return insight;
    }

    private async Task<bool> ShouldSkipPrecomputedStatsAsync(string question, string conversationContext, DataProfile profile)
    {
        var q = question.ToLowerInvariant();
        
        // Heuristic: follow-up pronouns or "most average" style require SQL/LLM
        var followUpTriggers = new[]
        {
            "tell me about it","tell me more about it","what about it","describe it","show me it","more about it","details about it","info about it","information about it","what information do we have about it","what do we know about it","what is it","what's it","its details","its name","its price","its info",
            "tell me about that","what is that","what's that","describe that","more about that","info about that","information about that",
            "tell me about this","what is this","what's this","describe this","more about this","info about this","information about this"
        };
        if (followUpTriggers.Any(t => q.Contains(t)))
            return true;

        // Generic pronoun + about it/that/this catch-all
        if (q.Contains("about it") || q.Contains("about that") || q.Contains("about this"))
            return true;
        
        // Most-average / typical questions need SQL
        if (q.Contains("most average") || q.Contains("closest to average") || q.Contains("closest to mean") || q.Contains("closest to median") || q.Contains("nearest to average") || q.Contains("typical") || q.Contains("mose average"))
            return true;
        
        // If LLM available, run a tiny intent probe using prior context
        if (!string.IsNullOrEmpty(_ollamaModel) && !string.IsNullOrWhiteSpace(conversationContext))
        {
            try
            {
                var probePrompt = $"""
Return only one token: follow_up or not_follow_up.
Question: {question}
Prior conversation:
{conversationContext}
""";
                var client = new OllamaSharp.OllamaApiClient(new Uri(_ollamaUrl));
                var resp = await client.GenerateAsync(new OllamaSharp.Models.GenerateRequest
                {
                    Model = _ollamaModel,
                    Prompt = probePrompt
                }).StreamToEndAsync();
                var text = (resp?.Response ?? "").ToLowerInvariant();
                if (text.Contains("follow_up")) return true;
            }
            catch
            {
                // fall back to heuristics on failure
            }
        }
        
        return false;
    }

    private DataInsight? TryBuildAverageClarifier(DataProfile profile, string q)
    {
        if (!(q.Contains("average") || q.Contains("mean"))) return null;
        if (q.Contains("most average") || q.Contains("closest")) return null;

        var mentionsColumn = profile.Columns.Any(c => q.Contains(c.Name.ToLowerInvariant()));
        if (mentionsColumn) return null;

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
        options.Add("overall (no grouping)");

        // Pick top categorical columns by distinct count (reasonable size) and coverage
        var candidates = profile.Columns
            .Where(c => c.InferredType == ColumnType.Categorical && c.UniqueCount > 1 && c.UniqueCount <= 2000 && c.NullPercent < 80)
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

        return options;
    }

    /// <summary>
    /// Attempts to answer common data questions directly from the profile without requiring LLM.
    /// Returns null if the question cannot be answered from the profile alone.
    /// </summary>
    public DataInsight? TryAnswerFromProfile(DataProfile profile, string question)

    {
        var q = question.ToLowerInvariant();
        
        // EARLY EXIT: Questions asking about specific entities/records should use SQL, not profile stats
        // These are questions that want to find specific rows, not understand the data structure
        if (IsEntityQuery(q))
        {
            return null; // Let LLM handle with SQL
        }

        // Ambiguous average: prompt for dimension instead of dumping global stats
        var clarifier = TryBuildAverageClarifier(profile, q);
        if (clarifier != null)
        {
            return clarifier;
        }
        
        // Missing values / nulls
        if (ContainsAny(q, "missing", "null", "nulls", "empty", "na ", "n/a"))
        {
            return AnswerMissingValues(profile);
        }
        
        // Outliers
        if (ContainsAny(q, "outlier", "outliers", "anomal", "extreme"))
        {
            return AnswerOutliers(profile);
        }
        
        // Schema / columns / structure
        if (ContainsAny(q, "schema", "column", "columns", "structure", "fields", "what columns", "list columns"))
        {
            return AnswerSchema(profile);
        }
        
        // Correlations
        if (ContainsAny(q, "correlat", "relationship", "related"))
        {
            return AnswerCorrelations(profile);
        }
        
        // Distributions
        if (ContainsAny(q, "distribution", "distributed", "skew", "normal", "bimodal"))
        {
            return AnswerDistributions(profile);
        }
        
        // Target / churn / prediction
        if (ContainsAny(q, "target", "predict", "churn", "driver", "feature importance", "impact"))
        {
            return AnswerTargetAnalysis(profile);
        }
        
        // Summary / overview
        if (ContainsAny(q, "summary", "overview", "describe", "tell me about", "what is this"))
        {
            return AnswerSummary(profile);
        }
        
        // Data quality / alerts / issues
        if (ContainsAny(q, "quality", "issue", "problem", "alert", "warning", "error"))
        {
            return AnswerDataQuality(profile);
        }
        
        // EARLY FILTER CHECK: If question has filter indicators (for X, in Y, where Z),
        // it needs SQL, not pre-computed stats - let it fall through to LLM
        if (HasFilterIndicator(q, profile))
        {
            return null; // Need SQL to filter
        }
        
        // Categorical / categories / values (only if NOT filtering by a category value)
        if (ContainsAny(q, "categor", "unique values", "distinct", "top values", "most common"))
        {
            // But "what categories" is different from "average for Electronics category"
            // Check if we're asking ABOUT categories vs asking FOR a specific category
            if (!ContainsAny(q, "average", "mean", "sum", "total", "count of", "how many"))
            {
                return AnswerCategorical(profile);
            }
        }
        
        // Numeric stats (only for pure statistical questions about the dataset, not specific entities)
        if (ContainsAny(q, "mean", "median", "std", "standard deviation", "min", "max", "range") ||
            (ContainsAny(q, "average") && ContainsAny(q, "what is the average", "show me the average", "calculate average")))
        {
            return AnswerNumericStats(profile);
        }
        
        // Time series / dates / trends
        if (ContainsAny(q, "time series", "trend", "seasonal", "date range", "temporal"))
        {
            return AnswerTimeSeries(profile);
        }
        
        // Patterns
        if (ContainsAny(q, "pattern", "format", "email", "phone", "url", "uuid"))
        {
            return AnswerPatterns(profile);
        }

        return null;
    }
    
    /// <summary>
    /// Detects if a question is asking about specific entities/records rather than dataset metadata.
    /// These questions need SQL to answer, not just profile statistics.
    /// </summary>
    private static bool IsEntityQuery(string q)
    {
        // Questions asking for specific records by superlative
        var superlatives = new[] { 
            "best", "worst", "most", "least", "top", "bottom", "highest", "lowest", 
            "oldest", "newest", "largest", "smallest", "longest", "shortest",
            "cheapest", "expensive", "popular", "unpopular", "rated"
        };
        
        // Entity nouns that indicate we're looking for specific records (not metadata)
        var entities = new[] {
            "movie", "film", "director", "actor", "product", "customer", "user", "person",
            "book", "author", "song", "artist", "album", "game", "company", "employee",
            "item", "record", "entry", "row", "one", "ones"
        };
        
        // Question words that ask for specific items
        var questionWords = new[] { "which", "who", "what is the", "what are the", "what's the" };
        
        // Check for superlative + entity pattern (e.g., "best movie", "oldest director")
        if (superlatives.Any(s => q.Contains(s)) && entities.Any(e => q.Contains(e)))
        {
            return true;
        }
        
        // Check for question word + entity (e.g., "which movie", "who is the director")
        if (questionWords.Any(w => q.Contains(w)) && entities.Any(e => q.Contains(e)))
        {
            return true;
        }
        
        // Questions that explicitly ask for specific items WITH entity nouns
        var listCommands = new[] { "show me the", "list the", "find the", "give me the", "tell me the", "name the", "identify the" };
        if (listCommands.Any(c => q.Contains(c)) && entities.Any(e => q.Contains(e)))
        {
            return true;
        }
        
        // "most average" is asking for a specific entity, not stats
        if (q.Contains("most average"))
        {
            return true;
        }
        
        // Questions with "based on" + entity typically need SQL to filter/aggregate
        if (q.Contains("based on") && entities.Any(e => q.Contains(e)))
        {
            return true;
        }
        
        // Superlative at start often means looking for specific records
        // e.g., "oldest?", "best rated?", "top 5?"
        if (superlatives.Any(s => q.StartsWith(s)) || q.StartsWith("top "))
        {
            return true;
        }
        
        return false;
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        return keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Check if the question contains filter indicators that require SQL.
    /// E.g., "for Electronics", "in North region", "where price > 100"
    /// </summary>
    private static bool HasFilterIndicator(string q, DataProfile profile)
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
                foreach (var val in col.TopValues!.Take(15))
                {
                    if (q.Contains(val.Value.ToLowerInvariant()))
                    {
                        return true; // Filtering by a specific category value
                    }
                }
            }
            
            // Also check column names themselves for "by Region", "per Category" patterns
            foreach (var col in profile.Columns.Where(c => c.InferredType == ColumnType.Categorical))
            {
                var colLower = col.Name.ToLowerInvariant();
                if (q.Contains($"by {colLower}") || q.Contains($"per {colLower}") || 
                    q.Contains($"each {colLower}") || q.Contains($"in {colLower}"))
                {
                    return true;
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

    private DataInsight AnswerMissingValues(DataProfile profile)
    {
        var columnsWithNulls = profile.Columns
            .Where(c => c.NullPercent > 0)
            .OrderByDescending(c => c.NullPercent)
            .ToList();

        if (columnsWithNulls.Count == 0)
        {
            return new DataInsight
            {
                Title = "No Missing Values",
                Description = "All columns have complete data with no missing values.",
                Source = InsightSource.Statistical,
                Score = 0.9
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {columnsWithNulls.Count} column(s) with missing values:");
        sb.AppendLine();
        
        foreach (var col in columnsWithNulls.Take(10))
        {
            sb.AppendLine($"- **{col.Name}**: {col.NullPercent:F1}% missing ({col.NullCount:N0} of {col.Count:N0} rows)");
        }
        
        if (columnsWithNulls.Count > 10)
        {
            sb.AppendLine($"- ... and {columnsWithNulls.Count - 10} more columns");
        }

        var highNullCols = columnsWithNulls.Where(c => c.NullPercent > 50).ToList();
        if (highNullCols.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"⚠️ {highNullCols.Count} column(s) have >50% missing data and may need imputation or exclusion.");
        }

        return new DataInsight
        {
            Title = "Missing Values Analysis",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            RelatedColumns = columnsWithNulls.Take(5).Select(c => c.Name).ToList(),
            Score = 0.85
        };
    }

    private DataInsight AnswerOutliers(DataProfile profile)
    {
        var columnsWithOutliers = profile.Columns
            .Where(c => c.OutlierCount > 0)
            .OrderByDescending(c => c.OutlierCount)
            .ToList();

        if (columnsWithOutliers.Count == 0)
        {
            return new DataInsight
            {
                Title = "No Outliers Detected",
                Description = "No significant outliers were detected in numeric columns using the IQR method (values outside Q1-1.5*IQR to Q3+1.5*IQR).",
                Source = InsightSource.Statistical,
                Score = 0.8
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found outliers in {columnsWithOutliers.Count} column(s):");
        sb.AppendLine();

        foreach (var col in columnsWithOutliers.Take(10))
        {
            var outlierPct = col.Count > 0 ? col.OutlierCount * 100.0 / col.Count : 0;
            var iqr = (col.Q75 ?? 0) - (col.Q25 ?? 0);
            var lowerBound = (col.Q25 ?? 0) - 1.5 * iqr;
            var upperBound = (col.Q75 ?? 0) + 1.5 * iqr;
            
            sb.AppendLine($"- **{col.Name}**: {col.OutlierCount:N0} outliers ({outlierPct:F1}%)");
            sb.AppendLine($"  - Valid range: [{lowerBound:F1}, {upperBound:F1}]");
            sb.AppendLine($"  - Actual range: [{col.Min:F1}, {col.Max:F1}]");
        }

        return new DataInsight
        {
            Title = "Outlier Analysis",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            RelatedColumns = columnsWithOutliers.Take(5).Select(c => c.Name).ToList(),
            Score = 0.85
        };
    }

    private DataInsight AnswerSchema(DataProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Dataset has **{profile.RowCount:N0} rows** and **{profile.ColumnCount} columns**.");
        sb.AppendLine();
        sb.AppendLine("| Column | Type | Role | Nulls |");
        sb.AppendLine("|--------|------|------|-------|");
        
        foreach (var col in profile.Columns)
        {
            var role = col.SemanticRole != SemanticRole.Unknown ? col.SemanticRole.ToString() : "-";
            sb.AppendLine($"| {col.Name} | {col.InferredType} | {role} | {col.NullPercent:F1}% |");
        }

        // Type summary
        var typeGroups = profile.Columns.GroupBy(c => c.InferredType).OrderByDescending(g => g.Count());
        sb.AppendLine();
        sb.AppendLine("**Type breakdown:**");
        foreach (var g in typeGroups)
        {
            sb.AppendLine($"- {g.Key}: {g.Count()} columns");
        }

        return new DataInsight
        {
            Title = "Schema Overview",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            RelatedColumns = profile.Columns.Select(c => c.Name).ToList(),
            Score = 0.9
        };
    }

    private DataInsight AnswerCorrelations(DataProfile profile)
    {
        if (profile.Correlations.Count == 0)
        {
            return new DataInsight
            {
                Title = "No Correlations Analyzed",
                Description = "No correlations were computed. This may be because there are fewer than 2 numeric columns, or correlation analysis was skipped.",
                Source = InsightSource.Statistical,
                Score = 0.6
            };
        }

        var strongCorrs = profile.Correlations.Where(c => Math.Abs(c.Correlation) >= 0.5).ToList();
        var sb = new StringBuilder();

        if (strongCorrs.Count > 0)
        {
            sb.AppendLine($"Found **{strongCorrs.Count} notable correlation(s)** (|r| ≥ 0.5):");
            sb.AppendLine();
            
            foreach (var corr in strongCorrs.OrderByDescending(c => Math.Abs(c.Correlation)).Take(10))
            {
                var direction = corr.Correlation > 0 ? "positive" : "negative";
                sb.AppendLine($"- **{corr.Column1}** ↔ **{corr.Column2}**: r = {corr.Correlation:F3} ({corr.Strength} {direction})");
            }
        }
        else
        {
            sb.AppendLine("No strong correlations found (|r| ≥ 0.5).");
            sb.AppendLine();
            sb.AppendLine("Top correlations:");
            foreach (var corr in profile.Correlations.OrderByDescending(c => Math.Abs(c.Correlation)).Take(5))
            {
                sb.AppendLine($"- {corr.Column1} ↔ {corr.Column2}: r = {corr.Correlation:F3} ({corr.Strength})");
            }
        }

        return new DataInsight
        {
            Title = "Correlation Analysis",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            RelatedColumns = strongCorrs.Take(3).SelectMany(c => new[] { c.Column1, c.Column2 }).Distinct().ToList(),
            Score = 0.85
        };
    }

    private DataInsight AnswerDistributions(DataProfile profile)
    {
        var numericCols = profile.Columns.Where(c => c.InferredType == ColumnType.Numeric && c.Distribution.HasValue).ToList();

        if (numericCols.Count == 0)
        {
            return new DataInsight
            {
                Title = "No Distribution Data",
                Description = "No distribution information available. This may be because there are no numeric columns or pattern detection was skipped (--fast mode).",
                Source = InsightSource.Statistical,
                Score = 0.5
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine("Distribution analysis for numeric columns:");
        sb.AppendLine();

        var byType = numericCols.GroupBy(c => c.Distribution).OrderByDescending(g => g.Count());
        foreach (var group in byType)
        {
            sb.AppendLine($"**{group.Key}**: {string.Join(", ", group.Select(c => c.Name))}");
        }

        sb.AppendLine();
        sb.AppendLine("Details:");
        foreach (var col in numericCols.Take(8))
        {
            var skewDesc = col.Skewness switch
            {
                > 1 => "right-skewed",
                < -1 => "left-skewed",
                _ => "symmetric"
            };
            sb.AppendLine($"- **{col.Name}**: {col.Distribution} (skewness: {col.Skewness:F2}, {skewDesc})");
        }

        return new DataInsight
        {
            Title = "Distribution Analysis",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            RelatedColumns = numericCols.Take(5).Select(c => c.Name).ToList(),
            Score = 0.8
        };
    }

    private DataInsight AnswerTargetAnalysis(DataProfile profile)
    {
        if (profile.Target == null)
        {
            return new DataInsight
            {
                Title = "No Target Analysis",
                Description = "No target column was specified. Use `--target <column>` to enable target-aware analysis for classification or prediction tasks.",
                Source = InsightSource.Statistical,
                Score = 0.5
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine($"**Target column**: {profile.Target.ColumnName}");
        sb.AppendLine($"**Type**: {(profile.Target.IsBinary ? "Binary classification" : "Multi-class")}");
        sb.AppendLine();
        
        sb.AppendLine("**Class distribution:**");
        foreach (var kv in profile.Target.ClassDistribution.OrderByDescending(kv => kv.Value))
        {
            sb.AppendLine($"- {kv.Key}: {kv.Value * 100:F1}%");
        }

        if (profile.Target.FeatureEffects.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Top feature drivers:**");
            foreach (var effect in profile.Target.FeatureEffects.Take(5))
            {
                sb.AppendLine($"- **{effect.Feature}** ({effect.Metric}): {effect.Summary}");
            }
        }

        // Check for imbalance
        var minorityPct = profile.Target.ClassDistribution.Min(kv => kv.Value) * 100;
        if (minorityPct < 20)
        {
            sb.AppendLine();
            sb.AppendLine($"⚠️ **Class imbalance detected**: minority class is only {minorityPct:F1}%. Consider using SMOTE, class weights, or stratified sampling.");
        }

        return new DataInsight
        {
            Title = "Target Analysis",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            RelatedColumns = new[] { profile.Target.ColumnName }.Concat(profile.Target.FeatureEffects.Take(3).Select(e => e.Feature)).ToList(),
            Score = 0.95
        };
    }

    private DataInsight AnswerSummary(DataProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Dataset**: {Path.GetFileName(profile.SourcePath)}");
        sb.AppendLine($"**Size**: {profile.RowCount:N0} rows × {profile.ColumnCount} columns");
        sb.AppendLine($"**Profile time**: {profile.ProfileTime.TotalSeconds:F1}s");
        sb.AppendLine();
        
        // Column types
        var numericCount = profile.Columns.Count(c => c.InferredType == ColumnType.Numeric);
        var categoricalCount = profile.Columns.Count(c => c.InferredType == ColumnType.Categorical);
        var dateCount = profile.Columns.Count(c => c.InferredType == ColumnType.DateTime);
        var textCount = profile.Columns.Count(c => c.InferredType == ColumnType.Text);
        
        sb.AppendLine("**Column types:**");
        if (numericCount > 0) sb.AppendLine($"- Numeric: {numericCount}");
        if (categoricalCount > 0) sb.AppendLine($"- Categorical: {categoricalCount}");
        if (dateCount > 0) sb.AppendLine($"- DateTime: {dateCount}");
        if (textCount > 0) sb.AppendLine($"- Text: {textCount}");
        
        // Data quality summary
        var nullyCols = profile.Columns.Count(c => c.NullPercent > 0);
        var highNullCols = profile.Columns.Count(c => c.NullPercent > 50);
        var outlierCols = profile.Columns.Count(c => c.OutlierCount > 0);
        
        sb.AppendLine();
        sb.AppendLine("**Data quality:**");
        sb.AppendLine($"- Columns with nulls: {nullyCols}");
        if (highNullCols > 0) sb.AppendLine($"- Columns with >50% nulls: {highNullCols}");
        sb.AppendLine($"- Columns with outliers: {outlierCols}");
        sb.AppendLine($"- Alerts: {profile.Alerts.Count}");
        
        if (profile.Correlations.Count > 0)
        {
            var strongCorrs = profile.Correlations.Count(c => Math.Abs(c.Correlation) >= 0.7);
            sb.AppendLine($"- Strong correlations (|r|≥0.7): {strongCorrs}");
        }
        
        if (profile.Target != null)
        {
            sb.AppendLine();
            sb.AppendLine($"**Target**: {profile.Target.ColumnName} ({profile.Target.FeatureEffects.Count} feature effects analyzed)");
        }

        return new DataInsight
        {
            Title = "Dataset Summary",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            Score = 0.9
        };
    }

    private DataInsight AnswerDataQuality(DataProfile profile)
    {
        if (profile.Alerts.Count == 0)
        {
            return new DataInsight
            {
                Title = "No Data Quality Issues",
                Description = "No significant data quality issues detected. The dataset appears clean.",
                Source = InsightSource.Statistical,
                Score = 0.9
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found **{profile.Alerts.Count} data quality issue(s)**:");
        sb.AppendLine();

        var byType = profile.Alerts.GroupBy(a => a.Type).OrderByDescending(g => g.Max(a => a.Severity));
        foreach (var group in byType)
        {
            sb.AppendLine($"**{group.Key}** ({group.Count()}):");
            foreach (var alert in group.Take(3))
            {
                var icon = alert.Severity switch
                {
                    AlertSeverity.Error => "🔴",
                    AlertSeverity.Warning => "🟡",
                    _ => "🔵"
                };
                sb.AppendLine($"  {icon} {alert.Column}: {alert.Message}");
            }
            if (group.Count() > 3)
            {
                sb.AppendLine($"  ... and {group.Count() - 3} more");
            }
            sb.AppendLine();
        }

        return new DataInsight
        {
            Title = "Data Quality Report",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            RelatedColumns = profile.Alerts.Select(a => a.Column).Distinct().Take(5).ToList(),
            Score = 0.85
        };
    }

    private DataInsight AnswerCategorical(DataProfile profile)
    {
        var categoricalCols = profile.Columns
            .Where(c => c.InferredType == ColumnType.Categorical && c.TopValues?.Count > 0)
            .ToList();

        if (categoricalCols.Count == 0)
        {
            return new DataInsight
            {
                Title = "No Categorical Data",
                Description = "No categorical columns found in this dataset.",
                Source = InsightSource.Statistical,
                Score = 0.5
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found **{categoricalCols.Count} categorical column(s)**:");
        sb.AppendLine();

        foreach (var col in categoricalCols.Take(8))
        {
            sb.AppendLine($"**{col.Name}** ({col.UniqueCount:N0} unique values):");
            foreach (var val in col.TopValues!.Take(5))
            {
                sb.AppendLine($"  - {val.Value}: {val.Percent:F1}% ({val.Count:N0})");
            }
            if (col.TopValues!.Count > 5)
            {
                sb.AppendLine($"  - ... and {col.UniqueCount - 5} more values");
            }
            sb.AppendLine();
        }

        return new DataInsight
        {
            Title = "Categorical Columns",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            RelatedColumns = categoricalCols.Take(5).Select(c => c.Name).ToList(),
            Score = 0.8
        };
    }

    private DataInsight AnswerNumericStats(DataProfile profile)
    {
        var numericCols = profile.Columns.Where(c => c.InferredType == ColumnType.Numeric).ToList();

        if (numericCols.Count == 0)
        {
            return new DataInsight
            {
                Title = "No Numeric Data",
                Description = "No numeric columns found in this dataset.",
                Source = InsightSource.Statistical,
                Score = 0.5
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine("| Column | Min | Max | Mean | Median | Std Dev |");
        sb.AppendLine("|--------|-----|-----|------|--------|---------|");

        foreach (var col in numericCols)
        {
            sb.AppendLine($"| {col.Name} | {col.Min:F2} | {col.Max:F2} | {col.Mean:F2} | {col.Median:F2} | {col.StdDev:F2} |");
        }

        return new DataInsight
        {
            Title = "Numeric Statistics",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            RelatedColumns = numericCols.Select(c => c.Name).ToList(),
            Score = 0.85
        };
    }

    private DataInsight AnswerTimeSeries(DataProfile profile)
    {
        var dateCols = profile.Columns.Where(c => c.InferredType == ColumnType.DateTime).ToList();
        var trends = profile.Columns.Where(c => c.Trend != null).ToList();
        var tsPatterns = profile.Patterns.Where(p => p.Type == PatternType.TimeSeries || p.Type == PatternType.Seasonality).ToList();

        if (dateCols.Count == 0)
        {
            return new DataInsight
            {
                Title = "No Time Data",
                Description = "No DateTime columns found in this dataset.",
                Source = InsightSource.Statistical,
                Score = 0.5
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine("**Date/Time columns:**");
        foreach (var col in dateCols)
        {
            sb.AppendLine($"- **{col.Name}**: {col.MinDate:yyyy-MM-dd} to {col.MaxDate:yyyy-MM-dd}");
            if (col.TimeSeries != null)
            {
                sb.AppendLine($"  - Granularity: {col.TimeSeries.Granularity}");
                sb.AppendLine($"  - Gaps: {col.TimeSeries.GapCount} ({col.TimeSeries.GapPercent:F1}%)");
                if (col.TimeSeries.HasSeasonality)
                    sb.AppendLine($"  - Seasonality detected (period: {col.TimeSeries.SeasonalPeriod})");
            }
        }

        if (trends.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Trends detected:**");
            foreach (var col in trends)
            {
                sb.AppendLine($"- **{col.Name}**: {col.Trend!.Direction} (R² = {col.Trend.RSquared:F3})");
            }
        }

        if (tsPatterns.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Time series patterns:**");
            foreach (var p in tsPatterns)
            {
                sb.AppendLine($"- {p.Description}");
            }
        }

        return new DataInsight
        {
            Title = "Time Series Analysis",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            RelatedColumns = dateCols.Select(c => c.Name).ToList(),
            Score = 0.85
        };
    }

    private DataInsight AnswerPatterns(DataProfile profile)
    {
        var colsWithPatterns = profile.Columns.Where(c => c.TextPatterns.Count > 0).ToList();
        
        if (colsWithPatterns.Count == 0)
        {
            return new DataInsight
            {
                Title = "No Text Patterns Detected",
                Description = "No specific text patterns (email, URL, phone, UUID, etc.) were detected in text columns.",
                Source = InsightSource.Statistical,
                Score = 0.6
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine("**Text patterns detected:**");
        sb.AppendLine();

        foreach (var col in colsWithPatterns)
        {
            sb.AppendLine($"**{col.Name}**:");
            foreach (var pattern in col.TextPatterns)
            {
                if (pattern.PatternType == TextPatternType.Novel)
                {
                    // Include novel pattern details
                    sb.AppendLine($"  - **Novel Pattern**: {pattern.MatchPercent:F1}% ({pattern.MatchCount:N0} matches)");
                    if (!string.IsNullOrEmpty(pattern.Description))
                    {
                        sb.AppendLine($"    Description: {pattern.Description}");
                    }
                    if (!string.IsNullOrEmpty(pattern.DetectedRegex))
                    {
                        sb.AppendLine($"    Regex: `{pattern.DetectedRegex}`");
                    }
                    if (pattern.Examples?.Count > 0)
                    {
                        sb.AppendLine($"    Examples: {string.Join(", ", pattern.Examples.Take(3))}");
                    }
                }
                else
                {
                    sb.AppendLine($"  - {pattern.PatternType}: {pattern.MatchPercent:F1}% ({pattern.MatchCount:N0} matches)");
                }
            }
            sb.AppendLine();
        }

        return new DataInsight
        {
            Title = "Text Pattern Analysis",
            Description = sb.ToString().Trim(),
            Source = InsightSource.Statistical,
            RelatedColumns = colsWithPatterns.Select(c => c.Name).ToList(),
            Score = 0.8
        };
    }

    /// <summary>
    /// Ask a question across the registry (vector search + LLM summarization)
    /// </summary>
    public async Task<DataInsight?> AskRegistryAsync(string question, int topK = 6)
    {
        await EnsureVectorStoreAsync();
        if (_vectorStore is null || !_vectorStore.IsAvailable)
            return new DataInsight { Title = "Registry unavailable", Description = "Vector store is disabled or unavailable.", Source = InsightSource.Statistical };

        var hits = await _vectorStore.SearchAsync(question, topK);
        if (hits.Count == 0)
            return null;

        // Load profiles for context
        var profiles = await LoadProfilesForHitsAsync(hits);
        if (profiles.Count == 0)
            return null;

        // Build context text
        var context = BuildRegistryContext(profiles, hits);

        // Append prior conversation turns
        var convo = await GetConversationContextInternalAsync(question);
        var convoText = convo.Count > 0
            ? string.Join('\n', convo.Select(t => $"[{t.Role}] {t.Content}"))
            : "";


        // If no LLM, return the context as a stub insight
        if (string.IsNullOrEmpty(_ollamaModel))
        {
            var desc = context + (convoText.Length > 0 ? "\nPrior conversation:\n" + convoText : "");
            await AppendTurnAsync("user", question);
            await AppendTurnAsync("assistant", desc);
            return new DataInsight
            {
                Title = "Registry context (no LLM)",
                Description = desc,
                Source = InsightSource.Statistical,
                RelatedColumns = profiles.SelectMany(p => p.Columns.Select(c => c.Name)).Take(10).ToList()
            };
        }

        var prompt = $"""
You are a data analyst. Answer the question using ONLY the context below. Be concise (2-3 sentences) and cite dataset names.

QUESTION:
{question}

CONTEXT:
{context}

PRIOR CONVERSATION (reuse if relevant):
{convoText}
""";

        try
        {
            var client = new OllamaSharp.OllamaApiClient(new Uri(_ollamaUrl));
            var resp = await client.GenerateAsync(new OllamaSharp.Models.GenerateRequest
            {
                Model = _ollamaModel,
                Prompt = prompt
            }).StreamToEndAsync();

            var answerText = (resp?.Response ?? "").Trim();
            await AppendTurnAsync("user", question);
            await AppendTurnAsync("assistant", answerText);

            return new DataInsight
            {
                Title = "Registry answer",
                Description = answerText,
                Source = InsightSource.LlmGenerated,
                RelatedColumns = profiles.SelectMany(p => p.Columns.Select(c => c.Name)).Take(10).ToList()
            };
        }
        catch (Exception ex)
        {
            // LLM failures are graceful
            var desc = context + (convoText.Length > 0 ? "\nPrior conversation:\n" + convoText : "");
            await AppendTurnAsync("user", question);
            await AppendTurnAsync("assistant", desc);
            return new DataInsight
            {
                Title = "Registry context (fallback)",
                Description = desc,
                Source = InsightSource.Statistical,
                RelatedColumns = profiles.SelectMany(p => p.Columns.Select(c => c.Name)).Take(10).ToList()
            };
        }
    }

    private async Task<DataProfile> ProfileWithExtrasAsync(string filePath, string? sheetName, bool useLlm, int maxLlmInsights)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        // Check cache first (VSS store with content hash)
        await EnsureVectorStoreAsync();
        string? contentHash = null;
        long? fileSize = null;
        
        if (_vectorStore?.IsAvailable == true)
        {
            var (hash, size) = ProfileStore.ComputeFileHashWithSize(filePath);
            contentHash = hash;
            fileSize = size;
            
            var cached = await _vectorStore.GetCachedProfileAsync(filePath, hash);
            if (cached != null)
            {
                _profileOptions.OnStatusUpdate?.Invoke($"Cache hit: {Path.GetFileName(filePath)}");
                cached.ProfileTime = stopwatch.Elapsed; // minimal time for cache hit
                return cached;
            }
            
            _profileOptions.OnStatusUpdate?.Invoke($"Profiling: {Path.GetFileName(filePath)}");
        }
        else
        {
            _profileOptions.OnStatusUpdate?.Invoke($"Profiling: {Path.GetFileName(filePath)}");
        }

        DataProfile profile;
        using (var profiler = new DuckDbProfiler(_verbose, _profileOptions, _onnxConfig))
        {
            profile = await profiler.ProfileAsync(filePath, sheetName);
        }


        Status($"Profiled: {profile.RowCount:N0} rows, {profile.ColumnCount} cols");

        // ONNX sentinel
        if (!string.IsNullOrWhiteSpace(_onnxSentinelPath))
        {
            try
            {
                using var sentinel = new OnnxSentinel(_onnxSentinelPath!, _verbose);
                if (sentinel.IsAvailable)
                {
                    var sentinelInsights = sentinel.ScoreColumns(profile);
                    profile.Insights.AddRange(sentinelInsights);
                    Status($"ONNX sentinel: {sentinelInsights.Count} insight(s)");
                }
            }
            catch
            {
                // ONNX sentinel errors are not user-facing
            }
        }

        // LLM insights
        if (useLlm && !string.IsNullOrEmpty(_ollamaModel))
        {
            try
            {
                Status($"Generating insights with {_ollamaModel}...");
                
                using var llm = new LlmInsightGenerator(_ollamaModel, _ollamaUrl, _verbose);
                var llmInsights = await llm.GenerateInsightsAsync(filePath, profile, maxLlmInsights);
                profile.Insights.AddRange(llmInsights);
                
                Status($"Generated {llmInsights.Count} LLM insights");
                
                // Enhance novel patterns with LLM analysis
                await EnhanceNovelPatternsWithLlmAsync(profile, llm);
            }
            catch
            {
                // LLM failures are graceful - continue without insights
            }
        }

        // store system turn (profile summary) for session
        await AppendTurnAsync("system", $"Profile for {filePath} done ({profile.RowCount} rows, {profile.ColumnCount} cols)");

        profile.ProfileTime = stopwatch.Elapsed;

        // Persist to vector store for reuse (with content hash for caching)
        await PersistToVectorStoreAsync(profile, contentHash, fileSize);
        
        // Save novel patterns to vector store
        await SaveNovelPatternsAsync(profile);

        return profile;
    }

    /// <summary>
    /// Enhance novel patterns with LLM analysis to get better regex and descriptions
    /// </summary>
    private async Task EnhanceNovelPatternsWithLlmAsync(DataProfile profile, LlmInsightGenerator llm)
    {
        var columnsWithNovelPatterns = profile.Columns
            .Where(c => c.TextPatterns.Any(p => p.PatternType == TextPatternType.Novel))
            .ToList();

        if (columnsWithNovelPatterns.Count == 0) return;

        Status($"Enhancing {columnsWithNovelPatterns.Count} novel pattern(s)...");

        foreach (var col in columnsWithNovelPatterns)
        {
            var novelPattern = col.TextPatterns.First(p => p.PatternType == TextPatternType.Novel);
            
            try
            {
                // First, check if we already have a similar pattern in the registry
                await EnsureVectorStoreAsync();
                if (_vectorStore?.IsAvailable == true && novelPattern.Examples?.Count > 0)
                {
                    var existingPattern = await _vectorStore.FindMatchingPatternAsync(novelPattern.Examples, maxDistance: 0.25);
                    if (existingPattern != null)
                    {
                        // Reuse existing pattern analysis
                        novelPattern.Description = existingPattern.Description;
                        novelPattern.DetectedRegex = existingPattern.ImprovedRegex ?? existingPattern.DetectedRegex;
                        continue;
                    }
                }

                // Use LLM to analyze the novel pattern
                var analysis = await llm.AnalyzeNovelPatternAsync(
                    col.Name,
                    novelPattern.Examples ?? new List<string>(),
                    novelPattern.DetectedRegex
                );

                if (analysis != null)
                {
                    novelPattern.Description = $"{analysis.PatternName}: {analysis.Description}";
                    if (!string.IsNullOrEmpty(analysis.ImprovedRegex))
                    {
                        novelPattern.DetectedRegex = analysis.ImprovedRegex;
                    }
                    
                    // Add insight about the novel pattern
                    profile.Insights.Add(new DataInsight
                    {
                        Title = $"Novel Pattern: {analysis.PatternName}",
                        Description = $"Column **{col.Name}** contains a consistent pattern ({novelPattern.MatchPercent:F0}% of values).\n\n" +
                                     $"{analysis.Description}\n\n" +
                                     (analysis.IsIdentifier ? "⚠️ This appears to be an identifier - consider excluding from ML features.\n" : "") +
                                     (analysis.IsSensitive ? "🔒 This may contain sensitive data - review before sharing.\n" : "") +
                                     (analysis.ValidationRules.Count > 0 ? $"Validation: {string.Join(", ", analysis.ValidationRules)}" : ""),
                        Source = InsightSource.LlmGenerated,
                        RelatedColumns = new List<string> { col.Name },
                        Score = 0.75
                    });
                }
            }
            catch
            {
                // Pattern enhancement failures are graceful
            }
        }
    }

    /// <summary>
    /// Save detected novel patterns to the vector store for future reuse
    /// </summary>
    private async Task SaveNovelPatternsAsync(DataProfile profile)
    {
        await EnsureVectorStoreAsync();
        if (_vectorStore is null || !_vectorStore.IsAvailable) return;

        var columnsWithNovelPatterns = profile.Columns
            .Where(c => c.TextPatterns.Any(p => p.PatternType == TextPatternType.Novel))
            .ToList();

        foreach (var col in columnsWithNovelPatterns)
        {
            var novelPattern = col.TextPatterns.First(p => p.PatternType == TextPatternType.Novel);
            
            try
            {
                // Parse the description to extract pattern name if LLM-enhanced
                var patternName = "Novel Pattern";
                var description = novelPattern.Description ?? $"Consistent format in column {col.Name}";
                
                if (novelPattern.Description?.Contains(':') == true)
                {
                    var parts = novelPattern.Description.Split(':', 2);
                    patternName = parts[0].Trim();
                    description = parts.Length > 1 ? parts[1].Trim() : description;
                }

                var record = new NovelPatternRecord
                {
                    PatternName = patternName,
                    ColumnName = col.Name,
                    FilePath = profile.SourcePath,
                    PatternType = "Novel",
                    DetectedRegex = novelPattern.DetectedRegex,
                    ImprovedRegex = novelPattern.DetectedRegex, // Same for now, LLM may have improved it
                    Description = description,
                    Examples = novelPattern.Examples,
                    MatchPercent = novelPattern.MatchPercent,
                    IsIdentifier = col.SemanticRole == SemanticRole.Identifier,
                    IsSensitive = false, // Default, LLM analysis may override
                    ValidationRules = null
                };

                await _vectorStore.UpsertNovelPatternAsync(record);
            }
            catch
            {
                // Pattern save failures are graceful
            }
        }
    }

    private async Task<DataSummaryReport> GenerateReportAsync(DataProfile profile, bool allowLlm, CancellationToken cancellationToken = default)
    {
        var baseBuilder = new StringBuilder();
        baseBuilder.AppendLine($"# Data Summary: {Path.GetFileName(profile.SourcePath)}");
        baseBuilder.AppendLine();
        baseBuilder.AppendLine($"> Generated in {profile.ProfileTime.TotalSeconds:F1}s");
        baseBuilder.AppendLine();

        baseBuilder.AppendLine("## Dataset Overview");
        baseBuilder.AppendLine();
        baseBuilder.AppendLine("| Property | Value |");
        baseBuilder.AppendLine("|----------|-------|");
        baseBuilder.AppendLine($"| **Rows** | {profile.RowCount:N0} |");
        baseBuilder.AppendLine($"| **Columns** | {profile.ColumnCount} |");
        baseBuilder.AppendLine($"| **Source Type** | {profile.SourceType} |");
        if (profile.SheetName != null)
            baseBuilder.AppendLine($"| **Sheet** | {profile.SheetName} |");
        if (profile.Target != null)
            baseBuilder.AppendLine($"| **Target** | {profile.Target.ColumnName} |");
        baseBuilder.AppendLine();

        baseBuilder.AppendLine("### Column Types");
        baseBuilder.AppendLine();
        var typeGroups = profile.Columns.GroupBy(c => c.InferredType).OrderByDescending(g => g.Count());
        foreach (var group in typeGroups)
        {
            var colNames = string.Join(", ", group.Select(c => $"`{c.Name}`"));
            baseBuilder.AppendLine($"- **{group.Key}** ({group.Count()}): {colNames}");
        }
        baseBuilder.AppendLine();

        baseBuilder.AppendLine("## Column Profiles");
        baseBuilder.AppendLine();
        baseBuilder.AppendLine("| Column | Type | Nulls | Unique | Stats |");
        baseBuilder.AppendLine("|--------|------|-------|--------|-------|");
        foreach (var col in profile.Columns)
        {
            var stats = GetColumnStatsString(col);
            baseBuilder.AppendLine($"| `{col.Name}` | {col.InferredType} | {col.NullPercent:F1}% | {col.UniqueCount:N0} | {stats} |");
        }
        baseBuilder.AppendLine();

        if (profile.Target != null)
        {
            baseBuilder.AppendLine("## Target Analysis");
            baseBuilder.AppendLine();
            baseBuilder.AppendLine("### Class Distribution");
            baseBuilder.AppendLine();
            baseBuilder.AppendLine("| Class | Share |");
            baseBuilder.AppendLine("|-------|-------|");
            foreach (var kvp in profile.Target.ClassDistribution)
            {
                baseBuilder.AppendLine($"| {kvp.Key} | {kvp.Value:P1} |");
            }
            baseBuilder.AppendLine();

            if (profile.Target.FeatureEffects.Count > 0)
            {
                baseBuilder.AppendLine("### Top Drivers");
                baseBuilder.AppendLine();
                foreach (var effect in profile.Target.FeatureEffects.Take(10))
                {
                    baseBuilder.AppendLine($"- **{effect.Feature}** ({effect.Metric}): {effect.Summary}");
                }
                baseBuilder.AppendLine();
            }
        }

        if (profile.Alerts.Count > 0)
        {
            baseBuilder.AppendLine("## Data Quality Alerts");
            baseBuilder.AppendLine();
            var alertsByType = profile.Alerts.GroupBy(a => a.Severity).OrderByDescending(g => (int)g.Key);
            foreach (var group in alertsByType)
            {
                var icon = group.Key switch
                {
                    AlertSeverity.Error => "🔴",
                    AlertSeverity.Warning => "🟡",
                    _ => "🔵"
                };
                foreach (var alert in group)
                {
                    baseBuilder.AppendLine($"- {icon} **{alert.Column}**: {alert.Message}");
                }
            }
            baseBuilder.AppendLine();
        }

        if (profile.Correlations.Count > 0)
        {
            baseBuilder.AppendLine("## Correlations");
            baseBuilder.AppendLine();
            baseBuilder.AppendLine("| Column 1 | Column 2 | Metric | Value | Strength |");
            baseBuilder.AppendLine("|----------|----------|--------|-------|----------|");
            foreach (var corr in profile.Correlations.Take(10))
            {
                baseBuilder.AppendLine($"| `{corr.Column1}` | `{corr.Column2}` | {corr.Metric} | {corr.Correlation:F3} | {corr.Strength} |");
            }
            baseBuilder.AppendLine();
        }

        if (profile.Insights.Count > 0)
        {
            baseBuilder.AppendLine("## Insights");
            baseBuilder.AppendLine();
            foreach (var insight in profile.Insights.OrderByDescending(i => i.Score))
            {
                var scoreText = insight.Score > 0 ? $" _(score {insight.Score:F2})_" : string.Empty;
                baseBuilder.AppendLine($"### {insight.Title}{scoreText}");
                baseBuilder.AppendLine();
                baseBuilder.AppendLine(insight.Description);
                if (!string.IsNullOrWhiteSpace(insight.Sql))
                {
                    baseBuilder.AppendLine();
                    baseBuilder.AppendLine("```sql");
                    baseBuilder.AppendLine(insight.Sql);
                    baseBuilder.AppendLine("```");
                }
                baseBuilder.AppendLine();
            }
        }

        var categoricalCols = profile.Columns
            .Where(c => c.InferredType == ColumnType.Categorical && c.TopValues?.Count > 0)
            .ToList();
        if (categoricalCols.Count > 0)
        {
            baseBuilder.AppendLine("## Top Values (Categorical Columns)");
            baseBuilder.AppendLine();
            foreach (var col in categoricalCols.Take(5))
            {
                baseBuilder.AppendLine($"### {col.Name}");
                baseBuilder.AppendLine();
                baseBuilder.AppendLine("| Value | Count | % |");
                baseBuilder.AppendLine("|-------|-------|---|");
                foreach (var val in col.TopValues!.Take(10))
                {
                    var displayVal = val.Value.Length > 40 ? val.Value[..37] + "..." : val.Value;
                    baseBuilder.AppendLine($"| {displayVal} | {val.Count:N0} | {val.Percent:F1}% |");
                }
                baseBuilder.AppendLine();
            }
        }

        var baseMarkdown = baseBuilder.ToString();
        var focusFindings = new Dictionary<string, string>();
        var executiveSummary = GenerateExecutiveSummary(profile);

        if (_reportOptions.GenerateMarkdown && allowLlm && !string.IsNullOrWhiteSpace(_ollamaModel))
        {
            try
            {
                using var llm = new LlmInsightGenerator(_ollamaModel, _ollamaUrl, _verbose);
                var focusList = _reportOptions.IncludeFocusQuestions ? _reportOptions.FocusQuestions : new List<string>();
                var narrative = await llm.GenerateReportNarrativeAsync(profile, focusList);
                if (!string.IsNullOrWhiteSpace(narrative.Summary))
                {
                    executiveSummary = narrative.Summary;
                }
                focusFindings = narrative.FocusAnswers;
            }
            catch
            {
                // LLM narrative failures are graceful
            }
        }

        var finalBuilder = new StringBuilder(baseMarkdown);
        if (focusFindings.Count > 0)
        {
            finalBuilder.AppendLine("## Focus Question Answers");
            finalBuilder.AppendLine();
            foreach (var kvp in focusFindings)
            {
                finalBuilder.AppendLine($"### {kvp.Key}");
                finalBuilder.AppendLine();
                finalBuilder.AppendLine(kvp.Value);
                finalBuilder.AppendLine();
            }
        }

        var markdown = finalBuilder.ToString();
        if (string.IsNullOrWhiteSpace(executiveSummary))
        {
            var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
            var plain = Markdown.ToPlainText(markdown, pipeline)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Take(5);
            executiveSummary = string.Join(' ', plain);
        }

        return new DataSummaryReport
        {
            Profile = profile,
            ExecutiveSummary = executiveSummary,
            MarkdownReport = markdown,
            FocusFindings = focusFindings
        };
    }

    private async Task PersistToVectorStoreAsync(DataProfile profile, string? contentHash = null, long? fileSize = null)
    {
        await EnsureVectorStoreAsync();
        if (_vectorStore is null || !_vectorStore.IsAvailable) return;

        try
        {
            await _vectorStore.UpsertProfileAsync(profile, contentHash, fileSize);
            await _vectorStore.UpsertEmbeddingsAsync(profile);
            Status($"Cached: {Path.GetFileName(profile.SourcePath)}");
        }
        catch
        {
            // Cache persist failures are graceful
        }
    }

    private async Task EnsureVectorStoreAsync()
    {
        if (_vectorStoreInitialized) return;
        if (string.IsNullOrWhiteSpace(_vectorStorePath)) return;

        _vectorStore = new VectorStoreService(_vectorStorePath!, _verbose);
        await _vectorStore.InitializeAsync();
        _vectorStoreInitialized = true;
    }

    private async Task<List<DataProfile>> LoadProfilesForHitsAsync(List<RegistryHit> hits)
    {
        var profiles = new List<DataProfile>();
        if (_vectorStore?.Connection is null) return profiles;

        try
        {
            var placeholders = string.Join(",", hits.Select(_ => "?"));
            var sql = $"SELECT file_path, profile_json FROM registry_files WHERE file_path IN ({placeholders})";
            await using var cmd = _vectorStore.Connection.CreateCommand();
            cmd.CommandText = sql;
            for (int i = 0; i < hits.Count; i++)
            {
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = hits[i].FilePath });
            }

            await using var reader = await cmd.ExecuteReaderAsync();
            var map = new Dictionary<string, string>();
            while (await reader.ReadAsync())
            {
                var path = reader.GetString(0);
                var json = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (json != null) map[path] = json;
            }

            foreach (var hit in hits)
            {
                if (map.TryGetValue(hit.FilePath, out var json))
                {
                    try
                    {
                        var profile = System.Text.Json.JsonSerializer.Deserialize<DataProfile>(json);
                        if (profile != null) profiles.Add(profile);
                    }
                    catch
                    {
                        // ignore bad rows
                    }
                }
            }
        }
        catch
        {
            // Load failures are graceful
        }

        return profiles;
    }

    private async Task<List<ConversationTurn>> GetConversationContextInternalAsync(string query)
    {
        await EnsureVectorStoreAsync();
        if (_vectorStore is null || !_vectorStore.IsAvailable) return new List<ConversationTurn>();
        return await _vectorStore.GetConversationContextAsync(_sessionId, query, topK: 5);
    }

    private static string BuildRegistryContext(List<DataProfile> profiles, List<RegistryHit> hits)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            var hit = hits.First(h => h.FilePath == p.SourcePath);
            var tableName = SanitizeTableName(Path.GetFileNameWithoutExtension(p.SourcePath));
            sb.AppendLine($"- Dataset: {Path.GetFileName(p.SourcePath)} (table `{tableName}`, score {hit.Score:F3})");
            sb.AppendLine($"  Rows: {p.RowCount:N0}, Columns: {p.ColumnCount}");
            sb.AppendLine($"  Types: numeric {p.Columns.Count(c => c.InferredType == ColumnType.Numeric)}, categorical {p.Columns.Count(c => c.InferredType == ColumnType.Categorical)}, date/time {p.Columns.Count(c => c.InferredType == ColumnType.DateTime)}");
            var interestingCols = p.Columns.Take(8)
                .Select(c => $"{c.Name} ({c.InferredType})");
            sb.AppendLine($"  Columns: {string.Join(", ", interestingCols)}");
            sb.AppendLine($"  SQL hint: SELECT * FROM {tableName} LIMIT 5");
            if (p.Insights.Count > 0)
            {
                var insight = p.Insights.First();
                sb.AppendLine($"  Insight: {insight.Title} - {insight.Description}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string SanitizeTableName(string name)
    {
        // Lowercase, replace non-alphanumerics with underscore, collapse repeats
        var cleaned = new string(name.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray());
        while (cleaned.Contains("__")) cleaned = cleaned.Replace("__", "_");
        cleaned = cleaned.Trim('_');
        if (string.IsNullOrEmpty(cleaned)) cleaned = "table";
        if (char.IsDigit(cleaned[0])) cleaned = "t_" + cleaned;
        return cleaned;
    }



    private string GenerateExecutiveSummary(DataProfile profile)
    {
        var sb = new StringBuilder();
        
        // Size summary
        sb.Append($"This dataset contains **{profile.RowCount:N0} rows** and **{profile.ColumnCount} columns**. ");
        
        // Column type breakdown
        var numericCount = profile.Columns.Count(c => c.InferredType == ColumnType.Numeric);
        var categoricalCount = profile.Columns.Count(c => c.InferredType == ColumnType.Categorical);
        var dateCount = profile.Columns.Count(c => c.InferredType == ColumnType.DateTime);
        
        var parts = new List<string>();
        if (numericCount > 0) parts.Add($"{numericCount} numeric");
        if (categoricalCount > 0) parts.Add($"{categoricalCount} categorical");
        if (dateCount > 0) parts.Add($"{dateCount} date/time");
        
        if (parts.Count > 0)
        {
            sb.Append($"Column breakdown: {string.Join(", ", parts)}. ");
        }

        // Data quality
        var nullyCols = profile.Columns.Where(c => c.NullPercent > 10).ToList();
        if (nullyCols.Count > 0)
        {
            sb.Append($"**{nullyCols.Count} column(s)** have >10% null values. ");
        }

        // Correlations
        var strongCorrs = profile.Correlations.Where(c => Math.Abs(c.Correlation) >= 0.7).ToList();
        if (strongCorrs.Count > 0)
        {
            sb.Append($"Found **{strongCorrs.Count} strong correlation(s)**. ");
        }

        // Alerts
        var criticalAlerts = profile.Alerts.Count(a => a.Severity == AlertSeverity.Error);
        var warningAlerts = profile.Alerts.Count(a => a.Severity == AlertSeverity.Warning);
        
        if (criticalAlerts > 0)
        {
            sb.Append($"⚠️ **{criticalAlerts} critical issue(s)** detected. ");
        }
        else if (warningAlerts > 0)
        {
            sb.Append($"Found {warningAlerts} warning(s) to review. ");
        }
        else
        {
            sb.Append("No major data quality issues detected. ");
        }

        return sb.ToString().Trim();
    }

    private string GetColumnStatsString(ColumnProfile col)
    {
        return col.InferredType switch
        {
            ColumnType.Numeric when col.Mean.HasValue => 
                $"μ={col.Mean:F1}, σ={col.StdDev:F1}, MAD={col.Mad:F1}",
            ColumnType.Categorical when col.TopValues?.Count > 0 => 
                $"top: {col.TopValues[0].Value} ({col.TopValues[0].Percent:F0}%)",
            ColumnType.DateTime when col.MinDate.HasValue => 
                $"{col.MinDate:yyyy-MM-dd} → {col.MaxDate:yyyy-MM-dd}",
            ColumnType.Text when col.AvgLength.HasValue => 
                $"avg len: {col.AvgLength:F0}",
            ColumnType.Boolean => "true/false",
            ColumnType.Id => "identifier",
            _ => "-"
        };
    }

    public void Dispose()
    {
        _vectorStore?.Dispose();
    }
}
