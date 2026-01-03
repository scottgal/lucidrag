using System.Threading.Channels;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Types of progress updates that can be reported.
/// </summary>
public enum ProgressType
{
    /// <summary>Processing stage changed (e.g., "Extracting", "Embedding", "Synthesizing")</summary>
    Stage,
    /// <summary>Item-level progress within a stage (e.g., "Processing segment 5/100")</summary>
    ItemProgress,
    /// <summary>LLM activity (e.g., "Calling Ollama with 1500 tokens")</summary>
    LlmActivity,
    /// <summary>Informational log message</summary>
    Info,
    /// <summary>Warning message</summary>
    Warning,
    /// <summary>Error message</summary>
    Error,
    /// <summary>Processing completed successfully</summary>
    Completed,
    /// <summary>Model download progress</summary>
    Download
}

/// <summary>
/// A progress update event that can be written to a channel.
/// </summary>
/// <param name="Type">The type of progress update.</param>
/// <param name="Stage">Current processing stage (e.g., "Extraction", "Retrieval", "Synthesis").</param>
/// <param name="Message">Human-readable progress message.</param>
/// <param name="Current">Current item number (for item progress).</param>
/// <param name="Total">Total items (for item progress).</param>
/// <param name="PercentComplete">Overall percentage complete (0-100).</param>
/// <param name="Timestamp">When this update was created.</param>
/// <param name="ElapsedMs">Milliseconds elapsed since processing started.</param>
/// <param name="Data">Optional additional data (e.g., model name, segment count).</param>
public record ProgressUpdate(
    ProgressType Type,
    string Stage,
    string Message,
    int Current = 0,
    int Total = 0,
    double PercentComplete = 0,
    DateTimeOffset? Timestamp = null,
    long ElapsedMs = 0,
    Dictionary<string, object>? Data = null)
{
}

/// <summary>
/// Factory methods for creating ProgressUpdate instances.
/// </summary>
public static class ProgressUpdates
{
    /// <summary>
    /// Create a stage change update.
    /// </summary>
    public static ProgressUpdate Stage(string stage, string message, double percent = 0, long elapsedMs = 0) =>
        new(ProgressType.Stage, stage, message, PercentComplete: percent, ElapsedMs: elapsedMs);

    /// <summary>
    /// Create an item progress update.
    /// </summary>
    public static ProgressUpdate Item(string stage, int current, int total, string? message = null, long elapsedMs = 0) =>
        new(ProgressType.ItemProgress, stage, message ?? $"Processing {current}/{total}", current, total, 
            total > 0 ? (current * 100.0 / total) : 0, ElapsedMs: elapsedMs);

    /// <summary>
    /// Create an LLM activity update.
    /// </summary>
    public static ProgressUpdate Llm(string stage, string message, long elapsedMs = 0) =>
        new(ProgressType.LlmActivity, stage, message, ElapsedMs: elapsedMs);

    /// <summary>
    /// Create an info message.
    /// </summary>
    public static ProgressUpdate Info(string stage, string message, long elapsedMs = 0) =>
        new(ProgressType.Info, stage, message, ElapsedMs: elapsedMs);

    /// <summary>
    /// Create a warning message.
    /// </summary>
    public static ProgressUpdate Warning(string stage, string message, long elapsedMs = 0) =>
        new(ProgressType.Warning, stage, message, ElapsedMs: elapsedMs);

    /// <summary>
    /// Create an error message.
    /// </summary>
    public static ProgressUpdate Error(string stage, string message, long elapsedMs = 0) =>
        new(ProgressType.Error, stage, message, ElapsedMs: elapsedMs);

    /// <summary>
    /// Create a completion update.
    /// </summary>
    public static ProgressUpdate Completed(string message, long elapsedMs, Dictionary<string, object>? data = null) =>
        new(ProgressType.Completed, "Complete", message, PercentComplete: 100, ElapsedMs: elapsedMs, Data: data);

    /// <summary>
    /// Create a download progress update.
    /// </summary>
    public static ProgressUpdate Download(string modelName, long bytesDownloaded, long totalBytes, long elapsedMs = 0) =>
        new(ProgressType.Download, "Download", $"Downloading {modelName}", 
            Current: (int)(bytesDownloaded / 1024), 
            Total: (int)(totalBytes / 1024),
            PercentComplete: totalBytes > 0 ? (bytesDownloaded * 100.0 / totalBytes) : 0,
            ElapsedMs: elapsedMs,
            Data: new Dictionary<string, object> { ["model"] = modelName, ["bytes"] = bytesDownloaded, ["totalBytes"] = totalBytes });
}

/// <summary>
/// Factory for creating progress channels.
/// </summary>
public static class ProgressChannel
{
    /// <summary>
    /// Create an unbounded progress channel.
    /// Use this when you want to buffer all updates.
    /// </summary>
    public static Channel<ProgressUpdate> CreateUnbounded() =>
        Channel.CreateUnbounded<ProgressUpdate>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });

    /// <summary>
    /// Create a bounded progress channel that drops oldest items when full.
    /// Use this when you only care about recent progress.
    /// </summary>
    /// <param name="capacity">Maximum number of items to buffer.</param>
    public static Channel<ProgressUpdate> CreateBounded(int capacity = 100) =>
        Channel.CreateBounded<ProgressUpdate>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
}

/// <summary>
/// Extension methods for writing progress updates to channels.
/// </summary>
public static class ProgressChannelExtensions
{
    /// <summary>
    /// Try to write a progress update without blocking.
    /// Returns false if the channel is full or completed.
    /// </summary>
    public static bool TryWriteUpdate(this ChannelWriter<ProgressUpdate>? writer, ProgressUpdate update)
    {
        if (writer == null) return false;
        return writer.TryWrite(update with { Timestamp = DateTimeOffset.UtcNow });
    }

    /// <summary>
    /// Write a stage change update.
    /// </summary>
    public static void WriteStage(this ChannelWriter<ProgressUpdate>? writer, string stage, string message, double percent = 0, long elapsedMs = 0)
    {
        writer.TryWriteUpdate(ProgressUpdates.Stage(stage, message, percent, elapsedMs));
    }

    /// <summary>
    /// Write an item progress update.
    /// </summary>
    public static void WriteItem(this ChannelWriter<ProgressUpdate>? writer, string stage, int current, int total, string? message = null, long elapsedMs = 0)
    {
        writer.TryWriteUpdate(ProgressUpdates.Item(stage, current, total, message, elapsedMs));
    }

    /// <summary>
    /// Write an LLM activity update.
    /// </summary>
    public static void WriteLlm(this ChannelWriter<ProgressUpdate>? writer, string stage, string message, long elapsedMs = 0)
    {
        writer.TryWriteUpdate(ProgressUpdates.Llm(stage, message, elapsedMs));
    }

    /// <summary>
    /// Write an info message.
    /// </summary>
    public static void WriteInfo(this ChannelWriter<ProgressUpdate>? writer, string stage, string message, long elapsedMs = 0)
    {
        writer.TryWriteUpdate(ProgressUpdates.Info(stage, message, elapsedMs));
    }

    /// <summary>
    /// Write a completion update and complete the channel.
    /// </summary>
    public static void WriteCompleted(this ChannelWriter<ProgressUpdate>? writer, string message, long elapsedMs, Dictionary<string, object>? data = null)
    {
        writer.TryWriteUpdate(ProgressUpdates.Completed(message, elapsedMs, data));
        writer?.TryComplete();
    }
}
