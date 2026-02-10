using System.Collections.Concurrent;
using System.Text.Json;
using LucidRAG.Data;
using LucidRAG.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Services;

/// <summary>
///     User-scoped personal fact storage. Stores facts as evidence artifacts
///     and injects relevant personal context into synthesis prompts.
///     Ported from DoomSummarizer's PersonalCorpusService, adapted for LucidRAG's
///     evidence-backed, multi-tenant architecture.
/// </summary>
public interface IPersonalMemoryService
{
    /// <summary>
    ///     Remember a personal fact for the user.
    /// </summary>
    Task<PersonalFact> RememberAsync(string userId, string fact, CancellationToken ct = default);

    /// <summary>
    ///     Get all personal facts for a user.
    /// </summary>
    Task<List<PersonalFact>> GetFactsAsync(string userId, CancellationToken ct = default);

    /// <summary>
    ///     Forget (delete) a personal fact by ID.
    /// </summary>
    Task<bool> ForgetAsync(string userId, Guid factId, CancellationToken ct = default);

    /// <summary>
    ///     Detect self-disclosure in a user's message and auto-remember.
    ///     Returns the extracted fact statement, or null if no personal info detected.
    /// </summary>
    Task<string?> DetectAndRememberAsync(string userId, string message, CancellationToken ct = default);

    /// <summary>
    ///     Build personal context string for injection into synthesis prompts.
    ///     Returns null if the user has no relevant personal facts.
    /// </summary>
    Task<string?> BuildPersonalContextAsync(string userId, CancellationToken ct = default);
}

public record PersonalFact(Guid Id, string Fact, DateTimeOffset CreatedAt);

public class PersonalMemoryService(
    RagDocumentsDbContext db,
    ILogger<PersonalMemoryService> logger) : IPersonalMemoryService
{
    // Evidence type for personal facts
    private const string PersonalFactType = "personal_fact";

    // We need a stable "personal memory" entity per user to hang facts off of
    // Use a deterministic GUID derived from the user ID
    private static Guid GetPersonalEntityId(string userId)
        => new(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"personal_memory:{userId}")));

    public async Task<PersonalFact> RememberAsync(string userId, string fact, CancellationToken ct = default)
    {
        var entityId = GetPersonalEntityId(userId);

        // Ensure the personal memory entity exists
        await EnsurePersonalEntityAsync(entityId, userId, ct);

        var artifact = new EvidenceArtifact
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            ArtifactType = PersonalFactType,
            MimeType = "text/plain",
            StorageBackend = "inline",
            StoragePath = $"inline:{PersonalFactType}",
            FileSizeBytes = System.Text.Encoding.UTF8.GetByteCount(fact),
            Content = fact,
            ProducerSource = "user",
            Metadata = JsonSerializer.Serialize(new { userId, type = "personal_fact" })
        };

        db.EvidenceArtifacts.Add(artifact);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Stored personal fact {FactId} for user {UserId}: {Fact}",
            artifact.Id, userId, fact.Length > 50 ? fact[..50] + "..." : fact);

        return new PersonalFact(artifact.Id, fact, artifact.CreatedAt);
    }

    public async Task<List<PersonalFact>> GetFactsAsync(string userId, CancellationToken ct = default)
    {
        var entityId = GetPersonalEntityId(userId);

        var artifacts = await db.EvidenceArtifacts
            .AsNoTracking()
            .Where(a => a.EntityId == entityId && a.ArtifactType == PersonalFactType)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        return artifacts
            .Where(a => !string.IsNullOrEmpty(a.Content))
            .Select(a => new PersonalFact(a.Id, a.Content!, a.CreatedAt))
            .ToList();
    }

    public async Task<bool> ForgetAsync(string userId, Guid factId, CancellationToken ct = default)
    {
        var entityId = GetPersonalEntityId(userId);

        var artifact = await db.EvidenceArtifacts
            .FirstOrDefaultAsync(a => a.Id == factId
                                      && a.EntityId == entityId
                                      && a.ArtifactType == PersonalFactType, ct);

        if (artifact == null) return false;

        db.EvidenceArtifacts.Remove(artifact);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Forgot personal fact {FactId} for user {UserId}", factId, userId);
        return true;
    }

    public Task<string?> DetectAndRememberAsync(string userId, string message, CancellationToken ct = default)
    {
        // Rule-based self-disclosure detection (no LLM needed)
        var fact = DetectSelfDisclosure(message);
        if (fact == null) return Task.FromResult<string?>(null);

        // Fire and forget the storage (don't block the chat flow)
        _ = Task.Run(async () =>
        {
            try
            {
                await RememberAsync(userId, fact, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to auto-remember personal fact");
            }
        }, ct);

        return Task.FromResult<string?>(fact);
    }

    public async Task<string?> BuildPersonalContextAsync(string userId, CancellationToken ct = default)
    {
        var facts = await GetFactsAsync(userId, ct);
        if (facts.Count == 0) return null;

        // Take most recent 10 facts
        var relevantFacts = facts.Take(10).Select(f => f.Fact);
        return $"USER CONTEXT:\n{string.Join("\n", relevantFacts)}";
    }

    private async Task EnsurePersonalEntityAsync(Guid entityId, string userId, CancellationToken ct)
    {
        var exists = await db.RetrievalEntities.AnyAsync(r => r.Id == entityId, ct);
        if (exists) return;

        db.RetrievalEntities.Add(new RetrievalEntityRecord
        {
            Id = entityId,
            ContentType = "personal",
            Source = $"personal_memory:{userId}",
            Title = $"Personal Memory ({userId})"
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Race condition: another request already created it
        }
    }

    /// <summary>
    ///     Rule-based self-disclosure detection. Catches explicit patterns like
    ///     "I live in X", "I work at X", "I use X", "My name is X".
    /// </summary>
    private static string? DetectSelfDisclosure(string message)
    {
        if (!ContainsFirstPerson(message)) return null;

        var lower = message.ToLowerInvariant();

        // Location patterns
        if (lower.Contains("i live in") || lower.Contains("i'm based in")
                                        || lower.Contains("i am based in") || lower.Contains("i moved to"))
            return ExtractFact(message, ["i live in", "i'm based in", "i am based in", "i moved to"], "lives in");

        // Work patterns
        if (lower.Contains("i work at") || lower.Contains("i work for"))
            return ExtractFact(message, ["i work at", "i work for"], "works at");

        // Role patterns
        if (lower.Contains("i'm a ") || lower.Contains("i am a "))
            return ExtractFact(message, ["i'm a", "i am a"], "is a");

        // Tool/preference patterns
        if (lower.Contains("i use ") || lower.Contains("i prefer "))
            return ExtractFact(message, ["i use", "i prefer"], "uses");

        return null;
    }

    private static string? ExtractFact(string text, string[] patterns, string verb)
    {
        foreach (var pattern in patterns)
        {
            var idx = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var after = text[(idx + pattern.Length)..].Trim();
            var endIdx = after.IndexOfAny(['.', ',', '?', '!', '\n']);
            var fact = endIdx > 0 ? after[..endIdx].Trim() : after.Trim();

            if (fact.Length >= 2)
                return $"User {verb} {fact}";
        }

        return null;
    }

    private static bool ContainsFirstPerson(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains(" i ") || lower.StartsWith("i ")
                                     || lower.Contains(" my ") || lower.StartsWith("my ")
                                     || lower.Contains(" i'm ") || lower.StartsWith("i'm ")
                                     || lower.Contains(" i am ") || lower.StartsWith("i am ");
    }
}
