using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;

namespace LucidRAG.ImageCli.Services;

/// <summary>
/// Service for finding duplicate and similar images using perceptual hashing.
/// </summary>
public class DeduplicationService
{
    private readonly IImageAnalyzer _imageAnalyzer;
    private readonly ILogger<DeduplicationService> _logger;

    public DeduplicationService(
        IImageAnalyzer imageAnalyzer,
        ILogger<DeduplicationService> logger)
    {
        _imageAnalyzer = imageAnalyzer;
        _logger = logger;
    }

    /// <summary>
    /// Find duplicate and similar images in a collection.
    /// </summary>
    /// <param name="imagePaths">Paths to images to analyze</param>
    /// <param name="hammingThreshold">Maximum Hamming distance to consider similar (0-64, lower = more similar)</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Groups of duplicate/similar images</returns>
    public async Task<DeduplicationResult> FindDuplicatesAsync(
        IEnumerable<string> imagePaths,
        int hammingThreshold = 5,
        IProgress<(int Processed, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var imagePathsList = imagePaths.ToList();
        var totalCount = imagePathsList.Count;

        _logger.LogInformation("Calculating perceptual hashes for {Count} images", totalCount);

        // Step 1: Calculate perceptual hashes for all images in parallel
        var hashMap = new ConcurrentDictionary<string, (ulong Hash, long FileSize)>();
        var processedCount = 0;

        var hashTasks = imagePathsList.Select(async path =>
        {
            try
            {
                var hashStr = await _imageAnalyzer.GeneratePerceptualHashAsync(path, ct);
                var hash = ConvertHashToUInt64(hashStr);
                var fileSize = new FileInfo(path).Length;

                hashMap[path] = (hash, fileSize);

                var processed = Interlocked.Increment(ref processedCount);
                progress?.Report((processed, totalCount));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate hash for {Path}", path);
                Interlocked.Increment(ref processedCount);
            }
        });

        await Task.WhenAll(hashTasks);

        _logger.LogInformation("Comparing {Count} hashes to find duplicates (threshold: {Threshold})",
            hashMap.Count, hammingThreshold);

        // Step 2: Find duplicate groups using Hamming distance
        var groups = FindDuplicateGroups(hashMap, hammingThreshold);

        // Step 3: Calculate statistics
        var duplicateCount = groups.Sum(g => g.Images.Count - 1); // All but one per group
        var wastedSpace = groups.Sum(g =>
        {
            var sizes = g.Images.Select(i => i.FileSize).ToList();
            var keepSize = sizes.Min();
            return sizes.Sum() - keepSize;
        });

        _logger.LogInformation("Found {GroupCount} duplicate groups containing {DuplicateCount} duplicates, wasting {Space:N0} bytes",
            groups.Count, duplicateCount, wastedSpace);

        return new DeduplicationResult(
            Groups: groups,
            TotalImages: totalCount,
            TotalDuplicates: duplicateCount,
            WastedSpace: wastedSpace);
    }

    /// <summary>
    /// Find groups of similar images based on Hamming distance.
    /// </summary>
    private List<DuplicateGroup> FindDuplicateGroups(
        ConcurrentDictionary<string, (ulong Hash, long FileSize)> hashMap,
        int threshold)
    {
        var groups = new List<DuplicateGroup>();
        var processed = new HashSet<string>();
        var paths = hashMap.Keys.ToList();

        for (int i = 0; i < paths.Count; i++)
        {
            var path1 = paths[i];
            if (processed.Contains(path1))
                continue;

            var (hash1, size1) = hashMap[path1];
            var groupImages = new List<DuplicateImage> { new(path1, hash1, size1, 0) };
            processed.Add(path1);

            // Find all similar images
            for (int j = i + 1; j < paths.Count; j++)
            {
                var path2 = paths[j];
                if (processed.Contains(path2))
                    continue;

                var (hash2, size2) = hashMap[path2];
                var distance = CalculateHammingDistance(hash1, hash2);

                if (distance <= threshold)
                {
                    groupImages.Add(new DuplicateImage(path2, hash2, size2, distance));
                    processed.Add(path2);
                }
            }

            // Only create a group if we found duplicates
            if (groupImages.Count > 1)
            {
                // Sort by file size (recommend keeping smallest)
                groupImages = groupImages.OrderBy(i => i.FileSize).ToList();
                groups.Add(new DuplicateGroup(groupImages, groupImages[0].Hash));
            }
        }

        return groups.OrderByDescending(g => g.Images.Count).ToList();
    }

    /// <summary>
    /// Calculate Hamming distance between two perceptual hashes.
    /// Returns number of differing bits (0-64).
    /// </summary>
    private static int CalculateHammingDistance(ulong hash1, ulong hash2)
    {
        var xor = hash1 ^ hash2;
        return System.Numerics.BitOperations.PopCount(xor);
    }

    /// <summary>
    /// Convert hexadecimal hash string to UInt64.
    /// </summary>
    private static ulong ConvertHashToUInt64(string hashHex)
    {
        // Take first 16 characters (64 bits)
        var hex = hashHex.Length > 16 ? hashHex[..16] : hashHex.PadRight(16, '0');
        return Convert.ToUInt64(hex, 16);
    }

    /// <summary>
    /// Perform deduplication action on a group.
    /// </summary>
    public async Task<DeduplicationActionResult> PerformActionAsync(
        DuplicateGroup group,
        DeduplicationAction action,
        string? moveToDirectory = null,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var results = new List<string>();

        switch (action)
        {
            case DeduplicationAction.Report:
                // Nothing to do, just reporting
                return new DeduplicationActionResult(true, results, "Report only");

            case DeduplicationAction.Move:
                if (string.IsNullOrWhiteSpace(moveToDirectory))
                {
                    return new DeduplicationActionResult(false, results, "Move directory not specified");
                }

                if (!dryRun && !Directory.Exists(moveToDirectory))
                {
                    Directory.CreateDirectory(moveToDirectory);
                }

                // Keep the first (smallest) file, move the rest
                foreach (var duplicate in group.Images.Skip(1))
                {
                    var fileName = Path.GetFileName(duplicate.FilePath);
                    var destPath = Path.Combine(moveToDirectory, fileName);

                    if (dryRun)
                    {
                        results.Add($"Would move: {duplicate.FilePath} -> {destPath}");
                    }
                    else
                    {
                        File.Move(duplicate.FilePath, destPath, overwrite: true);
                        results.Add($"Moved: {duplicate.FilePath} -> {destPath}");
                        _logger.LogInformation("Moved duplicate {Source} to {Dest}", duplicate.FilePath, destPath);
                    }
                }
                break;

            case DeduplicationAction.Delete:
                // Keep the first (smallest) file, delete the rest
                foreach (var duplicate in group.Images.Skip(1))
                {
                    if (dryRun)
                    {
                        results.Add($"Would delete: {duplicate.FilePath} ({duplicate.FileSize:N0} bytes)");
                    }
                    else
                    {
                        File.Delete(duplicate.FilePath);
                        results.Add($"Deleted: {duplicate.FilePath} ({duplicate.FileSize:N0} bytes)");
                        _logger.LogWarning("Deleted duplicate {Path}", duplicate.FilePath);
                    }
                }
                break;
        }

        await Task.CompletedTask;
        return new DeduplicationActionResult(true, results,
            $"Processed {results.Count} files{(dryRun ? " (dry run)" : "")}");
    }
}

/// <summary>
/// Result of deduplication analysis.
/// </summary>
public record DeduplicationResult(
    List<DuplicateGroup> Groups,
    int TotalImages,
    int TotalDuplicates,
    long WastedSpace);

/// <summary>
/// A group of duplicate or similar images.
/// </summary>
public record DuplicateGroup(
    List<DuplicateImage> Images,
    ulong RepresentativeHash);

/// <summary>
/// Information about a duplicate image.
/// </summary>
public record DuplicateImage(
    string FilePath,
    ulong Hash,
    long FileSize,
    int HammingDistance);

/// <summary>
/// Actions that can be performed on duplicates.
/// </summary>
public enum DeduplicationAction
{
    Report,
    Move,
    Delete
}

/// <summary>
/// Result of a deduplication action.
/// </summary>
public record DeduplicationActionResult(
    bool Success,
    List<string> Details,
    string Message);
