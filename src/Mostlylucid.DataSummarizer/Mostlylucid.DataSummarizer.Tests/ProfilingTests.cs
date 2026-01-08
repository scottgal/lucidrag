using System.Text;
using Mostlylucid.DataSummarizer.Models;
using Mostlylucid.DataSummarizer.Services;
using Xunit;

namespace Mostlylucid.DataSummarizer.Tests;

public class ProfilingTests
{
    [Fact]
    public async Task ProfilesBasicCsv()
    {
        // Use more rows so columns are classified as Numeric, not Id
        var csv = "A,B\n1,2\n3,4\n5,6\n1,2\n3,4\n5,6\n1,2\n3,4\n5,6\n1,2\n";
        var path = WriteTempCsv(csv);
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);

        var report = await svc.SummarizeAsync(path, useLlm: false);

        Assert.Equal(10, report.Profile.RowCount);
        Assert.Equal(2, report.Profile.ColumnCount);
        Assert.Contains(report.Profile.Columns, c => c.Name == "A" && c.InferredType == ColumnType.Numeric);
        Assert.Contains(report.Profile.Columns, c => c.Name == "B" && c.InferredType == ColumnType.Numeric);
    }

    [Fact]
    public async Task HighUniqueNumericNotFlaggedAsId()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Salary");
        for (int i = 1; i <= 200; i++)
        {
            sb.AppendLine($"{i},{100000 + i}");
        }
        var path = WriteTempCsv(sb.ToString());
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);

        var report = await svc.SummarizeAsync(path, useLlm: false);
        var alerts = report.Profile.Alerts;
        Assert.DoesNotContain(alerts, a => a.Column == "Salary" && a.Type == AlertType.HighCardinality);
    }

    [Fact]
    public async Task CalculatesEntropyForCategoricalColumns()
    {
        // Uniform distribution: 3 values with equal frequency
        var csv = "Category\nA\nB\nC\nA\nB\nC\nA\nB\nC\n";
        var path = WriteTempCsv(csv);
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);

        var report = await svc.SummarizeAsync(path, useLlm: false);
        var col = report.Profile.Columns.First(c => c.Name == "Category");

        Assert.NotNull(col.Entropy);
        Assert.True(col.Entropy > 1.5, "Uniform distribution should have high entropy (near log2(3) ≈ 1.58)");
    }

    [Fact]
    public async Task CalculatesKurtosisForNumericColumns()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Value");
        // Generate approximately normal data
        var random = new Random(42);
        for (int i = 0; i < 500; i++)
        {
            // Box-Muller transform for normal distribution
            var u1 = random.NextDouble();
            var u2 = random.NextDouble();
            var normal = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            var value = 100 + 15 * normal; // Mean=100, StdDev=15
            sb.AppendLine(value.ToString("F2"));
        }
        var path = WriteTempCsv(sb.ToString());
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);

        var report = await svc.SummarizeAsync(path, useLlm: false);
        var col = report.Profile.Columns.First(c => c.Name == "Value");

        Assert.NotNull(col.Kurtosis);
        // Normal distribution has kurtosis near 3
        Assert.InRange(col.Kurtosis.Value, 2, 5);
    }

    [Fact]
    public async Task CountsZerosInNumericColumns()
    {
        var csv = "Amount\n0\n0\n0\n100\n200\n300\n0\n50\n";
        var path = WriteTempCsv(csv);
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);

        var report = await svc.SummarizeAsync(path, useLlm: false);
        var col = report.Profile.Columns.First(c => c.Name == "Amount");

        Assert.Equal(4, col.ZeroCount);
    }

    [Fact]
    public async Task CalculatesCoefficientOfVariation()
    {
        // High variability data
        var csv = "Value\n1\n10\n100\n1000\n10000\n";
        var path = WriteTempCsv(csv);
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);

        var report = await svc.SummarizeAsync(path, useLlm: false);
        var col = report.Profile.Columns.First(c => c.Name == "Value");

        Assert.NotNull(col.CoefficientOfVariation);
        Assert.True(col.CoefficientOfVariation > 1, "High variability data should have CV > 1");
    }

    [Fact]
    public async Task CalculatesIqr()
    {
        var csv = "Value\n1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n";
        var path = WriteTempCsv(csv);
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);

        var report = await svc.SummarizeAsync(path, useLlm: false);
        var col = report.Profile.Columns.First(c => c.Name == "Value");

        Assert.NotNull(col.Iqr);
        Assert.True(col.Iqr > 0, "IQR should be positive for varied data");
    }

    [Fact]
    public async Task DetectsModeForCategoricalColumns()
    {
        var csv = "Color\nRed\nBlue\nRed\nRed\nGreen\nBlue\n";
        var path = WriteTempCsv(csv);
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);

        var report = await svc.SummarizeAsync(path, useLlm: false);
        var col = report.Profile.Columns.First(c => c.Name == "Color");

        Assert.Equal("Red", col.Mode);
    }

    [Fact]
    public async Task CalculatesTextLengthStats()
    {
        // Generate many unique values so the column is classified as Text, not Categorical
        var sb = new StringBuilder();
        sb.AppendLine("Description");
        for (int i = 0; i < 100; i++)
        {
            sb.AppendLine($"This is a unique description number {i} with enough text to be classified as text");
        }
        var path = WriteTempCsv(sb.ToString());
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);

        var report = await svc.SummarizeAsync(path, useLlm: false);
        var col = report.Profile.Columns.First(c => c.Name == "Description");

        Assert.Equal(ColumnType.Text, col.InferredType);
        Assert.NotNull(col.MinLength);
        Assert.NotNull(col.MaxLength);
        Assert.NotNull(col.AvgLength);
        Assert.True(col.MinLength > 0);
    }

    [Fact]
    public async Task ProfileBasedQueryAnswersMissingValues()
    {
        // Create data with clear nulls
        var sb = new StringBuilder();
        sb.AppendLine("A,B,C");
        sb.AppendLine("1,,3");
        sb.AppendLine("4,5,");
        sb.AppendLine("7,8,9");
        sb.AppendLine("10,,12");
        sb.AppendLine("13,14,");
        var path = WriteTempCsv(sb.ToString());
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);

        // AskAsync should work without LLM for common questions about nulls
        var insight = await svc.AskAsync(path, "Which columns have null values?");

        Assert.NotNull(insight);
        // Should either say "Missing" or "No Missing" 
        Assert.True(
            insight.Title.Contains("Missing", StringComparison.OrdinalIgnoreCase) ||
            insight.Title.Contains("Null", StringComparison.OrdinalIgnoreCase) ||
            insight.Description.Contains("null", StringComparison.OrdinalIgnoreCase) ||
            insight.Description.Contains("missing", StringComparison.OrdinalIgnoreCase),
            $"Expected insight about missing values but got: {insight.Title} - {insight.Description}"
        );
    }

    [Fact]
    public async Task ProfileBasedQueryAnswersSchema()
    {
        var csv = "Name,Age,Salary\nAlice,30,50000\nBob,25,45000\n";
        var path = WriteTempCsv(csv);
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);

        var insight = await svc.AskAsync(path, "What columns are in this dataset?");

        Assert.NotNull(insight);
        Assert.Contains("Schema", insight.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Name", insight.Description);
        Assert.Contains("Age", insight.Description);
        Assert.Contains("Salary", insight.Description);
    }

    [Fact]
    public async Task ProfileBasedQueryAnswersNumericStats()
    {
        // Create data with many values so column is classified as Numeric
        var sb = new StringBuilder();
        sb.AppendLine("Value");
        for (int i = 1; i <= 50; i++)
        {
            sb.AppendLine((i * 10).ToString());
        }
        var path = WriteTempCsv(sb.ToString());
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);

        var insight = await svc.AskAsync(path, "What is the mean and median?");

        Assert.NotNull(insight);
        // Should be about numeric stats
        Assert.True(
            insight.Title.Contains("Numeric", StringComparison.OrdinalIgnoreCase) ||
            insight.Description.Contains("Mean", StringComparison.OrdinalIgnoreCase) ||
            insight.Description.Contains("Median", StringComparison.OrdinalIgnoreCase),
            $"Expected numeric stats but got: {insight.Title} - {insight.Description}"
        );
    }

    private static string WriteTempCsv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ds-test-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        return path;
    }
}
