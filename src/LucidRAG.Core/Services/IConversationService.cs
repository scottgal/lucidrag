using LucidRAG.Entities;

namespace LucidRAG.Services;

public interface IConversationService
{
    Task<ConversationEntity> CreateConversationAsync(Guid? collectionId = null, string? title = null, CancellationToken ct = default);
    Task<ConversationEntity?> GetConversationAsync(Guid conversationId, CancellationToken ct = default);
    Task<List<ConversationEntity>> GetConversationsAsync(Guid? collectionId = null, CancellationToken ct = default);
    Task<ConversationMessage> AddMessageAsync(Guid conversationId, string role, string content, string? metadata = null, CancellationToken ct = default);
    Task<string> BuildContextAsync(Guid conversationId, int maxMessages = 10, CancellationToken ct = default);
    Task DeleteConversationAsync(Guid conversationId, CancellationToken ct = default);
}
