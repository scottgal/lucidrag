using System.Collections.Concurrent;
using System.Threading.Channels;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Services.Background;

public class DocumentProcessingQueue
{
    // Bounded queue to prevent unbounded memory growth - max 100 documents waiting
    private readonly Channel<DocumentProcessingJob> _queue = Channel.CreateBounded<DocumentProcessingJob>(
        new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait, // Block when full
            SingleReader = true, // Only one processor reads
            SingleWriter = false // Multiple uploads can write
        });

    // Track progress channels with creation time for cleanup
    private readonly ConcurrentDictionary<Guid, ProgressChannelEntry> _progressChannels = new();

    // Max age for progress channels before cleanup (handles abandoned channels)
    private static readonly TimeSpan ProgressChannelMaxAge = TimeSpan.FromHours(1);

    public async ValueTask EnqueueAsync(DocumentProcessingJob job, CancellationToken ct = default)
    {
        // Use timeout to prevent indefinite blocking if queue is full
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await _queue.Writer.WriteAsync(job, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Document processing queue is full. Please try again later.");
        }
    }

    public async ValueTask<DocumentProcessingJob> DequeueAsync(CancellationToken ct = default)
    {
        return await _queue.Reader.ReadAsync(ct);
    }

    public Channel<ProgressUpdate> GetOrCreateProgressChannel(Guid documentId)
    {
        var entry = _progressChannels.GetOrAdd(documentId, _ => new ProgressChannelEntry(
            // Bounded progress channel - max 500 updates (plenty for progress reporting)
            Channel.CreateBounded<ProgressUpdate>(new BoundedChannelOptions(500)
            {
                FullMode = BoundedChannelFullMode.DropOldest, // Drop old updates if consumer is slow
                SingleReader = false, // Multiple SSE clients may read
                SingleWriter = false // Multiple stages write
            }),
            DateTimeOffset.UtcNow));

        return entry.Channel;
    }

    public void CompleteProgressChannel(Guid documentId)
    {
        if (_progressChannels.TryRemove(documentId, out var entry))
        {
            entry.Channel.Writer.TryComplete();
        }
    }

    public bool TryGetProgressChannel(Guid documentId, out Channel<ProgressUpdate>? channel)
    {
        if (_progressChannels.TryGetValue(documentId, out var entry))
        {
            channel = entry.Channel;
            return true;
        }

        channel = null;
        return false;
    }

    /// <summary>
    /// Gets current queue depth for monitoring
    /// </summary>
    public int QueueDepth => _queue.Reader.Count;

    /// <summary>
    /// Gets number of active progress channels
    /// </summary>
    public int ActiveProgressChannels => _progressChannels.Count;

    /// <summary>
    /// Cleans up abandoned progress channels older than max age.
    /// Called periodically by the background processor.
    /// </summary>
    public int CleanupAbandonedProgressChannels()
    {
        var cutoff = DateTimeOffset.UtcNow - ProgressChannelMaxAge;
        var cleanedUp = 0;

        foreach (var kvp in _progressChannels)
        {
            if (kvp.Value.CreatedAt < cutoff)
            {
                if (_progressChannels.TryRemove(kvp.Key, out var entry))
                {
                    entry.Channel.Writer.TryComplete();
                    cleanedUp++;
                }
            }
        }

        return cleanedUp;
    }

    private record ProgressChannelEntry(Channel<ProgressUpdate> Channel, DateTimeOffset CreatedAt);
}

public record DocumentProcessingJob(
    Guid DocumentId,
    string FilePath,
    Guid? CollectionId);
