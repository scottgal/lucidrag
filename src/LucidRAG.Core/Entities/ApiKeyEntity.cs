namespace LucidRAG.Entities;

public class ApiKeyEntity
{
    public Guid Id { get; set; }

    /// <summary>
    ///     Visible prefix for display (e.g., "lrag_abc123...").
    /// </summary>
    public required string KeyPrefix { get; set; }

    /// <summary>
    ///     SHA256 hash (base64) of the full API key. Used for validation.
    /// </summary>
    public required string KeyHash { get; set; }

    public string? Description { get; set; }

    /// <summary>
    ///     FK to AspNetUsers. Null for system-generated keys.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    ///     Normalized email for free-tier dedup (Gmail +/. alias handling).
    ///     Stored at creation time so we can enforce 1 free key per real email.
    /// </summary>
    public string? NormalizedOwnerEmail { get; set; }

    /// <summary>
    ///     Plan tier: "free", "starter", "pro".
    /// </summary>
    public string Plan { get; set; } = "free";

    public bool IsActive { get; set; } = true;
    public bool AllowChat { get; set; } = true;
    public bool AllowSearch { get; set; } = true;

    public int RateLimitPerMinute { get; set; } = 60;
    public int RateLimitPerDay { get; set; } = 1000;
    public long TotalRequests { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    ///     Auto-created collection for this key's documents.
    /// </summary>
    public Guid? CollectionId { get; set; }

    // Navigation
    public CollectionEntity? Collection { get; set; }
    public ApiKeyIndexingSource? IndexingSource { get; set; }
    public ICollection<ApiKeyReadDomain> ReadDomains { get; set; } = [];
}

public class ApiKeyIndexingSource
{
    public Guid Id { get; set; }
    public Guid ApiKeyId { get; set; }

    public SourceType SourceType { get; set; }

    /// <summary>
    ///     Domain name (e.g., "docs.example.com") or GitHub repo (e.g., "owner/repo").
    /// </summary>
    public required string SourceValue { get; set; }

    public int MaxDocuments { get; set; } = 25;
    public int DocumentCount { get; set; }

    public DateTimeOffset? LastCrawledAt { get; set; }
    public string CrawlStatus { get; set; } = "idle";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Background indexer scheduling
    public DateTimeOffset? NextScheduledAt { get; set; }
    public bool TriggerCrawlNow { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }

    // Conditional re-crawl (ETag / If-Modified-Since)
    public string? ETag { get; set; }
    public string? LastModifiedHeader { get; set; }

    // Navigation
    public ApiKeyEntity? ApiKey { get; set; }
}

public class ApiKeyReadDomain
{
    public Guid Id { get; set; }
    public Guid ApiKeyId { get; set; }

    /// <summary>
    ///     Domain name without protocol or path (e.g., "example.com").
    /// </summary>
    public required string Domain { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ApiKeyEntity? ApiKey { get; set; }
}

public enum SourceType
{
    Domain,
    GitHubRepo,
    WordPress,
    GitHubApp
}

/// <summary>
///     Utility for normalizing email addresses to detect Gmail aliases (user+tag@, dots).
/// </summary>
public static class EmailNormalizer
{
    private static readonly HashSet<string> GmailDomains =
        ["gmail.com", "googlemail.com"];

    /// <summary>
    ///     Normalizes an email to a canonical form for dedup:
    ///     - Lowercase
    ///     - For Gmail: strip dots from local part, strip +suffix
    ///     - Returns "local@domain"
    /// </summary>
    public static string Normalize(string email)
    {
        email = email.Trim().ToLowerInvariant();
        var atIndex = email.IndexOf('@');
        if (atIndex < 0) return email;

        var local = email[..atIndex];
        var domain = email[(atIndex + 1)..];

        if (!GmailDomains.Contains(domain)) return email;

        // Strip +suffix
        var plusIndex = local.IndexOf('+');
        if (plusIndex >= 0)
            local = local[..plusIndex];

        // Strip dots
        local = local.Replace(".", "");

        // Normalize googlemail.com → gmail.com
        return $"{local}@gmail.com";
    }
}
