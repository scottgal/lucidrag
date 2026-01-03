using System.Diagnostics;
using Microsoft.Extensions.FileSystemGlobbing;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Spectre.Console;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Tracks whether we're inside a batch processing context to prevent nested Spectre progress bars.
/// </summary>
internal static class BatchContextTracker
{
    [ThreadStatic]
    private static bool _isInContext;
    
    public static bool IsInContext => _isInContext;
    
    public static IDisposable Enter()
    {
        _isInContext = true;
        return new BatchContextGuard();
    }
    
    private class BatchContextGuard : IDisposable
    {
        public void Dispose() => _isInContext = false;
    }
}

/// <summary>
/// Handles batch processing of multiple documents
/// </summary>
public class BatchProcessor
{
    private readonly BatchConfig _config;
    private readonly string? _errorLogPath;
    private readonly DocumentSummarizer _summarizer;
    private readonly bool _verbose;

    public BatchProcessor(DocumentSummarizer summarizer, BatchConfig config, bool verbose = false,
        string? errorLogPath = null)
    {
        _summarizer = summarizer;
        _config = config;
        _verbose = verbose;
        _errorLogPath = errorLogPath;
    }

    /// <summary>
    /// Process all documents in a directory, saving each immediately
    /// </summary>
    public async Task<BatchSummary> ProcessDirectoryAsync(
        string directoryPath,
        SummarizationMode mode = SummarizationMode.MapReduce,
        string? focus = null,
        Func<BatchResult, Task>? onFileCompleted = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");

        var sw = Stopwatch.StartNew();

        var successCount = 0;
        var failureCount = 0;
        var failedFiles = new List<(string Path, string Error, string? StackTrace)>();

        var files = FindMatchingFiles(directoryPath);
        var totalFiles = files.Count;

        AnsiConsole.MarkupLine($"[cyan]Found[/] [yellow]{totalFiles}[/] [cyan]files to process[/]");
        AnsiConsole.WriteLine();

        if (totalFiles == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No files found matching criteria[/]");
            return new BatchSummary(0, 0, 0, new List<BatchResult>(), sw.Elapsed);
        }

        using var _ = BatchContextTracker.Enter();

        // Use Spectre progress for batch processing
        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var overallTask = ctx.AddTask("[cyan]Batch Processing[/]", maxValue: totalFiles);
                
                for (var i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    if (cancellationToken.IsCancellationRequested) break;

                    var fileName = Path.GetFileName(file);
                    overallTask.Description = $"[cyan]Processing:[/] {Markup.Escape(TruncateFileName(fileName, 40))}";

                    var result = await ProcessFileAsync(file, mode, focus, cancellationToken);

                    if (onFileCompleted != null)
                    {
                        try
                        {
                            await onFileCompleted(result);
                        }
                        catch (Exception ex)
                        {
                            await LogErrorAsync(file, $"Failed to save output: {ex.Message}", ex.StackTrace);
                        }
                    }

                    if (result.Success)
                    {
                        successCount++;
                        SpectreProgressService.WriteBatchProgress(i + 1, totalFiles, fileName, true);
                        if (_verbose) 
                            AnsiConsole.MarkupLine($"  [dim]Completed in {result.ProcessingTime.TotalSeconds:F1}s[/]");
                    }
                    else
                    {
                        failureCount++;
                        SpectreProgressService.WriteBatchProgress(i + 1, totalFiles, fileName, false);
                        AnsiConsole.MarkupLine($"  [red]Error:[/] {Markup.Escape(result.Error ?? "Unknown error")}");
                        if (result.Error != null)
                        {
                            failedFiles.Add((file, result.Error, result.StackTrace));
                            await LogErrorAsync(file, result.Error, result.StackTrace);
                        }
                    }

                    overallTask.Increment(1);
                    result = null; // Allow GC

                    if (failureCount > 0 && !_config.ContinueOnError) break;
                }
                
                overallTask.Description = "[green]Batch processing complete[/]";
                overallTask.StopTask();
            });

        sw.Stop();

        AnsiConsole.WriteLine();
        DisplayBatchSummary(totalFiles, successCount, failureCount, failedFiles, sw.Elapsed);

        return new BatchSummary(
            totalFiles,
            successCount,
            failureCount,
            failedFiles.Select(f => new BatchResult(f.Path, false, null, f.Error, TimeSpan.Zero)).ToList(),
            sw.Elapsed);
    }

    private static void DisplayBatchSummary(int total, int success, int failed,
        List<(string Path, string Error, string? StackTrace)> failedFiles, TimeSpan elapsed)
    {
        // Use Spectre table for summary
        var summaryTable = new Table()
            .Border(TableBorder.Double)
            .BorderColor(Color.Blue)
            .Title("[cyan]Batch Processing Summary[/]");
        
        summaryTable.AddColumn(new TableColumn("[blue]Metric[/]").Centered());
        summaryTable.AddColumn(new TableColumn("[blue]Value[/]").RightAligned());
        
        summaryTable.AddRow("[cyan]Total Files[/]", $"[white]{total}[/]");
        summaryTable.AddRow("[green]Successful[/]", $"[green]{success}[/]");
        summaryTable.AddRow(failed > 0 ? "[red]Failed[/]" : "[dim]Failed[/]", 
            failed > 0 ? $"[red]{failed}[/]" : $"[dim]{failed}[/]");
        
        var successRate = total > 0 ? (double)success / total * 100 : 0;
        var rateColor = successRate >= 90 ? "green" : successRate >= 70 ? "yellow" : "red";
        summaryTable.AddRow("[cyan]Success Rate[/]", $"[{rateColor}]{successRate:F1}%[/]");
        summaryTable.AddRow("[cyan]Duration[/]", $"[white]{elapsed.TotalMinutes:F1} minutes[/]");
        
        AnsiConsole.Write(summaryTable);

        if (failedFiles.Count > 0)
        {
            AnsiConsole.WriteLine();
            
            var failedTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Red)
                .Title("[red]Failed Files[/]");
            
            failedTable.AddColumn(new TableColumn("[red]File[/]"));
            failedTable.AddColumn(new TableColumn("[red]Error[/]"));
            
            foreach (var (path, error, _) in failedFiles.Take(20))
            {
                var fileName = Path.GetFileName(path);
                var shortError = error.Length > 50 ? error[..47] + "..." : error;
                failedTable.AddRow(
                    Markup.Escape(TruncateFileName(fileName, 30)),
                    Markup.Escape(shortError));
            }

            if (failedFiles.Count > 20)
            {
                failedTable.AddRow($"[dim]... and {failedFiles.Count - 20} more[/]", "");
            }
            
            AnsiConsole.Write(failedTable);
        }
    }

    private async Task LogErrorAsync(string filePath, string error, string? stackTrace)
    {
        if (!string.IsNullOrEmpty(_errorLogPath))
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var logEntry = $"[{timestamp}] {filePath}\n  Error: {error}\n";
                if (!string.IsNullOrEmpty(stackTrace)) logEntry += $"  Stack: {stackTrace}\n";
                logEntry += "\n";

                await File.AppendAllTextAsync(_errorLogPath, logEntry);
            }
            catch
            {
                // Ignore logging errors
            }
        }
    }

    private static string TruncateFileName(string fileName, int maxLength)
    {
        if (fileName.Length <= maxLength) return fileName;
        var ext = Path.GetExtension(fileName);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var truncatedLength = maxLength - ext.Length - 3;
        if (truncatedLength < 5) return fileName[..maxLength];
        return nameWithoutExt[..truncatedLength] + "..." + ext;
    }

    private async Task<BatchResult> ProcessFileAsync(
        string filePath,
        SummarizationMode mode,
        string? focus,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var summary = await _summarizer.SummarizeAsync(filePath, mode, focus);
            sw.Stop();
            return new BatchResult(filePath, true, summary, null, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new BatchResult(filePath, false, null, ex.Message, sw.Elapsed, ex.StackTrace);
        }
    }

    private List<string> FindMatchingFiles(string directoryPath)
    {
        var searchOption = _config.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var matchedFiles = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*", searchOption))
        {
            if (_config.FileExtensions.Count == 0 ||
                _config.FileExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            {
                var relativePath = Path.GetRelativePath(directoryPath, file);
                var excluded = _config.ExcludePatterns.Any(p =>
                    relativePath.Contains(p, StringComparison.OrdinalIgnoreCase));

                if (!excluded)
                {
                    // Skip summary output files to prevent infinite loops
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.EndsWith("_summary", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith(".summary", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_verbose)
                            Spectre.Console.AnsiConsole.MarkupLine($"[dim]Skipping summary file: {Path.GetFileName(file)}[/]");
                        continue;
                    }
                    
                    matchedFiles.Add(file);
                }
            }
        }

        return matchedFiles;
    }
}
