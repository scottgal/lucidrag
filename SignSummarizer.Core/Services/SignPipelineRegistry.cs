using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SignSummarizer.Pipelines;
using SignSummarizer.Models;

namespace SignSummarizer.Services;

public interface ISignPipelineRegistry
{
    ISignProcessingPipeline? GetStandardPipeline();
    void RegisterPipeline(ISignProcessingPipeline pipeline);
}

public sealed class SignPipelineRegistry : ISignPipelineRegistry
{
    private readonly ConcurrentDictionary<string, ISignProcessingPipeline> _pipelines;
    
    public SignPipelineRegistry()
    {
        _pipelines = new ConcurrentDictionary<string, ISignProcessingPipeline>();
    }
    
    public ISignProcessingPipeline? GetStandardPipeline()
    {
        return _pipelines.TryGetValue("standard", out var pipeline) ? pipeline : null;
    }
    
    public void RegisterPipeline(ISignProcessingPipeline pipeline)
    {
        _pipelines.TryAdd("standard", pipeline);
    }
}

public interface ISignProcessingPipeline
{
    IAsyncEnumerable<SignAtom> ProcessVideoAsync(
        string videoPath,
        string? signerId = null,
        CancellationToken cancellationToken = default);
}

public sealed class SignProcessingPipeline : ISignProcessingPipeline
{
    private readonly ISignWaveCoordinator _waveCoordinator;
    private readonly ISignVectorStore _vectorStore;
    
    public SignProcessingPipeline(
        ISignWaveCoordinator waveCoordinator,
        ISignVectorStore vectorStore)
    {
        _waveCoordinator = waveCoordinator;
        _vectorStore = vectorStore;
        
        var registry = new SignPipelineRegistry();
        registry.RegisterPipeline(this);
    }
    
    public async IAsyncEnumerable<SignAtom> ProcessVideoAsync(
        string videoPath,
        string? signerId = null,
        CancellationToken cancellationToken = default)
    {
        var atoms = await _waveCoordinator.ProcessVideoAsync(
            videoPath,
            cancellationToken: cancellationToken);
        
        foreach (var atom in atoms)
        {
            await _vectorStore.StoreAsync(atom, signerId, cancellationToken);
            yield return atom;
        }
    }
}
