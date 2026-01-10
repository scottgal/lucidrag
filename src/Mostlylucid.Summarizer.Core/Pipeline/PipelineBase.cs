using System.Diagnostics;
using Mostlylucid.Summarizer.Core.Utilities;

namespace Mostlylucid.Summarizer.Core.Pipeline;

/// <summary>
/// Base class providing common pipeline functionality.
/// </summary>
public abstract class PipelineBase : IPipeline
{
    /// <inheritdoc />
    public abstract string PipelineId { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract IReadOnlySet<string> SupportedExtensions { get; }

    /// <inheritdoc />
    public virtual bool CanProcess(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);
    }

    /// <inheritdoc />
    public async Task<PipelineResult> ProcessAsync(
        string filePath,
        PipelineOptions? options = null,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            progress?.Report(new PipelineProgress("Starting", $"Processing {Path.GetFileName(filePath)}", 0));

            var chunks = await ProcessCoreAsync(filePath, options ?? new PipelineOptions(), progress, ct);

            stopwatch.Stop();
            progress?.Report(new PipelineProgress("Complete", "Processing finished", 100, chunks.Count, chunks.Count));

            return PipelineResult.Ok(filePath, PipelineId, chunks, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return PipelineResult.Fail(filePath, PipelineId, "Operation cancelled", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return PipelineResult.Fail(filePath, PipelineId, ex.Message, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Core processing logic to be implemented by derived classes.
    /// </summary>
    protected abstract Task<IReadOnlyList<ContentChunk>> ProcessCoreAsync(
        string filePath,
        PipelineOptions options,
        IProgress<PipelineProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// Generate a unique chunk ID.
    /// </summary>
    protected static string GenerateChunkId(string filePath, int index)
        => $"{Path.GetFileNameWithoutExtension(filePath)}_{index:D4}";

    /// <summary>
    /// Compute a content hash using XxHash64.
    /// </summary>
    protected static string ComputeHash(string content)
        => ContentHasher.ComputeHash(content);
}
