using Mostlylucid.GraphRag.Services;
using Mostlylucid.GraphRag.Storage;

namespace Mostlylucid.GraphRag.Graph;

/// <summary>
/// Leiden-inspired community detection with integrated summarization.
/// Simplified implementation: modularity optimization + connectivity refinement.
/// </summary>
public sealed class CommunityDetector
{
    private readonly GraphRagDb _db;
    private readonly OllamaClient _llm;
    private readonly double _resolution;
    private readonly int _minSize;
    private readonly Random _rng = new(42);

    public CommunityDetector(GraphRagDb db, OllamaClient llm, double resolution = 1.0, int minSize = 2)
    {
        _db = db;
        _llm = llm;
        _resolution = resolution;
        _minSize = minSize;
    }

    public async Task<HierarchicalCommunities> DetectAndSummarizeAsync(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new ProgressInfo(0, 1, "Loading graph..."));
        
        var entities = await _db.GetAllEntitiesAsync();
        var relationships = await _db.GetAllRelationshipsAsync();
        
        if (entities.Count == 0)
            return new HierarchicalCommunities([]);

        // Build graph
        var (nodes, totalWeight) = BuildGraph(entities, relationships);
        progress?.Report(new ProgressInfo(0, 1, $"Graph: {nodes.Count} nodes, {relationships.Count} edges"));

        // Detect communities (single level for simplicity - can extend to hierarchical)
        var partition = DetectCommunities(nodes, totalWeight);
        var communities = ExtractCommunities(nodes, partition);
        
        // Filter small communities
        communities = communities.Where(c => c.Entities.Count >= _minSize).ToList();
        progress?.Report(new ProgressInfo(0, communities.Count, $"Found {communities.Count} communities"));

        // Summarize each community
        for (int i = 0; i < communities.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = communities[i];
            progress?.Report(new ProgressInfo(i, communities.Count, $"Summarizing {c.Id} ({c.Entities.Count} entities)"));
            c.Summary = await SummarizeAsync(c, ct);
        }

        // Store
        foreach (var c in communities)
            await _db.InsertCommunityAsync(c.Id, c.Level, c.Entities.Select(e => e.Id).ToArray(), c.Summary);

        return new HierarchicalCommunities([new CommunityLevel(0, communities)]);
    }

    private (Dictionary<string, GraphNode> Nodes, double TotalWeight) BuildGraph(List<EntityResult> entities, List<RelationshipResult> rels)
    {
        var nodes = entities.ToDictionary(e => e.Id, e => new GraphNode(e.Id, e, []));
        
        foreach (var r in rels)
        {
            if (!nodes.ContainsKey(r.SourceEntityId) || !nodes.ContainsKey(r.TargetEntityId)) continue;
            nodes[r.SourceEntityId].Neighbors[r.TargetEntityId] = nodes[r.SourceEntityId].Neighbors.GetValueOrDefault(r.TargetEntityId) + r.Weight;
            nodes[r.TargetEntityId].Neighbors[r.SourceEntityId] = nodes[r.TargetEntityId].Neighbors.GetValueOrDefault(r.SourceEntityId) + r.Weight;
        }
        
        var totalWeight = nodes.Values.Sum(n => n.Neighbors.Values.Sum());
        return (nodes, totalWeight);
    }

    private Dictionary<string, int> DetectCommunities(Dictionary<string, GraphNode> nodes, double totalWeight)
    {
        // Initialize: each node in own community
        var partition = nodes.Keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
        
        // Modularity optimization (simplified Leiden local moving phase)
        bool improved;
        int iterations = 0;
        do
        {
            improved = false;
            var nodeList = nodes.Keys.ToList();
            Shuffle(nodeList);

            foreach (var nodeId in nodeList)
            {
                var node = nodes[nodeId];
                var currentComm = partition[nodeId];
                var bestComm = currentComm;
                var bestDelta = 0.0;

                // Find neighbor communities
                var neighborComms = node.Neighbors.Keys.Where(nodes.ContainsKey).Select(n => partition[n]).Distinct().ToList();
                if (!neighborComms.Contains(currentComm)) neighborComms.Add(currentComm);

                foreach (var comm in neighborComms)
                {
                    if (comm == currentComm) continue;
                    var delta = ModularityDelta(nodes, partition, nodeId, currentComm, comm, totalWeight);
                    if (delta > bestDelta) { bestDelta = delta; bestComm = comm; }
                }

                if (bestComm != currentComm) { partition[nodeId] = bestComm; improved = true; }
            }
        } while (improved && ++iterations < 50);

        // Renumber to contiguous
        var unique = partition.Values.Distinct().OrderBy(x => x).Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        foreach (var k in partition.Keys.ToList()) partition[k] = unique[partition[k]];

        return partition;
    }

    private double ModularityDelta(Dictionary<string, GraphNode> nodes, Dictionary<string, int> partition, 
        string nodeId, int fromComm, int toComm, double m2)
    {
        if (m2 == 0) return 0;
        var node = nodes[nodeId];
        var ki = node.Neighbors.Values.Sum();

        var kiIn = node.Neighbors.Where(kv => nodes.ContainsKey(kv.Key) && partition[kv.Key] == toComm).Sum(kv => kv.Value);
        var kiOut = node.Neighbors.Where(kv => nodes.ContainsKey(kv.Key) && partition[kv.Key] == fromComm && kv.Key != nodeId).Sum(kv => kv.Value);

        var sigmaTot = partition.Where(kv => kv.Value == toComm).Sum(kv => nodes[kv.Key].Neighbors.Values.Sum());
        var sigmaFrom = partition.Where(kv => kv.Value == fromComm && kv.Key != nodeId).Sum(kv => nodes[kv.Key].Neighbors.Values.Sum());

        return (kiIn - kiOut) / m2 - _resolution * ki * (sigmaTot - sigmaFrom) / (m2 * m2);
    }

    private List<Community> ExtractCommunities(Dictionary<string, GraphNode> nodes, Dictionary<string, int> partition)
    {
        return partition.GroupBy(kv => kv.Value)
            .OrderByDescending(g => g.Count())
            .Select((g, i) => new Community($"c_0_{i}", 0, g.Select(kv => nodes[kv.Key].Entity).ToList()))
            .ToList();
    }

    private async Task<string> SummarizeAsync(Community c, CancellationToken ct)
    {
        var entityList = string.Join("\n", c.Entities.Take(15).Select(e => $"- {e.Name} ({e.Type}): {e.Description ?? "no desc"}"));
        var rels = new List<string>();
        foreach (var e in c.Entities.Take(8))
        {
            var r = await _db.GetRelationshipsForEntityAsync(e.Id);
            rels.AddRange(r.Take(3).Select(x => $"- {x.SourceName} --[{x.RelationshipType}]--> {x.TargetName}"));
        }

        var prompt = $"""
            Summarize this community of related concepts in 2-3 sentences.
            
            Entities:
            {entityList}
            
            Relationships:
            {string.Join("\n", rels.Distinct().Take(10))}
            
            Focus on: main theme, key technologies, how they connect.
            """;

        return await _llm.GenerateAsync(prompt, 0.3, ct);
    }

    private void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private record GraphNode(string Id, EntityResult Entity, Dictionary<string, double> Neighbors);
}
