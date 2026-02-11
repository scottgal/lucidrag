namespace LucidRAG.Entities;

public class CustomDomainEntity
{
    public Guid Id { get; set; }
    public Guid ApiKeyId { get; set; }
    public string Domain { get; set; } = "";
    public bool IsVerified { get; set; }
    public string? VerificationToken { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ApiKeyEntity ApiKey { get; set; } = null!;
}
