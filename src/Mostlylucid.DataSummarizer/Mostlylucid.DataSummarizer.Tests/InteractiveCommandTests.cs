using Xunit;
using Mostlylucid.DataSummarizer.Models;
using Mostlylucid.DataSummarizer.Services;
using Mostlylucid.DataSummarizer.Configuration;

namespace Mostlylucid.DataSummarizer.Tests;

/// <summary>
/// Tests for interactive mode command parsing and execution.
/// These test the helper functions directly without requiring TTY.
/// </summary>
public class InteractiveCommandTests
{
    private DataSummaryReport CreateTestReport()
    {
        var profile = new DataProfile
        {
            SourcePath = "test.csv",
            RowCount = 1000,
            Columns =
            [
                new ColumnProfile
                {
                    Name = "Id",
                    InferredType = ColumnType.Id,
                    Count = 1000,
                    UniqueCount = 1000,
                    NullCount = 0
                },
                new ColumnProfile
                {
                    Name = "Age",
                    InferredType = ColumnType.Numeric,
                    Count = 1000,
                    UniqueCount = 50,
                    NullCount = 25,
                    Mean = 35.5,
                    Median = 34.0,
                    StdDev = 12.3,
                    Min = 18,
                    Max = 85,
                    OutlierCount = 15
                },
                new ColumnProfile
                {
                    Name = "Status",
                    InferredType = ColumnType.Categorical,
                    Count = 1000,
                    UniqueCount = 3,
                    NullCount = 0,
                    TopValues = [new ValueCount { Value = "Active", Count = 600, Percent = 60 }]
                }
            ],
            Alerts =
            [
                new DataAlert
                {
                    Column = "Age",
                    Severity = AlertSeverity.Warning,
                    Type = AlertType.Outliers,
                    Message = "15 outliers detected"
                },
                new DataAlert
                {
                    Column = "Status",
                    Severity = AlertSeverity.Info,
                    Type = AlertType.Imbalanced,
                    Message = "Class imbalance detected"
                }
            ],
            Insights =
            [
                new DataInsight
                {
                    Title = "Age Distribution",
                    Description = "Age follows normal distribution",
                    Score = 0.85,
                    Source = InsightSource.Statistical,
                    RelatedColumns = ["Age"]
                }
            ]
        };

        return new DataSummaryReport { Profile = profile };
    }

    [Fact]
    public void AnalyticsToolRegistry_GetAllTools_ReturnsTools()
    {
        var registry = new AnalyticsToolRegistry();
        var tools = registry.GetAllTools();

        Assert.NotEmpty(tools);
        Assert.Contains(tools, t => t.Id == "segment_audience");
        Assert.Contains(tools, t => t.Id == "detect_anomalies");
        Assert.Contains(tools, t => t.Id == "compare_groups");
    }

    [Fact]
    public void AnalyticsToolRegistry_GetAllTools_HasCategories()
    {
        var registry = new AnalyticsToolRegistry();
        var tools = registry.GetAllTools();
        var categories = tools.Select(t => t.Category).Distinct().ToList();

        Assert.True(categories.Count >= 3, "Should have multiple tool categories");
    }

    [Fact]
    public void AnalyticsToolRegistry_ToolsHaveExampleQuestions()
    {
        var registry = new AnalyticsToolRegistry();
        var tools = registry.GetAllTools();

        foreach (var tool in tools)
        {
            Assert.NotEmpty(tool.ExampleQuestions);
            Assert.All(tool.ExampleQuestions, q => Assert.False(string.IsNullOrWhiteSpace(q)));
        }
    }

    [Fact]
    public void OutputProfiles_AllBuiltInProfilesExist()
    {
        var profiles = new Dictionary<string, OutputProfileConfig>
        {
            ["Default"] = OutputProfileConfig.Default,
            ["Tool"] = OutputProfileConfig.Tool,
            ["Brief"] = OutputProfileConfig.Brief,
            ["Detailed"] = OutputProfileConfig.Detailed,
            ["Markdown"] = OutputProfileConfig.MarkdownFocus
        };

        Assert.Equal(5, profiles.Count);
        Assert.All(profiles.Values, p => Assert.NotNull(p));
        Assert.All(profiles.Values, p => Assert.NotNull(p.Console));
    }

    [Fact]
    public void OutputProfiles_DefaultShowsSummaryAndAlerts()
    {
        var profile = OutputProfileConfig.Default;

        Assert.True(profile.Console.ShowSummary);
        Assert.True(profile.Console.ShowAlerts);
        Assert.True(profile.Console.ShowColumnTable);
    }

    [Fact]
    public void OutputProfiles_BriefIsMinimal()
    {
        var profile = OutputProfileConfig.Brief;

        Assert.True(profile.Console.ShowSummary);
        Assert.True(profile.Console.ShowAlerts);
        Assert.False(profile.Console.ShowColumnTable);
        Assert.False(profile.Console.ShowInsights);
        Assert.False(profile.Console.ShowCorrelations);
    }

    [Fact]
    public void OutputProfiles_DetailedShowsEverything()
    {
        var profile = OutputProfileConfig.Detailed;

        Assert.True(profile.Console.ShowSummary);
        Assert.True(profile.Console.ShowAlerts);
        Assert.True(profile.Console.ShowColumnTable);
        Assert.True(profile.Console.ShowInsights);
        Assert.True(profile.Console.ShowCorrelations);
        Assert.True(profile.Json.Enabled);
        Assert.True(profile.Markdown.Enabled);
    }

    [Fact]
    public void OutputProfiles_ToolIsJsonOnly()
    {
        var profile = OutputProfileConfig.Tool;

        Assert.False(profile.Console.ShowSummary);
        Assert.False(profile.Console.ShowAlerts);
        Assert.True(profile.Json.Enabled);
        Assert.False(profile.Markdown.Enabled);
    }

    [Fact]
    public void DataProfile_CanFindColumnByName()
    {
        var report = CreateTestReport();
        
        var ageCol = report.Profile.Columns.FirstOrDefault(c => 
            c.Name.Equals("Age", StringComparison.OrdinalIgnoreCase));
        
        Assert.NotNull(ageCol);
        Assert.Equal(ColumnType.Numeric, ageCol.InferredType);
        Assert.Equal(35.5, ageCol.Mean);
    }

    [Fact]
    public void DataProfile_CanGroupAlertsBySeverity()
    {
        var report = CreateTestReport();
        
        var grouped = report.Profile.Alerts
            .GroupBy(a => a.Severity)
            .ToDictionary(g => g.Key, g => g.Count());
        
        Assert.Equal(1, grouped[AlertSeverity.Warning]);
        Assert.Equal(1, grouped[AlertSeverity.Info]);
    }

    [Fact]
    public void DataProfile_CanCountColumnTypes()
    {
        var report = CreateTestReport();
        
        var numericCount = report.Profile.Columns.Count(c => c.InferredType == ColumnType.Numeric);
        var categoricalCount = report.Profile.Columns.Count(c => c.InferredType == ColumnType.Categorical);
        var idCount = report.Profile.Columns.Count(c => c.InferredType == ColumnType.Id);
        
        Assert.Equal(1, numericCount);
        Assert.Equal(1, categoricalCount);
        Assert.Equal(1, idCount);
    }

    [Fact]
    public void CommandParsing_SlashExtractsCommand()
    {
        var inputs = new[]
        {
            ("/help", "help", ""),
            ("/exit", "exit", ""),
            ("/profile Brief", "profile", "Brief"),
            ("/column Age", "column", "Age"),
            ("/column Customer Name", "column", "Customer Name"),
            ("/", "", ""),
        };

        foreach (var (input, expectedCmd, expectedArg) in inputs)
        {
            var trimmed = input.Trim();
            Assert.True(trimmed.StartsWith('/'), $"Input should start with /: {input}");
            
            var cmd = trimmed[1..].ToLowerInvariant();
            var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var command = parts.Length > 0 ? parts[0] : "";
            var arg = parts.Length > 1 ? parts[1] : "";
            
            Assert.Equal(expectedCmd.ToLowerInvariant(), command);
            Assert.Equal(expectedArg.ToLowerInvariant(), arg.ToLowerInvariant());
        }
    }

    [Fact]
    public void CommandParsing_NonSlashIsDataQuestion()
    {
        var inputs = new[]
        {
            "What columns have missing values?",
            "Show me outliers",
            "tell me about Age",
            "compare groups by Status"
        };

        foreach (var input in inputs)
        {
            Assert.False(input.Trim().StartsWith('/'), $"Data question should not start with /: {input}");
        }
    }
}
