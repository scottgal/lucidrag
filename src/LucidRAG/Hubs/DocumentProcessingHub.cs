using Microsoft.AspNetCore.SignalR;

namespace LucidRAG.Hubs;

/// <summary>
/// SignalR hub for real-time document processing updates.
/// Broadcasts processing status, progress, and completion events.
/// </summary>
public class DocumentProcessingHub : Hub
{
    /// <summary>
    /// Client can call this to join a specific collection's update group.
    /// </summary>
    public async Task JoinCollection(string collectionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"collection_{collectionId}");
    }

    /// <summary>
    /// Client can call this to leave a collection's update group.
    /// </summary>
    public async Task LeaveCollection(string collectionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"collection_{collectionId}");
    }
}

/// <summary>
/// Service for broadcasting document processing updates via SignalR.
/// </summary>
public interface IProcessingNotificationService
{
    Task NotifyDocumentStarted(Guid documentId, string documentName, Guid? collectionId);
    Task NotifyDocumentProgress(Guid documentId, string documentName, float progress, string stage, Guid? collectionId);
    Task NotifyDocumentCompleted(Guid documentId, string documentName, int segmentCount, int entityCount, int tableCount, Guid? collectionId);
    Task NotifyDocumentFailed(Guid documentId, string documentName, string error, Guid? collectionId);
    Task NotifyQueueStatus(int pending, int processing, int completed, int failed);
}

public class ProcessingNotificationService(IHubContext<DocumentProcessingHub> hubContext) : IProcessingNotificationService
{
    public async Task NotifyDocumentStarted(Guid documentId, string documentName, Guid? collectionId)
    {
        var payload = new
        {
            type = "started",
            documentId,
            documentName,
            collectionId,
            timestamp = DateTimeOffset.UtcNow
        };

        await hubContext.Clients.All.SendAsync("DocumentProcessing", payload);

        if (collectionId.HasValue)
        {
            await hubContext.Clients.Group($"collection_{collectionId.Value}").SendAsync("DocumentProcessing", payload);
        }
    }

    public async Task NotifyDocumentProgress(Guid documentId, string documentName, float progress, string stage, Guid? collectionId)
    {
        var payload = new
        {
            type = "progress",
            documentId,
            documentName,
            progress,
            stage,
            collectionId,
            timestamp = DateTimeOffset.UtcNow
        };

        await hubContext.Clients.All.SendAsync("DocumentProcessing", payload);

        if (collectionId.HasValue)
        {
            await hubContext.Clients.Group($"collection_{collectionId.Value}").SendAsync("DocumentProcessing", payload);
        }
    }

    public async Task NotifyDocumentCompleted(Guid documentId, string documentName, int segmentCount, int entityCount, int tableCount, Guid? collectionId)
    {
        var payload = new
        {
            type = "completed",
            documentId,
            documentName,
            segmentCount,
            entityCount,
            tableCount,
            collectionId,
            timestamp = DateTimeOffset.UtcNow
        };

        await hubContext.Clients.All.SendAsync("DocumentProcessing", payload);

        if (collectionId.HasValue)
        {
            await hubContext.Clients.Group($"collection_{collectionId.Value}").SendAsync("DocumentProcessing", payload);
        }
    }

    public async Task NotifyDocumentFailed(Guid documentId, string documentName, string error, Guid? collectionId)
    {
        var payload = new
        {
            type = "failed",
            documentId,
            documentName,
            error,
            collectionId,
            timestamp = DateTimeOffset.UtcNow
        };

        await hubContext.Clients.All.SendAsync("DocumentProcessing", payload);

        if (collectionId.HasValue)
        {
            await hubContext.Clients.Group($"collection_{collectionId.Value}").SendAsync("DocumentProcessing", payload);
        }
    }

    public async Task NotifyQueueStatus(int pending, int processing, int completed, int failed)
    {
        var payload = new
        {
            type = "queue_status",
            pending,
            processing,
            completed,
            failed,
            timestamp = DateTimeOffset.UtcNow
        };

        await hubContext.Clients.All.SendAsync("QueueStatus", payload);
    }
}
