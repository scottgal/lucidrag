using Mostlylucid.GraphRag.Search;
using Mostlylucid.GraphRag.Services;
using Mostlylucid.GraphRag.Storage;

namespace Mostlylucid.GraphRag.Query;

/// <summary>
/// GraphRAG query engine with three modes:
/// - Local: Hybrid search (BERT+BM25) with entity context
/// - Global: Map-reduce over community summaries
/// - DRIFT: Local + community context for connective queries
/// </summary>
public sealed class QueryEngine
{
    private readonly GraphRagDb _db;
    private readonly SearchService _search;
    private readonly OllamaClient _llm;

    public QueryEngine(GraphRagDb db, SearchService search, OllamaClient llm)
    {
        _db = db;
        _search = search;
        _llm = llm;
    }

    public async Task<QueryResult> QueryAsync(string query, QueryMode? mode = null, CancellationToken ct = default)
    {
        mode ??= ClassifyQuery(query);
        return mode.Value switch
        {
            QueryMode.Global => await GlobalSearchAsync(query, ct),
            QueryMode.Drift => await DriftSearchAsync(query, ct),
            _ => await LocalSearchAsync(query, ct)
        };
    }

    /// <summary>
    /// Local: Hybrid search with entity/relationship context
    /// Best for: "How do I use X?", "What is Y?"
    /// </summary>
    private async Task<QueryResult> LocalSearchAsync(string query, CancellationToken ct)
    {
        var results = await _search.SearchAsync(query, topK: 5, ct);
        var context = BuildContext(results);
        var answer = await _llm.GenerateAsync($"""
            Answer based on this context. Be concise. If context doesn't help, say so.
            
            Context:
            {context}
            
            Question: {query}
            """, 0.7, ct);

        return new QueryResult(QueryMode.Local, query, answer)
        {
            Sources = results.Select(r => new SourceRef(r.ChunkId, r.DocumentId, Truncate(r.Text, 200), r.Score)).ToList(),
            Entities = results.SelectMany(r => r.Entities).DistinctBy(e => e.Id).Select(e => e.Name).ToList()
        };
    }

    /// <summary>
    /// Global: Map-reduce over community summaries
    /// Best for: "What are the main themes?", "Summarize the topics"
    /// </summary>
    private async Task<QueryResult> GlobalSearchAsync(string query, CancellationToken ct)
    {
        var communities = await _db.GetCommunitiesAsync();
        if (communities.Count == 0)
            return new QueryResult(QueryMode.Global, query, "No communities found. Run indexing first.");

        // Map: extract relevant info from each community
        var partials = new List<string>();
        foreach (var c in communities.Where(c => !string.IsNullOrEmpty(c.Summary)))
        {
            ct.ThrowIfCancellationRequested();
            var response = await _llm.GenerateAsync($"""
                Community: {c.Summary}
                Question: {query}
                If relevant, summarize in 1-2 sentences. If not, say "NOT_RELEVANT".
                """, 0.3, ct);
            if (!response.Contains("NOT_RELEVANT", StringComparison.OrdinalIgnoreCase))
                partials.Add(response);
        }

        // Reduce: synthesize
        var answer = partials.Count > 0
            ? await _llm.GenerateAsync($"""
                Question: {query}
                
                Information from topic clusters:
                {string.Join("\n\n", partials.Select((p, i) => $"[{i + 1}] {p}"))}
                
                Synthesize into a comprehensive answer. Organize by themes.
                """, 0.7, ct)
            : "No relevant information found in community summaries.";

        return new QueryResult(QueryMode.Global, query, answer) { CommunitiesUsed = communities.Count };
    }

    /// <summary>
    /// DRIFT: Local + community context for connective queries
    /// Best for: "How does X relate to Y?", "Compare A and B"
    /// </summary>
    private async Task<QueryResult> DriftSearchAsync(string query, CancellationToken ct)
    {
        var local = await LocalSearchAsync(query, ct);

        // Find communities containing discovered entities
        var allEntities = await _db.GetAllEntitiesAsync();
        var entityIds = allEntities
            .Where(e => local.Entities.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
            .Select(e => e.Id).ToHashSet();

        var communities = await _db.GetCommunitiesAsync();
        var relevant = communities
            .Where(c => c.EntityIds.Any(id => entityIds.Contains(id)) && !string.IsNullOrEmpty(c.Summary))
            .ToList();

        var answer = await _llm.GenerateAsync($"""
            Question: {query}
            
            Specific details:
            {local.Answer}
            
            Broader themes:
            {string.Join("\n", relevant.Select(c => $"- {c.Summary}"))}
            
            Synthesize details with themes. Focus on connections.
            """, 0.7, ct);

        return new QueryResult(QueryMode.Drift, query, answer)
        {
            Sources = local.Sources,
            Entities = local.Entities,
            CommunitiesUsed = relevant.Count
        };
    }

    private static QueryMode ClassifyQuery(string query)
    {
        var q = query.ToLowerInvariant();
        if (q.Contains("main theme") || q.Contains("summarize") || q.Contains("what topics") || q.Contains("overview"))
            return QueryMode.Global;
        if (q.Contains("relate") || q.Contains("connect") || q.Contains("compare") || q.Contains("between"))
            return QueryMode.Drift;
        return QueryMode.Local;
    }

    private static string BuildContext(List<SearchResult> results)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in results)
        {
            sb.AppendLine($"--- Chunk (score: {r.Score:F3}) ---");
            sb.AppendLine(r.Text);
            if (r.Entities.Count > 0)
                sb.AppendLine($"Entities: {string.Join(", ", r.Entities.Select(e => e.Name))}");
            if (r.Relationships.Count > 0)
                foreach (var rel in r.Relationships.Take(3))
                    sb.AppendLine($"  {rel.SourceName} --[{rel.RelationshipType}]--> {rel.TargetName}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
