using Microsoft.Extensions.Logging;
using SignSummarizer.Models;
using SignSummarizer.Services;

namespace SignSummarizer.Pipelines;

public sealed class ModifierDetectionWave : ISignWave
{
    private readonly ILogger<ModifierDetectionWave> _logger;
    private readonly IModifierDetectionService _modifierDetectionService;
    
    public string Name => "modifier_detection";
    public string Description => "Detects non-manual sign modifiers";
    public int Priority => 350;
    public bool Enabled { get; set; } = true;
    
    public ModifierDetectionWave(
        ILogger<ModifierDetectionWave> logger,
        IModifierDetectionService modifierDetectionService)
    {
        _logger = logger;
        _modifierDetectionService = modifierDetectionService;
    }
    
    public Task<SignWaveResult> ExecuteAsync(
        SignWaveContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing ModifierDetectionWave for {AtomId}", context.SignAtomId);
        
        try
        {
            var existingModifiers = context.FrameLandmarks
                .Select(f => f.Modifiers)
                .Where(m => m != null && m.HasModifiers)
                .ToList();
            
            if (existingModifiers.Count == 0)
            {
                return Task.FromResult(SignWaveResult.Success(
                    data: new Dictionary<string, object>
                    {
                        ["modifiers"] = null,
                        ["modifier_count"] = 0
                    }));
            }
            
            var avgModifiers = AggregateModifiers(existingModifiers!);
            
            var resultData = new Dictionary<string, object>
            {
                ["modifiers"] = avgModifiers,
                ["modifier_count"] = existingModifiers.Count,
                ["has_modifiers"] = avgModifiers.HasModifiers,
                ["dominant_brow"] = existingModifiers
                    .GroupBy(m => m!.BrowPosition)
                    .OrderByDescending(g => g.Count())
                    .First().Key,
                ["dominant_head_motion"] = existingModifiers
                    .GroupBy(m => m!.HeadMotion)
                    .OrderByDescending(g => g.Count())
                    .First().Key
            };
            
            return Task.FromResult(SignWaveResult.Success(data: resultData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifierDetectionWave failed");
            return Task.FromResult(SignWaveResult.Failure(ex.Message));
        }
    }
    
    private NonManualModifiers AggregateModifiers(List<NonManualModifiers> modifiers)
    {
        return new NonManualModifiers
        {
            BrowPosition = modifiers
                .GroupBy(m => m.BrowPosition)
                .OrderByDescending(g => g.Count())
                .First().Key,
            
            HeadMotion = modifiers
                .GroupBy(m => m.HeadMotion)
                .OrderByDescending(g => g.Count())
                .First().Key,
            
            MouthShape = modifiers
                .GroupBy(m => m.MouthShape)
                .OrderByDescending(g => g.Count())
                .First().Key,
            
            TorsoShift = modifiers.Average(m => m.TorsoShift),
            Confidence = modifiers.Average(m => m.Confidence),
            Timestamp = modifiers[0].Timestamp
        };
    }
}

public sealed class RagRetrievalWave : ISignWave
{
    private readonly ILogger<RagRetrievalWave> _logger;
    private readonly ISignVectorStore _vectorStore;
    
    public string Name => "rag_retrieval";
    public string Description => "Retrieves similar signs from vector store";
    public int Priority => 450;
    public bool Enabled { get; set; } = true;
    
    public RagRetrievalWave(
        ILogger<RagRetrievalWave> logger,
        ISignVectorStore vectorStore)
    {
        _logger = logger;
        _vectorStore = vectorStore;
    }
    
    public async Task<SignWaveResult> ExecuteAsync(
        SignWaveContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing RagRetrievalWave for {AtomId}", context.SignAtomId);
        
        try
        {
            var atom = context.SignAtom ?? new SignAtom(
                AtomType.Hold,
                TimeSpan.Zero,
                TimeSpan.Zero);
            
            if (atom.PoseEmbedding == null)
            {
                return SignWaveResult.Failure("No embedding available for RAG retrieval");
            }
            
            var matches = await _vectorStore.FindMatchesAsync(
                atom,
                topK: 10,
                cancellationToken: cancellationToken);
            
            var similarSigns = matches
                .Where(m => m.Similarity > 0.6f)
                .Take(5)
                .ToList();
            
            var resultData = new Dictionary<string, object>
            {
                ["similar_signs"] = similarSigns,
                ["total_matches"] = matches.Count,
                ["high_confidence_matches"] = matches.Count(m => m.Similarity > 0.8f),
                ["avg_similarity"] = matches.Count > 0 ? matches.Average(m => m.Similarity) : 0f
            };
            
            return SignWaveResult.Success(data: resultData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RagRetrievalWave failed");
            return SignWaveResult.Failure(ex.Message);
        }
    }
}