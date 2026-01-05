using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using LucidRAG.ImageCli.Services.OutputFormatters;
using Mostlylucid.DocSummarizer.Images.Services;

namespace LucidRAG.ImageCli.Services;

/// <summary>
/// Service for parallel batch processing of images with glob pattern support.
/// </summary>
public class ImageBatchProcessor
{
    private readonly EscalationService _escalationService;
    private readonly ILogger<ImageBatchProcessor> _logger;

    private static readonly string[] SupportedExtensions =
    [
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".tif"
    ];

    public ImageBatchProcessor(
        EscalationService escalationService,
        ILogger<ImageBatchProcessor> logger)
    {
        _escalationService = escalationService;
        _logger = logger;
    }

    /// <summary>
    /// Process a batch of images from a directory with glob pattern filtering.
    /// </summary>
    public async Task<BatchProcessingResult> ProcessBatchAsync(
        string directory,
        string globPattern = "**/*",
        bool recursive = true,
        int maxParallel = 0,
        bool enableAutoEscalation = true,
        bool enableOcr = true,
        Func<EscalationResult, bool>? filter = null,
        IProgress<BatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (maxParallel <= 0)
        {
            maxParallel = Environment.ProcessorCount;
        }

        // Find all matching image files
        var imageFiles = FindImageFiles(directory, globPattern, recursive);

        if (!imageFiles.Any())
        {
            _logger.LogWarning("No images found in {Directory} matching pattern {Pattern}",
                directory, globPattern);
            return new BatchProcessingResult([], directory, globPattern);
        }

        _logger.LogInformation("Found {Count} images to process with {Workers} parallel workers",
            imageFiles.Count, maxParallel);

        // Process in parallel using escalation service
        var results = await _escalationService.AnalyzeBatchAsync(
            imageFiles,
            enableAutoEscalation,
            enableOcr,
            maxParallel,
            progress,
            ct);

        // Apply optional filter
        if (filter != null)
        {
            results = results.Where(filter).ToList();
        }

        // Convert to ImageAnalysisResult format
        var analysisResults = results.Select(r => new ImageAnalysisResult(
            FilePath: GetRelativePath(directory, r.FilePath),
            Profile: r.Profile,
            LlmCaption: r.LlmCaption,
            ExtractedText: r.ExtractedText,
            Error: null,
            WasEscalated: r.WasEscalated,
            GifMotion: r.GifMotion
        )).ToList();

        return new BatchProcessingResult(analysisResults, directory, globPattern);
    }

    /// <summary>
    /// Find all image files matching the glob pattern.
    /// </summary>
    private List<string> FindImageFiles(string directory, string pattern, bool recursive)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);

        // Add pattern
        matcher.AddInclude(pattern);

        // Add supported extensions if pattern doesn't specify extension
        if (!pattern.Contains('.'))
        {
            foreach (var ext in SupportedExtensions)
            {
                matcher.AddInclude($"{pattern}{ext}");
            }
        }

        var directoryInfo = new DirectoryInfo(directory);
        if (!directoryInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Directory not found: {directory}");
        }

        var result = matcher.Execute(
            new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(directoryInfo));

        var files = result.Files
            .Select(f => Path.Combine(directory, f.Path))
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        return files;
    }

    /// <summary>
    /// Get relative path from base directory.
    /// </summary>
    private static string GetRelativePath(string baseDirectory, string fullPath)
    {
        var baseUri = new Uri(Path.GetFullPath(baseDirectory) + Path.DirectorySeparatorChar);
        var fullUri = new Uri(Path.GetFullPath(fullPath));
        return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString());
    }

    /// <summary>
    /// Export batch results to CSV format.
    /// </summary>
    public async Task ExportToCsvAsync(
        BatchProcessingResult results,
        string outputPath,
        CancellationToken ct = default)
    {
        var csv = new System.Text.StringBuilder();

        // Header
        csv.AppendLine("File,Type,TypeConfidence,Width,Height,AspectRatio,EdgeDensity,Sharpness,TextLikeliness,MeanSaturation,IsGrayscale,WasEscalated,DominantColor1,DominantColor2,DominantColor3");

        // Rows
        foreach (var result in results.Results.Where(r => r.Profile != null))
        {
            var p = result.Profile!;
            var colors = p.DominantColors?.Take(3).ToList() ?? [];

            csv.AppendLine(string.Join(",",
                EscapeCsv(Path.GetFileName(result.FilePath)),
                p.DetectedType,
                p.TypeConfidence.ToString("F3"),
                p.Width,
                p.Height,
                p.AspectRatio.ToString("F3"),
                p.EdgeDensity.ToString("F3"),
                p.LaplacianVariance.ToString("F1"),
                p.TextLikeliness.ToString("F3"),
                p.MeanSaturation.ToString("F3"),
                p.IsMostlyGrayscale,
                result.WasEscalated,
                colors.Count > 0 ? $"{colors[0].Name}({colors[0].Hex})" : "",
                colors.Count > 1 ? $"{colors[1].Name}({colors[1].Hex})" : "",
                colors.Count > 2 ? $"{colors[2].Name}({colors[2].Hex})" : ""
            ));
        }

        await File.WriteAllTextAsync(outputPath, csv.ToString(), ct);
        _logger.LogInformation("Exported {Count} results to CSV: {Path}",
            results.Results.Count(r => r.Profile != null), outputPath);
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }
}

/// <summary>
/// Result of batch processing operation.
/// </summary>
public record BatchProcessingResult(
    List<ImageAnalysisResult> Results,
    string Directory,
    string Pattern);
