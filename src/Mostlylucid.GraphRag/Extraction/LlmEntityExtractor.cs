using System.Diagnostics;
using System.Text.RegularExpressions;
using Mostlylucid.GraphRag.Services;
using Mostlylucid.GraphRag.Storage;

namespace Mostlylucid.GraphRag.Extraction;

/// <summary>
/// LLM-based entity extraction mimicking Microsoft GraphRAG approach.
/// 
/// This makes 2 LLM calls per chunk:
/// 1. Extract entities (name, type, description)
/// 2. Extract relationships between entities
/// 
/// This is included for comparison with the heuristic approach.
/// For 100 chunks, expect ~200 LLM calls vs ~1 with heuristics.
/// </summary>
public sealed class LlmEntityExtractor : IEntityExtractor
{
    private readonly GraphRagDb _db;
    private readonly EmbeddingService _embedder;
    private readonly OllamaClient _llm;
    private int _llmCallCount;

    private const string EntityPrompt = """
        Extract all named entities from the following text.
        For each entity, provide: name|type|description
        
        Types: technology, framework, library, language, tool, database, service, concept, person, organization
        
        Text:
        {0}
        
        Return one entity per line in format: name|type|brief description
        Only include significant entities, not common words.
        """;

    private const string RelationshipPrompt = """
        Given these entities extracted from the text:
        {0}
        
        And this source text:
        {1}
        
        Extract relationships between the entities.
        Return one relationship per line in format: source_entity|relationship_type|target_entity|description
        
        Common relationship types: uses, implements, extends, depends_on, configures, part_of, related_to
        """;

    public LlmEntityExtractor(GraphRagDb db, EmbeddingService embedder, OllamaClient llm)
    {
        _db = db;
        _embedder = embedder;
        _llm = llm;
    }

    public async Task<ExtractionResult> ExtractAsync(IProgress<ProgressInfo>? progress = null, 
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _llmCallCount = 0;
        
        var chunks = await _db.GetAllChunksAsync();
        if (chunks.Count == 0) 
            return new ExtractionResult { Mode = ExtractionMode.Llm };

        var allEntities = new Dictionary<string, LlmEntity>(StringComparer.OrdinalIgnoreCase);
        var allRelationships = new List<LlmRelationship>();

        progress?.Report(new ProgressInfo(0, chunks.Count, "LLM extraction starting..."));

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = chunks[i];

            // ═══════════════════════════════════════════════════════════════
            // LLM Call 1: Extract entities from this chunk
            // ═══════════════════════════════════════════════════════════════
            var chunkEntities = await ExtractEntitiesFromChunkAsync(chunk.Text, chunk.Id, ct);
            foreach (var e in chunkEntities)
            {
                if (allEntities.TryGetValue(e.Name, out var existing))
                {
                    existing.ChunkIds.Add(chunk.Id);
                    existing.MentionCount++;
                }
                else
                {
                    e.ChunkIds.Add(chunk.Id);
                    allEntities[e.Name] = e;
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // LLM Call 2: Extract relationships between entities in this chunk  
            // ═══════════════════════════════════════════════════════════════
            if (chunkEntities.Count >= 2)
            {
                var chunkRels = await ExtractRelationshipsFromChunkAsync(
                    chunkEntities, chunk.Text, chunk.Id, ct);
                allRelationships.AddRange(chunkRels);
            }

            progress?.Report(new ProgressInfo(i + 1, chunks.Count, 
                $"Chunk {i + 1}/{chunks.Count}: {allEntities.Count} entities, {_llmCallCount} LLM calls"));
        }

        // ═══════════════════════════════════════════════════════════════════
        // Deduplicate entities using embeddings (same as heuristic approach)
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, 1, "Deduplicating entities..."));
        var deduped = await DeduplicateAsync(allEntities.Values.ToList(), ct);

        // ═══════════════════════════════════════════════════════════════════
        // Store entities and relationships
        // ═══════════════════════════════════════════════════════════════════
        progress?.Report(new ProgressInfo(0, deduped.Count, "Storing entities..."));
        foreach (var e in deduped)
        {
            await _db.UpsertEntityAsync(
                EntityId(e.Name), e.Name, e.Type, e.Description, e.ChunkIds.ToArray());
        }

        progress?.Report(new ProgressInfo(0, allRelationships.Count, "Storing relationships..."));
        var storedRels = await StoreRelationshipsAsync(allRelationships, deduped);

        sw.Stop();
        
        return new ExtractionResult
        {
            EntitiesExtracted = deduped.Count,
            RelationshipsExtracted = storedRels,
            LlmCallCount = _llmCallCount,
            Duration = sw.Elapsed,
            Mode = ExtractionMode.Llm
        };
    }

    private async Task<List<LlmEntity>> ExtractEntitiesFromChunkAsync(string text, string chunkId, 
        CancellationToken ct)
    {
        var prompt = string.Format(EntityPrompt, TruncateText(text, 2000));
        var response = await _llm.GenerateAsync(prompt, 0.3, ct);
        _llmCallCount++;

        var entities = new List<LlmEntity>();
        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length >= 2)
            {
                var name = parts[0].Trim().TrimStart('-', '*', ' ');
                if (name.Length >= 2 && name.Length <= 100)
                {
                    entities.Add(new LlmEntity
                    {
                        Name = name,
                        Type = parts[1].Trim().ToLowerInvariant(),
                        Description = parts.Length >= 3 ? parts[2].Trim() : null
                    });
                }
            }
        }
        return entities;
    }

    private async Task<List<LlmRelationship>> ExtractRelationshipsFromChunkAsync(
        List<LlmEntity> entities, string text, string chunkId, CancellationToken ct)
    {
        var entityList = string.Join("\n", entities.Select(e => $"- {e.Name} ({e.Type})"));
        var prompt = string.Format(RelationshipPrompt, entityList, TruncateText(text, 1500));
        var response = await _llm.GenerateAsync(prompt, 0.3, ct);
        _llmCallCount++;

        var relationships = new List<LlmRelationship>();
        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length >= 3)
            {
                relationships.Add(new LlmRelationship
                {
                    Source = parts[0].Trim().TrimStart('-', '*', ' '),
                    Type = parts[1].Trim().ToLowerInvariant().Replace(" ", "_"),
                    Target = parts[2].Trim(),
                    Description = parts.Length >= 4 ? parts[3].Trim() : null,
                    ChunkId = chunkId
                });
            }
        }
        return relationships;
    }

    private async Task<List<LlmEntity>> DeduplicateAsync(List<LlmEntity> entities, CancellationToken ct)
    {
        if (entities.Count <= 1) return entities;

        // Only deduplicate top entities to limit embedding calls
        var sorted = entities.OrderByDescending(e => e.MentionCount).Take(500).ToList();
        var embeddings = await _embedder.EmbedBatchAsync(sorted.Select(e => e.Name), ct);

        var merged = new List<LlmEntity>();
        var used = new HashSet<int>();

        for (int i = 0; i < sorted.Count; i++)
        {
            if (used.Contains(i)) continue;
            var canonical = sorted[i];
            used.Add(i);

            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (used.Contains(j)) continue;
                var similarity = CosineSimilarity(embeddings[i], embeddings[j]);
                if (similarity >= 0.85)
                {
                    canonical.MentionCount += sorted[j].MentionCount;
                    canonical.ChunkIds.UnionWith(sorted[j].ChunkIds);
                    used.Add(j);
                }
            }
            merged.Add(canonical);
        }

        // Add remaining entities that weren't in top 500
        var remaining = entities.Skip(500).Where(e => 
            !merged.Any(m => m.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase)));
        merged.AddRange(remaining);

        return merged;
    }

    private async Task<int> StoreRelationshipsAsync(List<LlmRelationship> relationships, 
        List<LlmEntity> entities)
    {
        var entityLookup = entities.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var count = 0;

        // Group relationships by source+target+type
        var grouped = relationships
            .Where(r => entityLookup.ContainsKey(r.Source) && entityLookup.ContainsKey(r.Target))
            .GroupBy(r => (r.Source, r.Target, r.Type));

        foreach (var g in grouped)
        {
            var srcId = EntityId(g.Key.Source);
            var tgtId = EntityId(g.Key.Target);
            var chunkIds = g.Select(r => r.ChunkId).Distinct().ToArray();
            var desc = g.First().Description;

            await _db.UpsertRelationshipAsync($"r_{srcId}_{tgtId}_{g.Key.Type}", 
                srcId, tgtId, g.Key.Type, desc, chunkIds);
            count++;
        }
        return count;
    }

    private static string EntityId(string name) =>
        $"e_{Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]", "_")}";

    private static string TruncateText(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "...";

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb) + 1e-10f);
    }
}

internal class LlmEntity
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "concept";
    public string? Description { get; set; }
    public int MentionCount { get; set; } = 1;
    public HashSet<string> ChunkIds { get; set; } = [];
}

internal class LlmRelationship
{
    public string Source { get; set; } = "";
    public string Type { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Description { get; set; }
    public string ChunkId { get; set; } = "";
}
