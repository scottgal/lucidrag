using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Core.Models;

namespace Mostlylucid.DocSummarizer.Core.Services;

/// <summary>
/// Base class for Python-based table extractors using subprocess
/// </summary>
public abstract class PythonTableExtractor : ITableExtractor
{
    protected readonly ILogger Logger;
    protected readonly string PythonExecutable;

    protected PythonTableExtractor(ILogger logger, string? pythonExecutable = null)
    {
        Logger = logger;
        PythonExecutable = pythonExecutable ?? FindPythonExecutable();
    }

    public abstract IReadOnlyList<string> SupportedExtensions { get; }
    public abstract string Name { get; }
    protected abstract string PythonScriptName { get; }
    protected abstract string[] RequiredPythonPackages { get; }

    /// <summary>
    /// Check if Python and required packages are available
    /// </summary>
    public virtual async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            // Check Python version
            var versionCheck = await RunPythonCommandAsync("--version", ct);
            if (!versionCheck.Success)
            {
                Logger.LogWarning("{Name}: Python not found at {Path}", Name, PythonExecutable);
                return false;
            }

            // Check required packages
            foreach (var package in RequiredPythonPackages)
            {
                var importCheck = await RunPythonCommandAsync($"-c \"import {package}\"", ct);
                if (!importCheck.Success)
                {
                    Logger.LogWarning("{Name}: Required package '{Package}' not installed", Name, package);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Name}: Error checking availability", Name);
            return false;
        }
    }

    public async Task<TableExtractionResult> ExtractTablesAsync(
        string filePath,
        TableExtractionOptions? options = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        options ??= new TableExtractionOptions();

        try
        {
            // Get Python script path
            var scriptPath = GetPythonScriptPath();
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"Python script not found: {scriptPath}");
            }

            // Prepare arguments
            var args = PrepareArguments(filePath, options);

            // Run Python script
            var result = await RunPythonScriptAsync(scriptPath, args, ct);

            if (!result.Success)
            {
                return new TableExtractionResult
                {
                    SourcePath = filePath,
                    Tables = new List<ExtractedTable>(),
                    Duration = stopwatch.Elapsed,
                    Errors = new List<string> { result.Error ?? "Unknown error" }
                };
            }

            // Parse JSON output
            var tables = ParseJsonOutput(result.Output, filePath);

            stopwatch.Stop();

            return new TableExtractionResult
            {
                SourcePath = filePath,
                Tables = tables,
                TotalPages = options.Pages?.Count ?? 0,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Name}: Error extracting tables from {Path}", Name, filePath);

            return new TableExtractionResult
            {
                SourcePath = filePath,
                Tables = new List<ExtractedTable>(),
                Duration = stopwatch.Elapsed,
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Prepare command-line arguments for Python script
    /// </summary>
    protected virtual string PrepareArguments(string filePath, TableExtractionOptions options)
    {
        var sb = new StringBuilder();
        sb.Append($"--input \"{filePath}\" ");

        if (options.Pages != null && options.Pages.Count > 0)
        {
            sb.Append($"--pages {string.Join(",", options.Pages)} ");
        }

        if (options.MinRows > 0)
        {
            sb.Append($"--min-rows {options.MinRows} ");
        }

        if (options.MinColumns > 0)
        {
            sb.Append($"--min-cols {options.MinColumns} ");
        }

        if (options.EnableOcr)
        {
            sb.Append("--ocr ");
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Parse JSON output from Python script
    /// </summary>
    protected virtual List<ExtractedTable> ParseJsonOutput(string json, string sourcePath)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var tables = JsonSerializer.Deserialize<List<PythonTableOutput>>(json, options);
            if (tables == null) return new List<ExtractedTable>();

            return tables.Select((t, idx) => new ExtractedTable
            {
                Id = $"{Path.GetFileNameWithoutExtension(sourcePath)}_table_{idx + 1}",
                SourcePath = sourcePath,
                PageOrSection = t.Page ?? 0,
                TableNumber = idx + 1,
                BoundingBox = t.BoundingBox,
                Rows = t.Rows.Select(row =>
                    row.Select(cell => TableCell.FromText(cell)).ToList()
                ).ToList(),
                HasHeader = t.HasHeader ?? true,
                ColumnNames = t.HasHeader == true && t.Rows.Count > 0
                    ? t.Rows[0].ToList()
                    : null,
                Confidence = t.Confidence,
                ExtractionMethod = Name,
                Metadata = t.Metadata
            }).ToList();
        }
        catch (JsonException ex)
        {
            Logger.LogError(ex, "{Name}: Error parsing JSON output", Name);
            return new List<ExtractedTable>();
        }
    }

    /// <summary>
    /// Get path to Python script
    /// </summary>
    protected virtual string GetPythonScriptPath()
    {
        // Scripts are embedded in Resources folder
        var assemblyPath = Path.GetDirectoryName(typeof(PythonTableExtractor).Assembly.Location);
        return Path.Combine(assemblyPath!, "Resources", "TableExtractors", PythonScriptName);
    }

    /// <summary>
    /// Run Python script with arguments
    /// </summary>
    protected async Task<(bool Success, string Output, string? Error)> RunPythonScriptAsync(
        string scriptPath,
        string arguments,
        CancellationToken ct)
    {
        var fullArgs = $"\"{scriptPath}\" {arguments}";
        return await RunPythonCommandAsync(fullArgs, ct);
    }

    /// <summary>
    /// Run Python command
    /// </summary>
    protected async Task<(bool Success, string Output, string? Error)> RunPythonCommandAsync(
        string arguments,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PythonExecutable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        var output = outputBuilder.ToString();
        var error = errorBuilder.ToString();

        return (process.ExitCode == 0, output, string.IsNullOrEmpty(error) ? null : error);
    }

    /// <summary>
    /// Find Python executable
    /// </summary>
    private static string FindPythonExecutable()
    {
        // Try common Python locations
        var candidates = new[] { "python3", "python", "py" };

        foreach (var candidate in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    process.WaitForExit(1000);
                    if (process.ExitCode == 0)
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // Try next candidate
            }
        }

        return "python3"; // Default fallback
    }

    /// <summary>
    /// Python script JSON output format
    /// </summary>
    protected class PythonTableOutput
    {
        public int? Page { get; set; }
        public float[]? BoundingBox { get; set; }
        public required List<List<string>> Rows { get; set; }
        public bool? HasHeader { get; set; }
        public double? Confidence { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }
}
