using System.Text;
using Mostlylucid.DataSummarizer.Models;
using Mostlylucid.DataSummarizer.Services;
using Xunit;

namespace Mostlylucid.DataSummarizer.Tests;

/// <summary>
/// Tests for DataSynthesizer functionality
/// </summary>
public class DataSynthesizerTests
{
    [Fact]
    public async Task GeneratesCsvWithCorrectRowCount()
    {
        var csv = "Name,Age,Salary\nAlice,30,50000\nBob,25,45000\nCharlie,35,60000\n";
        var path = WriteTempCsv(csv);
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);
        var report = await svc.SummarizeAsync(path, useLlm: false);

        var outputPath = Path.Combine(Path.GetTempPath(), $"synth-{Guid.NewGuid():N}.csv");
        DataSynthesizer.GenerateCsv(report.Profile, 100, outputPath);

        Assert.True(File.Exists(outputPath));
        var lines = File.ReadAllLines(outputPath);
        Assert.Equal(101, lines.Length); // 100 data rows + 1 header
    }

    [Fact]
    public async Task GeneratesCsvWithCorrectColumns()
    {
        var csv = "Name,Age,Salary\nAlice,30,50000\nBob,25,45000\n";
        var path = WriteTempCsv(csv);
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);
        var report = await svc.SummarizeAsync(path, useLlm: false);

        var outputPath = Path.Combine(Path.GetTempPath(), $"synth-{Guid.NewGuid():N}.csv");
        DataSynthesizer.GenerateCsv(report.Profile, 10, outputPath);

        var lines = File.ReadAllLines(outputPath);
        var header = lines[0];
        Assert.Contains("Name", header);
        Assert.Contains("Age", header);
        Assert.Contains("Salary", header);
    }

    [Fact]
    public async Task GeneratesNumericDataWithinRange()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Value");
        for (int i = 0; i < 100; i++)
        {
            sb.AppendLine((i * 10).ToString());
        }
        var path = WriteTempCsv(sb.ToString());
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);
        var report = await svc.SummarizeAsync(path, useLlm: false);

        var outputPath = Path.Combine(Path.GetTempPath(), $"synth-{Guid.NewGuid():N}.csv");
        DataSynthesizer.GenerateCsv(report.Profile, 50, outputPath);

        var lines = File.ReadAllLines(outputPath).Skip(1).ToList();
        foreach (var line in lines)
        {
            if (double.TryParse(line, out var val))
            {
                // Should be roughly within the range of original data
                Assert.True(val >= -100 && val <= 1100, $"Value {val} outside expected range");
            }
        }
    }

    [Fact]
    public async Task GeneratesCategoricalDataFromDistribution()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Color");
        for (int i = 0; i < 30; i++) sb.AppendLine("Red");
        for (int i = 0; i < 20; i++) sb.AppendLine("Blue");
        for (int i = 0; i < 10; i++) sb.AppendLine("Green");

        var path = WriteTempCsv(sb.ToString());
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);
        var report = await svc.SummarizeAsync(path, useLlm: false);

        var outputPath = Path.Combine(Path.GetTempPath(), $"synth-{Guid.NewGuid():N}.csv");
        DataSynthesizer.GenerateCsv(report.Profile, 100, outputPath);

        var lines = File.ReadAllLines(outputPath).Skip(1).ToList();
        var colors = new HashSet<string>(lines);

        // Should only contain the original categories
        Assert.True(colors.All(c => c == "Red" || c == "Blue" || c == "Green" || string.IsNullOrEmpty(c)));
    }

    [Fact]
    public async Task HandlesNullPercentage()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Value");
        for (int i = 0; i < 50; i++) sb.AppendLine(i.ToString());
        for (int i = 0; i < 50; i++) sb.AppendLine(""); // 50% nulls

        var path = WriteTempCsv(sb.ToString());
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);
        var report = await svc.SummarizeAsync(path, useLlm: false);

        // Verify that the profile captured the null percentage
        var col = report.Profile.Columns.First();
        Assert.InRange(col.NullPercent, 40, 60); // Should be around 50%

        // DataSynthesizer currently generates values based on distribution
        // but doesn't necessarily inject nulls - that's a feature enhancement
        var outputPath = Path.Combine(Path.GetTempPath(), $"synth-{Guid.NewGuid():N}.csv");
        DataSynthesizer.GenerateCsv(report.Profile, 100, outputPath);

        Assert.True(File.Exists(outputPath));
        var lines = File.ReadAllLines(outputPath);
        Assert.Equal(101, lines.Length); // 100 rows + header
    }

    [Fact]
    public async Task GeneratesMultipleColumnsCorrectly()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Name,Score,Active");
        for (int i = 1; i <= 20; i++)
        {
            sb.AppendLine($"{i},User{i},{50 + i},{(i % 2 == 0 ? "true" : "false")}");
        }

        var path = WriteTempCsv(sb.ToString());
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);
        var report = await svc.SummarizeAsync(path, useLlm: false);

        var outputPath = Path.Combine(Path.GetTempPath(), $"synth-{Guid.NewGuid():N}.csv");
        DataSynthesizer.GenerateCsv(report.Profile, 50, outputPath);

        var lines = File.ReadAllLines(outputPath);
        Assert.Equal(51, lines.Length);

        // Check each row has 4 columns
        // Use proper CSV parsing to handle quoted values with commas
        var header = lines[0];
        var headerParts = ParseCsvLine(header);
        Assert.Equal(4, headerParts.Length);
        
        foreach (var line in lines.Skip(1))
        {
            var parts = ParseCsvLine(line);
            Assert.True(parts.Length == 4, $"Expected 4 columns but got {parts.Length} in: {line}");
        }
    }

    [Fact]
    public async Task GeneratesZeroRowsCorrectly()
    {
        var csv = "Name,Age\nAlice,30\nBob,25\n";
        var path = WriteTempCsv(csv);
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);
        var report = await svc.SummarizeAsync(path, useLlm: false);

        var outputPath = Path.Combine(Path.GetTempPath(), $"synth-{Guid.NewGuid():N}.csv");
        DataSynthesizer.GenerateCsv(report.Profile, 0, outputPath);

        var lines = File.ReadAllLines(outputPath);
        Assert.Single(lines); // Just header
    }

    [Fact]
    public async Task GeneratesLargeDataset()
    {
        var csv = "Value\n1\n2\n3\n4\n5\n";
        var path = WriteTempCsv(csv);
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);
        var report = await svc.SummarizeAsync(path, useLlm: false);

        var outputPath = Path.Combine(Path.GetTempPath(), $"synth-{Guid.NewGuid():N}.csv");
        DataSynthesizer.GenerateCsv(report.Profile, 10000, outputPath);

        var lines = File.ReadAllLines(outputPath);
        Assert.Equal(10001, lines.Length);
    }

    [Fact]
    public async Task HandlesDateTimeColumn()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Date");
        for (int i = 1; i <= 12; i++)
        {
            sb.AppendLine($"2024-{i:D2}-15");
        }

        var path = WriteTempCsv(sb.ToString());
        var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null);
        var report = await svc.SummarizeAsync(path, useLlm: false);

        var outputPath = Path.Combine(Path.GetTempPath(), $"synth-{Guid.NewGuid():N}.csv");
        DataSynthesizer.GenerateCsv(report.Profile, 20, outputPath);

        Assert.True(File.Exists(outputPath));
        var lines = File.ReadAllLines(outputPath);
        Assert.Equal(21, lines.Length);
    }

    private static string WriteTempCsv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ds-synth-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        return path;
    }
    
    /// <summary>
    /// Parse a CSV line properly handling quoted values that may contain commas.
    /// </summary>
    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var inQuotes = false;
        var current = new StringBuilder();
        
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            
            if (c == '"')
            {
                // Check for escaped quote
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++; // Skip next quote
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        
        result.Add(current.ToString());
        return result.ToArray();
    }
}
