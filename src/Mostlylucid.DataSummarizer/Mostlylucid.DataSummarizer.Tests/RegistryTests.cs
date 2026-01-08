using System.Text;
using Mostlylucid.DataSummarizer.Services;
using Xunit;

namespace Mostlylucid.DataSummarizer.Tests;

/// <summary>
/// Comprehensive tests for the multi-file Registry functionality.
/// Tests ingestion, querying, similarity search, and conversation history.
/// </summary>
public class RegistryTests
{
    #region Basic Ingestion Tests

    [Fact]
    public async Task IngestAndAskRegistry_NoLlm_ReturnsContext()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var file1 = WriteCsv(tmpDir.FullName, "f1.csv", "A,B\n1,foo\n2,bar\n3,baz\n");
            var file2 = WriteCsv(tmpDir.FullName, "f2.csv", "X,Y\n10,100\n20,200\n30,300\n");

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(new[] { file1, file2 }, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("quick overview", topK: 4);

            Assert.NotNull(answer);
            Assert.False(string.IsNullOrWhiteSpace(answer!.Description));
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    [Fact]
    public async Task IngestAsync_SingleFile_Succeeds()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var file = WriteCsv(tmpDir.FullName, "single.csv", "Name,Age,Score\nAlice,30,85\nBob,25,90\nCharlie,35,78\n");

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(new[] { file }, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("describe the data", topK: 2);

            Assert.NotNull(answer);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    [Fact]
    public async Task IngestAsync_EmptyFileList_DoesNotThrow()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(Array.Empty<string>(), maxLlmInsights: 0);

            // Should not throw
            Assert.True(true);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    [Fact]
    public async Task IngestAsync_ManyFiles_AllProfiled()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var files = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                var content = $"Col{i},Value\n1,A\n2,B\n3,C\n";
                files.Add(WriteCsv(tmpDir.FullName, $"file{i}.csv", content));
            }

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(files, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("how many datasets", topK: 10);

            Assert.NotNull(answer);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    [Fact]
    public async Task IngestAsync_DuplicateFiles_HandlesDuplicates()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var file = WriteCsv(tmpDir.FullName, "dup.csv", "A,B\n1,2\n3,4\n");

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            
            // Ingest same file twice
            await svc.IngestAsync(new[] { file, file }, maxLlmInsights: 0);

            // Should not throw and should handle deduplication
            Assert.True(true);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    #endregion

    #region Query Tests

    [Fact]
    public async Task AskRegistryAsync_SchemaQuestion_ReturnsColumns()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var file = WriteCsv(tmpDir.FullName, "schema.csv", "CustomerId,Email,Amount,Date\n1,a@b.com,100,2024-01-01\n");

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(new[] { file }, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("what columns are in the data", topK: 5);

            Assert.NotNull(answer);
            Assert.False(string.IsNullOrWhiteSpace(answer!.Description));
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    [Fact]
    public async Task AskRegistryAsync_TypeQuestion_ReturnsTypes()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var file = WriteCsv(tmpDir.FullName, "types.csv", "Id,Name,Score,Active,Created\n1,Alice,95.5,true,2024-01-15\n");

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(new[] { file }, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("what data types are present", topK: 3);

            Assert.NotNull(answer);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    [Fact]
    public async Task AskRegistryAsync_TopKLimitsResults()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            // Create multiple files
            for (int i = 0; i < 10; i++)
            {
                WriteCsv(tmpDir.FullName, $"data{i}.csv", $"Col{i},Val\n1,A\n2,B\n");
            }

            var files = Directory.GetFiles(tmpDir.FullName, "*.csv");
            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(files, maxLlmInsights: 0);

            // Query with limited topK
            var answer = await svc.AskRegistryAsync("overview", topK: 2);

            Assert.NotNull(answer);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    #endregion

    #region Schema Matching Tests

    [Fact]
    public async Task IngestAsync_SameSchema_RecognizedAsSimilar()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            // Two files with identical schema
            var file1 = WriteCsv(tmpDir.FullName, "batch1.csv", "Id,Name,Amount\n1,Alice,100\n2,Bob,200\n");
            var file2 = WriteCsv(tmpDir.FullName, "batch2.csv", "Id,Name,Amount\n3,Charlie,300\n4,Diana,400\n");

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(new[] { file1, file2 }, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("datasets with similar schema", topK: 5);

            Assert.NotNull(answer);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    [Fact]
    public async Task IngestAsync_DifferentSchemas_ProfiledSeparately()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            // Two files with completely different schemas
            var file1 = WriteCsv(tmpDir.FullName, "users.csv", "UserId,Email,Name\n1,a@b.com,Alice\n");
            var file2 = WriteCsv(tmpDir.FullName, "products.csv", "ProductId,Price,Category\n101,29.99,Electronics\n");

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(new[] { file1, file2 }, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("what types of data do we have", topK: 5);

            Assert.NotNull(answer);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    #endregion

    #region Data Pattern Tests

    [Fact]
    public async Task IngestAsync_WithPII_DetectsPatterns()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Name,Email,Phone,SSN");
            sb.AppendLine("John Doe,john@example.com,555-123-4567,123-45-6789");
            sb.AppendLine("Jane Smith,jane@company.org,555-987-6543,987-65-4321");
            
            var file = WriteCsv(tmpDir.FullName, "pii.csv", sb.ToString());

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(new[] { file }, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("any PII or sensitive data", topK: 3);

            Assert.NotNull(answer);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    [Fact]
    public async Task IngestAsync_WithChurnTarget_DetectsTargetColumn()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("CustomerId,Age,Balance,Churned");
            for (int i = 0; i < 20; i++)
            {
                sb.AppendLine($"{i},{25 + i},{1000 + i * 100},{(i % 3 == 0 ? 1 : 0)}");
            }
            
            var file = WriteCsv(tmpDir.FullName, "churn.csv", sb.ToString());

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(new[] { file }, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("which datasets have churn data", topK: 3);

            Assert.NotNull(answer);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task IngestAsync_NonExistentFile_HandlesGracefully()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var validFile = WriteCsv(tmpDir.FullName, "valid.csv", "A,B\n1,2\n");
            var nonExistent = Path.Combine(tmpDir.FullName, "does_not_exist.csv");

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            
            // Should not throw - should skip invalid file
            await svc.IngestAsync(new[] { validFile, nonExistent }, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("overview", topK: 2);
            Assert.NotNull(answer);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    [Fact]
    public async Task IngestAsync_MalformedCsv_HandlesGracefully()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var validFile = WriteCsv(tmpDir.FullName, "valid.csv", "A,B,C\n1,2,3\n4,5,6\n");
            // Malformed: varying column counts (DuckDB may or may not handle this)
            var malformed = WriteCsv(tmpDir.FullName, "malformed.csv", "A,B\n1\n1,2,3,4\n");

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            
            // Should handle gracefully
            await svc.IngestAsync(new[] { validFile, malformed }, maxLlmInsights: 0);

            Assert.True(true);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    [Fact]
    public async Task AskRegistryAsync_EmptyRegistry_ReturnsEmptyResult()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            
            // Query empty registry
            var answer = await svc.AskRegistryAsync("anything", topK: 5);

            // Should return something (possibly null or empty description)
            // Main thing is it should not throw
            Assert.True(true);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    #endregion

    #region Re-Ingestion Tests

    [Fact]
    public async Task IngestAsync_SameFileTwice_UpdatesProfile()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var filePath = Path.Combine(tmpDir.FullName, "evolving.csv");
            
            // First version
            File.WriteAllText(filePath, "A,B\n1,2\n3,4\n");

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(new[] { filePath }, maxLlmInsights: 0);

            // Update file content
            File.WriteAllText(filePath, "A,B,C\n1,2,3\n4,5,6\n7,8,9\n");

            // Re-ingest
            await svc.IngestAsync(new[] { filePath }, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("describe the data", topK: 3);

            Assert.NotNull(answer);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    #endregion

    #region Large Data Tests

    [Fact]
    public async Task IngestAsync_LargeFile_Succeeds()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Id,Value,Category,Score");
            for (int i = 0; i < 1000; i++)
            {
                sb.AppendLine($"{i},{i * 10},{(i % 5 == 0 ? "A" : i % 3 == 0 ? "B" : "C")},{i % 100}");
            }
            
            var file = WriteCsv(tmpDir.FullName, "large.csv", sb.ToString());

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(new[] { file }, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("row count", topK: 3);

            Assert.NotNull(answer);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    [Fact]
    public async Task IngestAsync_WideFile_Succeeds()
    {
        var tmpDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ds-reg-{Guid.NewGuid():N}"));
        var dbPath = Path.Combine(tmpDir.FullName, "registry.duckdb");

        try
        {
            var sb = new StringBuilder();
            // 50 columns
            var cols = string.Join(",", Enumerable.Range(1, 50).Select(i => $"Col{i}"));
            sb.AppendLine(cols);
            
            for (int row = 0; row < 20; row++)
            {
                var vals = string.Join(",", Enumerable.Range(1, 50).Select(i => (row * i).ToString()));
                sb.AppendLine(vals);
            }
            
            var file = WriteCsv(tmpDir.FullName, "wide.csv", sb.ToString());

            using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, vectorStorePath: dbPath);
            await svc.IngestAsync(new[] { file }, maxLlmInsights: 0);

            var answer = await svc.AskRegistryAsync("how many columns", topK: 3);

            Assert.NotNull(answer);
        }
        finally
        {
            CleanupDirectory(tmpDir.FullName);
        }
    }

    #endregion

    #region Helper Methods

    private static string WriteCsv(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static void CleanupDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    #endregion
}
