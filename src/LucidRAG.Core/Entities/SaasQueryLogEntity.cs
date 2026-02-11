namespace LucidRAG.Entities;

/// <summary>
///     Per-query audit log for SaaS API calls (search, chat, autocomplete).
/// </summary>
public class SaasQueryLogEntity
{
    public Guid Id { get; set; }
    public Guid ApiKeyId { get; set; }

    /// <summary>User's search/chat query text.</summary>
    public string QueryText { get; set; } = "";

    /// <summary>"search" | "chat" | "chat_stream" | "autocomplete"</summary>
    public string QueryType { get; set; } = "search";

    /// <summary>"hybrid" | "semantic" | "keyword"</summary>
    public string? SearchMode { get; set; }

    public int ResultCount { get; set; }

    /// <summary>End-to-end response time in milliseconds.</summary>
    public int TotalTimeMs { get; set; }

    /// <summary>Vector/BM25 retrieval phase time (ms).</summary>
    public int? RetrievalTimeMs { get; set; }

    /// <summary>LLM synthesis phase time (ms, chat only).</summary>
    public int? LlmTimeMs { get; set; }

    public bool Success { get; set; } = true;

    /// <summary>Null on success, error type on failure.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Origin/Referer domain.</summary>
    public string? RequestDomain { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country code from GeoDetection.</summary>
    public string? CountryCode { get; set; }

    /// <summary>Truncated user agent string.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Links to ConversationEntity for chat queries.</summary>
    public Guid? ConversationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ApiKeyEntity? ApiKey { get; set; }
}
