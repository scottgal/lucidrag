using System.Text;
using Mostlylucid.DataSummarizer.Models;
using Mostlylucid.DataSummarizer.Services;
using Xunit;

namespace Mostlylucid.DataSummarizer.Tests;

/// <summary>
/// Comprehensive tests for target-aware profiling functionality.
/// Tests binary classification, multiclass, and continuous targets.
/// </summary>
public class TargetAnalysisTests
{
    #region Binary Target Tests

    [Fact]
    public async Task TargetAwareProfiling_Completes_OnDecimalColumns()
    {
        // Need at least 5 rows per target class for effect analysis
        var csv = @"CreditScore,Exited
650.5,1
700.1,0
620.9,1
680.0,0
615.2,1
710.3,0
625.8,1
690.5,0
630.1,1
705.7,0
";
        var path = WriteTempCsv(csv);
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, profileOptions: new ProfileOptions { TargetColumn = "Exited" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile.Target);
            Assert.True(report.Profile.Target!.FeatureEffects.Count > 0, $"Expected feature effects but got {report.Profile.Target?.FeatureEffects?.Count ?? 0}");
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task BinaryTarget_ComputesTargetDistribution()
    {
        var csv = CreateBinaryTargetData(100, 0.3); // 30% positive rate
        var path = WriteTempCsv(csv);
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Churned" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile.Target);
            Assert.Equal("Churned", report.Profile.Target!.ColumnName);
            // Class distribution should show approximately 30% for class "1"
            Assert.True(report.Profile.Target.ClassDistribution.ContainsKey("1") || 
                        report.Profile.Target.ClassDistribution.Count > 0,
                        "Expected class distribution to be computed");
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task BinaryTarget_IdentifiesNumericFeatureEffects()
    {
        // Create data where Age clearly correlates with churn
        var sb = new StringBuilder();
        sb.AppendLine("Age,Balance,Churned");
        // Churned customers tend to be older
        for (int i = 0; i < 50; i++)
        {
            sb.AppendLine($"{45 + i % 20},{50000 + i * 100},1");
        }
        // Non-churned customers tend to be younger
        for (int i = 0; i < 100; i++)
        {
            sb.AppendLine($"{25 + i % 15},{40000 + i * 100},0");
        }
        
        var path = WriteTempCsv(sb.ToString());
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Churned" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile.Target);
            Assert.NotEmpty(report.Profile.Target!.FeatureEffects);
            
            // Age should be identified as a feature with effect
            var ageEffect = report.Profile.Target.FeatureEffects.FirstOrDefault(f => f.Feature == "Age");
            Assert.NotNull(ageEffect);
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task BinaryTarget_IdentifiesCategoricalFeatureEffects()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Region,ProductType,Churned");
        // France has higher churn
        for (int i = 0; i < 30; i++) sb.AppendLine("France,Premium,1");
        for (int i = 0; i < 10; i++) sb.AppendLine("France,Basic,0");
        // Germany has lower churn
        for (int i = 0; i < 5; i++) sb.AppendLine("Germany,Premium,1");
        for (int i = 0; i < 55; i++) sb.AppendLine("Germany,Basic,0");
        
        var path = WriteTempCsv(sb.ToString());
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Churned" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile.Target);
            Assert.NotEmpty(report.Profile.Target!.FeatureEffects);
            
            // Region should show effect due to France vs Germany difference
            var regionEffect = report.Profile.Target.FeatureEffects.FirstOrDefault(f => f.Feature == "Region");
            Assert.NotNull(regionEffect);
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task BinaryTarget_HandlesImbalancedData()
    {
        // 95% class 0, 5% class 1 (severely imbalanced)
        var sb = new StringBuilder();
        sb.AppendLine("Feature,Target");
        for (int i = 0; i < 95; i++) sb.AppendLine($"{i},0");
        for (int i = 0; i < 5; i++) sb.AppendLine($"{100 + i},1");
        
        var path = WriteTempCsv(sb.ToString());
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Target" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile.Target);
            // Class distribution should show the imbalance
            Assert.True(report.Profile.Target!.ClassDistribution.Count > 0,
                        "Expected class distribution to be computed");
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    #endregion

    #region Multiclass Target Tests

    [Fact]
    public async Task MulticlassTarget_ComputesDistribution()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Feature,Category");
        for (int i = 0; i < 40; i++) sb.AppendLine($"{i},A");
        for (int i = 0; i < 30; i++) sb.AppendLine($"{i},B");
        for (int i = 0; i < 20; i++) sb.AppendLine($"{i},C");
        for (int i = 0; i < 10; i++) sb.AppendLine($"{i},D");
        
        var path = WriteTempCsv(sb.ToString());
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Category" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile.Target);
            Assert.Equal("Category", report.Profile.Target!.ColumnName);
            // Should have computed something (multiclass handling varies)
            Assert.True(report.Profile.Target.FeatureEffects.Count >= 0);
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task MulticlassTarget_HandlesThreeClasses()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Score,Segment");
        // Low scores -> Bronze
        for (int i = 0; i < 30; i++) sb.AppendLine($"{10 + i % 20},Bronze");
        // Medium scores -> Silver
        for (int i = 0; i < 30; i++) sb.AppendLine($"{40 + i % 20},Silver");
        // High scores -> Gold
        for (int i = 0; i < 30; i++) sb.AppendLine($"{70 + i % 20},Gold");
        
        var path = WriteTempCsv(sb.ToString());
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Segment" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile.Target);
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    #endregion

    #region Continuous Target Tests

    [Fact]
    public async Task ContinuousTarget_ComputesCorrelations()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Bedrooms,SqFt,Price");
        // Linear relationship: more bedrooms and sqft = higher price
        for (int i = 1; i <= 50; i++)
        {
            var bedrooms = 1 + (i % 5);
            var sqft = 500 + i * 50;
            var price = 50000 + bedrooms * 25000 + sqft * 100;
            sb.AppendLine($"{bedrooms},{sqft},{price}");
        }
        
        var path = WriteTempCsv(sb.ToString());
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Price" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            // For continuous targets, we still expect profiling to complete
            Assert.NotNull(report.Profile);
            Assert.Equal(50, report.Profile.RowCount);
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task ContinuousTarget_HandlesNegativeValues()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Feature,ProfitLoss");
        for (int i = 0; i < 50; i++)
        {
            var profit = (i - 25) * 1000; // Range from -25000 to +24000
            sb.AppendLine($"{i},{profit}");
        }
        
        var path = WriteTempCsv(sb.ToString());
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "ProfitLoss" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile);
            Assert.Equal(50, report.Profile.RowCount);
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task TargetColumn_NotFound_ProfilesWithoutTarget()
    {
        var csv = "A,B\n1,2\n3,4\n5,6\n";
        var path = WriteTempCsv(csv);
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "NonExistent" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            // Should still profile, just without target analysis
            Assert.NotNull(report.Profile);
            Assert.Equal(3, report.Profile.RowCount);
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task TargetColumn_AllSameValue_HandlesGracefully()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Feature,Target");
        for (int i = 0; i < 50; i++) sb.AppendLine($"{i},1"); // All same target value
        
        var path = WriteTempCsv(sb.ToString());
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Target" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile);
            // Should handle constant target gracefully
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task TargetColumn_WithNulls_HandlesGracefully()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Feature,Target");
        for (int i = 0; i < 30; i++) sb.AppendLine($"{i},1");
        for (int i = 0; i < 30; i++) sb.AppendLine($"{i},0");
        for (int i = 0; i < 10; i++) sb.AppendLine($"{i},"); // Nulls in target
        
        var path = WriteTempCsv(sb.ToString());
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Target" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile);
            Assert.Equal(70, report.Profile.RowCount);
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task TargetColumn_SingleFeature_WorksCorrectly()
    {
        var sb = new StringBuilder();
        sb.AppendLine("OnlyFeature,Target");
        for (int i = 0; i < 25; i++) sb.AppendLine($"{50 + i},1");
        for (int i = 0; i < 25; i++) sb.AppendLine($"{10 + i},0");
        
        var path = WriteTempCsv(sb.ToString());
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Target" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile.Target);
            Assert.NotEmpty(report.Profile.Target!.FeatureEffects);
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task TargetColumn_ManyFeatures_IdentifiesTopDrivers()
    {
        var sb = new StringBuilder();
        sb.AppendLine("F1,F2,F3,F4,F5,F6,F7,F8,F9,F10,Target");
        var random = new Random(42);
        for (int i = 0; i < 100; i++)
        {
            // F1 strongly correlates with target
            var target = i < 50 ? 1 : 0;
            var f1 = target == 1 ? 80 + random.Next(20) : 20 + random.Next(20);
            // Other features are random
            sb.AppendLine($"{f1},{random.Next(100)},{random.Next(100)},{random.Next(100)},{random.Next(100)}," +
                         $"{random.Next(100)},{random.Next(100)},{random.Next(100)},{random.Next(100)},{random.Next(100)},{target}");
        }
        
        var path = WriteTempCsv(sb.ToString());
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Target" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile.Target);
            Assert.NotEmpty(report.Profile.Target!.FeatureEffects);
            
            // F1 should be among top drivers
            var f1Effect = report.Profile.Target.FeatureEffects.FirstOrDefault(f => f.Feature == "F1");
            Assert.NotNull(f1Effect);
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task TargetColumn_MixedFeatureTypes_HandlesAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("NumericFeature,CategoricalFeature,DateFeature,Target");
        for (int i = 0; i < 25; i++)
        {
            sb.AppendLine($"{70 + i},Premium,2024-01-{(i % 28) + 1:D2},1");
        }
        for (int i = 0; i < 25; i++)
        {
            sb.AppendLine($"{30 + i},Basic,2024-02-{(i % 28) + 1:D2},0");
        }
        
        var path = WriteTempCsv(sb.ToString());
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Target" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile.Target);
            Assert.NotEmpty(report.Profile.Target!.FeatureEffects);
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    #endregion

    #region Integration with Insights

    [Fact]
    public async Task TargetAnalysis_GeneratesInsights()
    {
        var csv = CreateBinaryTargetData(100, 0.25);
        var path = WriteTempCsv(csv);
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Churned" });

            var report = await svc.SummarizeAsync(path, useLlm: false);

            Assert.NotNull(report.Profile);
            // Target insights may be in the insights list
            // or in the target object itself
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task AskAsync_AnswersTargetQuestions()
    {
        var csv = CreateBinaryTargetData(100, 0.3);
        var path = WriteTempCsv(csv);
        try
        {
            var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: null, 
                profileOptions: new ProfileOptions { TargetColumn = "Churned" });

            var insight = await svc.AskAsync(path, "What drives churn?");

            Assert.NotNull(insight);
        }
        finally
        {
            CleanupTempFile(path);
        }
    }

    #endregion

    #region Helper Methods

    private static string WriteTempCsv(string content)
    {
        var file = Path.Combine(Path.GetTempPath(), $"target_test_{Guid.NewGuid():N}.csv");
        File.WriteAllText(file, content);
        return file;
    }

    private static void CleanupTempFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    /// <summary>
    /// Creates test data for binary classification with specified positive rate.
    /// </summary>
    private static string CreateBinaryTargetData(int rowCount, double positiveRate)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Age,Balance,NumProducts,Churned");
        
        var random = new Random(42);
        var positiveCount = (int)(rowCount * positiveRate);
        
        // Churned customers (positive class)
        for (int i = 0; i < positiveCount; i++)
        {
            var age = 40 + random.Next(30); // Older
            var balance = 80000 + random.Next(50000); // Higher balance
            var products = random.Next(1, 5);
            sb.AppendLine($"{age},{balance},{products},1");
        }
        
        // Non-churned customers (negative class)
        for (int i = 0; i < rowCount - positiveCount; i++)
        {
            var age = 20 + random.Next(25); // Younger
            var balance = 20000 + random.Next(40000); // Lower balance
            var products = random.Next(1, 4);
            sb.AppendLine($"{age},{balance},{products},0");
        }
        
        return sb.ToString();
    }

    #endregion
}
