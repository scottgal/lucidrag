using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// MapReduce summarizer that produces structured JSON outputs for better merging.
/// Uses StructuredMapOutput schema for map phase and LossAwareReduceOutput for reduce.
/// </summary>
public class StructuredMapReduceSummarizer
{
    private const int DefaultMaxParallelism = 4;
    private const double ContextWindowTargetPercent = 0.6;
    private const double CharsPerToken = 4.0;

    private readonly int _contextWindow;
    private readonly int _maxParallelism;
    private readonly OllamaService _ollama;
    private readonly bool _verbose;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public StructuredMapReduceSummarizer(
        OllamaService ollama,
        bool verbose = false,
        int maxParallelism = DefaultMaxParallelism,
        int contextWindow = 8192)
    {
        _ollama = ollama;
        _verbose = verbose;
        _maxParallelism = maxParallelism > 0 ? maxParallelism : DefaultMaxParallelism;
        _contextWindow = contextWindow;
    }

    /// <summary>
    /// Create with auto-detected context window from model
    /// </summary>
    public static async Task<StructuredMapReduceSummarizer> CreateAsync(
        OllamaService ollama,
        bool verbose = false,
        int maxParallelism = DefaultMaxParallelism)
    {
        var contextWindow = await ollama.GetContextWindowAsync();
        return new StructuredMapReduceSummarizer(ollama, verbose, maxParallelism, contextWindow);
    }

    /// <summary>
    /// Summarize document chunks using structured JSON output
    /// </summary>
    public async Task<StructuredSummaryResult> SummarizeAsync(string docId, List<DocumentChunk> chunks)
    {
        var sw = Stopwatch.StartNew();

        Console.WriteLine($"Structured Map Phase: Processing {chunks.Count} chunks...");

        // Map phase: extract structured data from each chunk
        var mapOutputs = await ProcessMapPhaseAsync(chunks);

        Console.WriteLine($"Stitcher Phase: Deduplicating entities...");

        // Stitcher phase: deduplicate and resolve entities
        var stitcherOutput = RunStitcher(mapOutputs);

        Console.WriteLine($"Reduce Phase: Synthesizing final summary...");

        // Reduce phase: merge into final loss-aware output
        var reduceOutput = await RunReduceAsync(mapOutputs, stitcherOutput);

        sw.Stop();
        Console.WriteLine($"Completed in {sw.Elapsed.TotalSeconds:F1}s");

        return new StructuredSummaryResult(
            docId,
            mapOutputs,
            stitcherOutput,
            reduceOutput,
            sw.Elapsed);
    }

    #region Map Phase

    private async Task<List<StructuredMapOutput>> ProcessMapPhaseAsync(List<DocumentChunk> chunks)
    {
        var results = new StructuredMapOutput[chunks.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = _maxParallelism };
        var completed = 0;
        var lockObj = new object();

        await Parallel.ForEachAsync(
            chunks.Select((chunk, index) => (chunk, index)),
            options,
            async (item, ct) =>
            {
                results[item.index] = await ExtractStructuredDataAsync(item.chunk);

                lock (lockObj)
                {
                    completed++;
                    var percent = (double)completed / chunks.Count * 100;
                    Console.Write($"\r  Map: {percent:F0}% ({completed}/{chunks.Count})    ");
                }
            });

        Console.WriteLine();
        return results.ToList();
    }

    private async Task<StructuredMapOutput> ExtractStructuredDataAsync(DocumentChunk chunk)
    {
        const int maxContentLength = 2500;
        var content = chunk.Content.Length > maxContentLength
            ? chunk.Content[..maxContentLength] + "..."
            : chunk.Content;

        var jsonTemplate = """
            {
              "entities": [
                {"name": "string", "type": "Class|Function|Person|Organization|Technology|Concept|Location|Event", "description": "brief description"}
              ],
              "functions": [
                {"name": "string", "purpose": "what it does", "inputs": ["param1"], "outputs": ["return"], "sideEffects": ["effect"]}
              ],
              "keyFlows": [
                {"steps": ["A", "B", "C"], "type": "DataFlow|ControlFlow|Workflow|Dependency", "description": "brief"}
              ],
              "facts": [
                {"statement": "claim from text", "confidence": "High|Medium|Low", "evidence": "quote or reference"}
              ],
              "uncertainties": [
                {"description": "what's unclear", "type": "MissingContext|Ambiguous|IncompleteData"}
              ],
              "quotables": ["short memorable quote from text"]
            }
            """;

        var prompt = $"""
            Analyze this text and extract structured information as JSON.

            TEXT:
            {content}

            OUTPUT FORMAT (respond with ONLY valid JSON, no markdown):
            {jsonTemplate}

            Rules:
            - Only include entities/facts actually present in the text
            - confidence: High = directly stated, Medium = strongly implied, Low = inferred
            - If a category has no items, use empty array []
            - Keep descriptions concise (under 20 words)
            """;

        var response = await _ollama.GenerateAsync(prompt);
        return ParseMapOutput(response, chunk);
    }

    private StructuredMapOutput ParseMapOutput(string response, DocumentChunk chunk)
    {
        try
        {
            // Extract JSON from response (handle markdown code blocks)
            var json = ExtractJson(response);
            
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var entities = ParseEntities(root);
            var functions = ParseFunctions(root, chunk.Id);
            var keyFlows = ParseFlows(root);
            var facts = ParseFacts(root, chunk.Id);
            var uncertainties = ParseUncertainties(root);
            var quotables = ParseQuotables(root);

            return new StructuredMapOutput(
                chunk.Id,
                chunk.Order,
                chunk.Heading,
                entities,
                functions,
                keyFlows,
                facts,
                uncertainties,
                quotables);
        }
        catch (JsonException ex)
        {
            if (_verbose)
                Console.WriteLine($"\n  [WARN] JSON parse error for {chunk.Id}: {ex.Message}");

            // Return minimal output on parse failure
            return new StructuredMapOutput(
                chunk.Id,
                chunk.Order,
                chunk.Heading,
                [],
                [],
                [],
                [new FactClaim($"Content from: {chunk.Heading}", ConfidenceLevel.Medium, null, chunk.Id)],
                [new UncertaintyFlag("JSON parsing failed", UncertaintyType.IncompleteData, null)],
                []);
        }
    }

    private static string ExtractJson(string response)
    {
        // Remove markdown code blocks
        var json = response.Trim();
        
        if (json.StartsWith("```json"))
            json = json[7..];
        else if (json.StartsWith("```"))
            json = json[3..];
            
        if (json.EndsWith("```"))
            json = json[..^3];

        // Find JSON object boundaries
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        
        if (start >= 0 && end > start)
            json = json[start..(end + 1)];

        return json.Trim();
    }

    private static List<EntityReference> ParseEntities(JsonElement root)
    {
        var entities = new List<EntityReference>();
        
        if (!root.TryGetProperty("entities", out var entitiesElement))
            return entities;

        foreach (var e in entitiesElement.EnumerateArray())
        {
            var name = e.GetProperty("name").GetString() ?? "";
            var typeStr = e.TryGetProperty("type", out var t) ? t.GetString() ?? "Unknown" : "Unknown";
            var desc = e.TryGetProperty("description", out var d) ? d.GetString() : null;

            if (string.IsNullOrWhiteSpace(name)) continue;

            var entityType = typeStr.ToLowerInvariant() switch
            {
                "class" => EntityType.Class,
                "interface" => EntityType.Interface,
                "module" => EntityType.Module,
                "function" => EntityType.Function,
                "variable" => EntityType.Variable,
                "config" => EntityType.Config,
                "person" => EntityType.Person,
                "organization" => EntityType.Organization,
                "location" => EntityType.Location,
                "concept" => EntityType.Concept,
                "technology" => EntityType.Technology,
                "event" => EntityType.Event,
                _ => EntityType.Unknown
            };

            entities.Add(new EntityReference(name, entityType, desc, null));
        }

        return entities;
    }

    private static List<FunctionReference> ParseFunctions(JsonElement root, string chunkId)
    {
        var functions = new List<FunctionReference>();
        
        if (!root.TryGetProperty("functions", out var funcsElement))
            return functions;

        foreach (var f in funcsElement.EnumerateArray())
        {
            var name = f.GetProperty("name").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;

            var purpose = f.TryGetProperty("purpose", out var p) ? p.GetString() : null;
            var inputs = f.TryGetProperty("inputs", out var i) 
                ? i.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList() 
                : null;
            var outputs = f.TryGetProperty("outputs", out var o) 
                ? o.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList() 
                : null;
            var sideEffects = f.TryGetProperty("sideEffects", out var s) 
                ? s.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList() 
                : null;

            functions.Add(new FunctionReference(name, purpose, inputs, outputs, sideEffects, chunkId));
        }

        return functions;
    }

    private static List<FlowReference> ParseFlows(JsonElement root)
    {
        var flows = new List<FlowReference>();
        
        if (!root.TryGetProperty("keyFlows", out var flowsElement))
            return flows;

        foreach (var f in flowsElement.EnumerateArray())
        {
            var steps = f.TryGetProperty("steps", out var s)
                ? s.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList()
                : [];
                
            if (steps.Count < 2) continue;

            var typeStr = f.TryGetProperty("type", out var t) ? t.GetString() ?? "Workflow" : "Workflow";
            var desc = f.TryGetProperty("description", out var d) ? d.GetString() : null;

            var flowType = typeStr.ToLowerInvariant() switch
            {
                "dataflow" => FlowType.DataFlow,
                "controlflow" => FlowType.ControlFlow,
                "dependency" => FlowType.Dependency,
                "inheritance" => FlowType.Inheritance,
                "composition" => FlowType.Composition,
                "communication" => FlowType.Communication,
                _ => FlowType.Workflow
            };

            flows.Add(new FlowReference(steps, flowType, desc));
        }

        return flows;
    }

    private static List<FactClaim> ParseFacts(JsonElement root, string chunkId)
    {
        var facts = new List<FactClaim>();
        
        if (!root.TryGetProperty("facts", out var factsElement))
            return facts;

        foreach (var f in factsElement.EnumerateArray())
        {
            var statement = f.GetProperty("statement").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(statement)) continue;

            var confStr = f.TryGetProperty("confidence", out var c) ? c.GetString() ?? "Medium" : "Medium";
            var evidence = f.TryGetProperty("evidence", out var e) ? e.GetString() : null;

            var confidence = confStr.ToLowerInvariant() switch
            {
                "high" => ConfidenceLevel.High,
                "medium" => ConfidenceLevel.Medium,
                "low" => ConfidenceLevel.Low,
                _ => ConfidenceLevel.Medium
            };

            facts.Add(new FactClaim(statement, confidence, evidence, chunkId));
        }

        return facts;
    }

    private static List<UncertaintyFlag> ParseUncertainties(JsonElement root)
    {
        var uncertainties = new List<UncertaintyFlag>();
        
        if (!root.TryGetProperty("uncertainties", out var uncElement))
            return uncertainties;

        foreach (var u in uncElement.EnumerateArray())
        {
            var desc = u.GetProperty("description").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(desc)) continue;

            var typeStr = u.TryGetProperty("type", out var t) ? t.GetString() ?? "MissingContext" : "MissingContext";

            var uncType = typeStr.ToLowerInvariant() switch
            {
                "missingcontext" => UncertaintyType.MissingContext,
                "ambiguous" => UncertaintyType.Ambiguous,
                "contradictory" => UncertaintyType.Contradictory,
                "incompletedata" => UncertaintyType.IncompleteData,
                "externaldependency" => UncertaintyType.ExternalDependency,
                _ => UncertaintyType.MissingContext
            };

            uncertainties.Add(new UncertaintyFlag(desc, uncType, null));
        }

        return uncertainties;
    }

    private static List<string> ParseQuotables(JsonElement root)
    {
        if (!root.TryGetProperty("quotables", out var quotesElement))
            return [];

        return quotesElement.EnumerateArray()
            .Select(q => q.GetString() ?? "")
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .ToList();
    }

    #endregion

    #region Stitcher Phase

    private StitcherOutput RunStitcher(List<StructuredMapOutput> mapOutputs)
    {
        // Collect all entities across chunks
        var allEntities = mapOutputs
            .SelectMany(m => m.Entities.Select(e => (Entity: e, ChunkId: m.ChunkId)))
            .ToList();

        // Build merged entities with deduplication
        var mergedEntities = DeduplicateEntities(allEntities);

        // Build reference graph (who uses/calls what)
        var references = BuildReferenceGraph(mapOutputs);

        // Find name collisions
        var collisions = FindNameCollisions(allEntities);

        // Build coverage map
        var coverageMap = BuildCoverageMap(mapOutputs);

        return new StitcherOutput(mergedEntities, references, collisions, coverageMap);
    }

    private static List<MergedEntity> DeduplicateEntities(
        List<(EntityReference Entity, string ChunkId)> allEntities)
    {
        var merged = new Dictionary<string, MergedEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var (entity, chunkId) in allEntities)
        {
            var key = NormalizeEntityName(entity.Name);
            
            if (merged.TryGetValue(key, out var existing))
            {
                // Merge: add source chunk if not already present
                if (!existing.SourceChunks.Contains(chunkId))
                {
                    var updatedChunks = existing.SourceChunks.Append(chunkId).ToList();
                    var updatedAliases = existing.Aliases;
                    
                    // Add as alias if name differs
                    if (!existing.CanonicalName.Equals(entity.Name, StringComparison.OrdinalIgnoreCase) &&
                        !updatedAliases.Contains(entity.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        updatedAliases = updatedAliases.Append(entity.Name).ToList();
                    }

                    // Keep longer description
                    var desc = (entity.Description?.Length ?? 0) > (existing.Description?.Length ?? 0)
                        ? entity.Description
                        : existing.Description;

                    merged[key] = existing with
                    {
                        SourceChunks = updatedChunks,
                        Aliases = updatedAliases,
                        Description = desc
                    };
                }
            }
            else
            {
                merged[key] = new MergedEntity(
                    entity.Name,
                    entity.Type,
                    [chunkId],
                    [],
                    entity.Description);
            }
        }

        return merged.Values
            .OrderByDescending(e => e.SourceChunks.Count)
            .ThenBy(e => e.CanonicalName)
            .ToList();
    }

    private static string NormalizeEntityName(string name)
    {
        // Remove common prefixes/suffixes and normalize
        var normalized = name.Trim()
            .TrimStart('_')
            .TrimEnd('(', ')');
            
        // Remove method signatures
        var parenIndex = normalized.IndexOf('(');
        if (parenIndex > 0)
            normalized = normalized[..parenIndex];

        return normalized.ToLowerInvariant();
    }

    private static Dictionary<string, List<string>> BuildReferenceGraph(List<StructuredMapOutput> mapOutputs)
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var output in mapOutputs)
        {
            // Functions reference their inputs/outputs
            foreach (var func in output.Functions)
            {
                if (!graph.ContainsKey(func.Name))
                    graph[func.Name] = [];

                if (func.Inputs != null)
                    graph[func.Name].AddRange(func.Inputs);
                if (func.Outputs != null)
                    graph[func.Name].AddRange(func.Outputs);
            }

            // Flows define relationships
            foreach (var flow in output.KeyFlows)
            {
                for (int i = 0; i < flow.Steps.Count - 1; i++)
                {
                    var from = flow.Steps[i];
                    var to = flow.Steps[i + 1];
                    
                    if (!graph.ContainsKey(from))
                        graph[from] = [];
                    
                    if (!graph[from].Contains(to, StringComparer.OrdinalIgnoreCase))
                        graph[from].Add(to);
                }
            }
        }

        return graph;
    }

    private static List<NameCollision> FindNameCollisions(
        List<(EntityReference Entity, string ChunkId)> allEntities)
    {
        var collisions = new List<NameCollision>();

        // Group by normalized name
        var groups = allEntities
            .GroupBy(e => NormalizeEntityName(e.Entity.Name), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            // Check if types differ (indicates collision, not duplicate)
            var types = group.Select(e => e.Entity.Type).Distinct().ToList();
            if (types.Count > 1)
            {
                collisions.Add(new NameCollision(
                    group.First().Entity.Name,
                    group.Select(e => e.ChunkId).Distinct().ToList(),
                    $"Conflicting types: {string.Join(", ", types)}"));
            }
        }

        return collisions;
    }

    private static Dictionary<string, List<string>> BuildCoverageMap(List<StructuredMapOutput> mapOutputs)
    {
        var coverage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var output in mapOutputs)
        {
            // Use heading as topic
            var topic = string.IsNullOrWhiteSpace(output.Heading) ? "General" : output.Heading;
            
            if (!coverage.ContainsKey(topic))
                coverage[topic] = [];
            
            coverage[topic].Add(output.ChunkId);
        }

        return coverage;
    }

    #endregion

    #region Reduce Phase

    private async Task<LossAwareReduceOutput> RunReduceAsync(
        List<StructuredMapOutput> mapOutputs,
        StitcherOutput stitcherOutput)
    {
        // Aggregate all facts with their sources
        var allFacts = mapOutputs
            .SelectMany(m => m.Facts)
            .OrderByDescending(f => f.Confidence)
            .ToList();

        // Find contradictions
        var contradictions = FindContradictions(allFacts);

        // Build coverage report
        var coverageReport = BuildCoverageReport(mapOutputs, stitcherOutput);

        // Generate executive summary from aggregated data
        var executiveSummary = await GenerateExecutiveSummaryAsync(
            mapOutputs, stitcherOutput, allFacts, contradictions);

        // Identify retrieval questions
        var retrievalQuestions = GenerateRetrievalQuestions(mapOutputs);

        // Calculate overall confidence
        var overallConfidence = CalculateOverallConfidence(allFacts, contradictions, coverageReport);

        return new LossAwareReduceOutput(
            executiveSummary,
            contradictions,
            coverageReport,
            retrievalQuestions,
            overallConfidence);
    }

    private static List<Contradiction> FindContradictions(List<FactClaim> allFacts)
    {
        var contradictions = new List<Contradiction>();

        // Simple contradiction detection: look for negation patterns
        for (int i = 0; i < allFacts.Count; i++)
        {
            for (int j = i + 1; j < allFacts.Count; j++)
            {
                var fact1 = allFacts[i];
                var fact2 = allFacts[j];

                if (fact1.SourceChunk == fact2.SourceChunk)
                    continue;

                // Check for potential contradiction patterns
                if (MightContradict(fact1.Statement, fact2.Statement))
                {
                    contradictions.Add(new Contradiction(
                        $"'{fact1.Statement}' vs '{fact2.Statement}'",
                        [fact1.SourceChunk ?? "unknown", fact2.SourceChunk ?? "unknown"],
                        "Different source chunks make conflicting claims"));
                }
            }
        }

        return contradictions;
    }

    private static bool MightContradict(string s1, string s2)
    {
        var s1Lower = s1.ToLowerInvariant();
        var s2Lower = s2.ToLowerInvariant();

        // Check for explicit negation
        var negationPatterns = new[] { "not ", "never ", "cannot ", "doesn't ", "isn't ", "won't ", "shouldn't " };
        
        foreach (var pattern in negationPatterns)
        {
            if ((s1Lower.Contains(pattern) && !s2Lower.Contains(pattern)) ||
                (!s1Lower.Contains(pattern) && s2Lower.Contains(pattern)))
            {
                // Check if they're about the same subject (share significant words)
                var words1 = Regex.Split(s1Lower, @"\W+").Where(w => w.Length > 4).ToHashSet();
                var words2 = Regex.Split(s2Lower, @"\W+").Where(w => w.Length > 4).ToHashSet();
                var overlap = words1.Intersect(words2).Count();
                
                if (overlap >= 2)
                    return true;
            }
        }

        return false;
    }

    private static CoverageReport BuildCoverageReport(
        List<StructuredMapOutput> mapOutputs,
        StitcherOutput stitcherOutput)
    {
        var directlyCovered = mapOutputs
            .Where(m => m.Facts.Count > 0 || m.Entities.Count > 0)
            .Select(m => m.Heading)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct()
            .ToList();

        var inferred = mapOutputs
            .Where(m => m.Facts.Count == 0 && m.Entities.Count == 0 && m.KeyFlows.Count > 0)
            .Select(m => m.Heading)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct()
            .ToList();

        var notCovered = mapOutputs
            .Where(m => m.Facts.Count == 0 && m.Entities.Count == 0 && m.KeyFlows.Count == 0)
            .Select(m => m.Heading)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct()
            .ToList();

        var total = directlyCovered.Count + inferred.Count + notCovered.Count;
        var coverageRatio = total > 0 ? (double)(directlyCovered.Count + inferred.Count * 0.5) / total : 0;

        return new CoverageReport(directlyCovered, inferred, notCovered, coverageRatio);
    }

    private async Task<string> GenerateExecutiveSummaryAsync(
        List<StructuredMapOutput> mapOutputs,
        StitcherOutput stitcherOutput,
        List<FactClaim> allFacts,
        List<Contradiction> contradictions)
    {
        // Build structured input for LLM
        var topFacts = allFacts
            .Where(f => f.Confidence >= ConfidenceLevel.Medium)
            .Take(15)
            .Select(f => $"- {f.Statement} [{f.SourceChunk}] (confidence: {f.Confidence})")
            .ToList();

        var keyEntities = stitcherOutput.Entities
            .Take(10)
            .Select(e => $"- {e.CanonicalName} ({e.Type}): {e.Description ?? "no description"}")
            .ToList();

        var keyFlows = mapOutputs
            .SelectMany(m => m.KeyFlows)
            .Take(5)
            .Select(f => $"- {string.Join(" -> ", f.Steps)} ({f.Type})")
            .ToList();

        var contradictionText = contradictions.Count > 0
            ? $"\n\nCONTRADICTIONS FOUND (preserve both views):\n{string.Join("\n", contradictions.Select(c => $"- {c.Description}"))}"
            : "";

        var prompt = $"""
            Create an executive summary from this structured analysis.

            KEY FACTS (with source citations):
            {string.Join("\n", topFacts)}

            KEY ENTITIES:
            {string.Join("\n", keyEntities)}

            KEY FLOWS/PROCESSES:
            {string.Join("\n", keyFlows)}
            {contradictionText}

            RULES:
            1. Write 3-5 paragraphs covering the main points
            2. Include [chunk-N] citations after each major claim
            3. If contradictions exist, present BOTH views without resolving them
            4. Focus on facts with High/Medium confidence
            5. Be concise but comprehensive
            """;

        return await _ollama.GenerateAsync(prompt);
    }

    private static List<string> GenerateRetrievalQuestions(List<StructuredMapOutput> mapOutputs)
    {
        var questions = new List<string>();

        // Generate questions from uncertainties
        foreach (var output in mapOutputs)
        {
            foreach (var uncertainty in output.Uncertainties)
            {
                if (uncertainty.Type == UncertaintyType.MissingContext ||
                    uncertainty.Type == UncertaintyType.IncompleteData)
                {
                    questions.Add($"What is {uncertainty.Description}?");
                }
            }
        }

        // Limit to top 5
        return questions.Distinct().Take(5).ToList();
    }

    private static ConfidenceLevel CalculateOverallConfidence(
        List<FactClaim> allFacts,
        List<Contradiction> contradictions,
        CoverageReport coverage)
    {
        if (allFacts.Count == 0)
            return ConfidenceLevel.Low;

        var highConfidenceRatio = (double)allFacts.Count(f => f.Confidence == ConfidenceLevel.High) / allFacts.Count;
        var contradictionPenalty = Math.Min(contradictions.Count * 0.1, 0.3);
        var coverageBonus = coverage.CoverageRatio * 0.2;

        var score = highConfidenceRatio - contradictionPenalty + coverageBonus;

        return score switch
        {
            >= 0.7 => ConfidenceLevel.High,
            >= 0.4 => ConfidenceLevel.Medium,
            >= 0.2 => ConfidenceLevel.Low,
            _ => ConfidenceLevel.Uncertain
        };
    }

    #endregion
}

/// <summary>
/// Complete result from structured summarization
/// </summary>
public record StructuredSummaryResult(
    string DocumentId,
    List<StructuredMapOutput> MapOutputs,
    StitcherOutput StitcherOutput,
    LossAwareReduceOutput ReduceOutput,
    TimeSpan ProcessingTime)
{
    /// <summary>
    /// Convert to standard DocumentSummary for compatibility
    /// </summary>
    public DocumentSummary ToDocumentSummary()
    {
        var topicSummaries = MapOutputs
            .Where(m => !string.IsNullOrWhiteSpace(m.Heading))
            .Select(m => new TopicSummary(
                m.Heading,
                string.Join("; ", m.Facts.Take(3).Select(f => f.Statement)),
                [m.ChunkId]))
            .ToList();

        // Extract entities from stitcher output
        var entities = new ExtractedEntities(
            StitcherOutput.Entities.Where(e => e.Type == EntityType.Person).Select(e => e.CanonicalName).ToList(),
            StitcherOutput.Entities.Where(e => e.Type == EntityType.Location).Select(e => e.CanonicalName).ToList(),
            [], // dates
            StitcherOutput.Entities.Where(e => e.Type == EntityType.Event).Select(e => e.CanonicalName).ToList(),
            StitcherOutput.Entities.Where(e => e.Type == EntityType.Organization).Select(e => e.CanonicalName).ToList());

        var trace = new SummarizationTrace(
            DocumentId,
            MapOutputs.Count,
            MapOutputs.Count,
            MapOutputs.Select(m => m.Heading).Where(h => !string.IsNullOrEmpty(h)).ToList(),
            ProcessingTime,
            ReduceOutput.Coverage.CoverageRatio,
            0.0); // Citation rate calculated differently in structured mode

        return new DocumentSummary(
            ReduceOutput.ExecutiveSummary,
            topicSummaries,
            ReduceOutput.RetrievalQuestions,
            trace,
            entities);
    }
}
