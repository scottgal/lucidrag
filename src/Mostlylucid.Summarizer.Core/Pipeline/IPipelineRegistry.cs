namespace Mostlylucid.Summarizer.Core.Pipeline;

/// <summary>
/// Registry for discovering and accessing content pipelines.
/// LucidRAG uses this to find the right pipeline for each file type.
/// </summary>
public interface IPipelineRegistry
{
    /// <summary>
    /// Get all registered pipelines.
    /// </summary>
    IReadOnlyList<IPipeline> GetAll();

    /// <summary>
    /// Get a pipeline by its ID.
    /// </summary>
    IPipeline? GetById(string pipelineId);

    /// <summary>
    /// Find a pipeline that can process the given file.
    /// Returns null if no pipeline supports the file type.
    /// </summary>
    IPipeline? FindForFile(string filePath);

    /// <summary>
    /// Get all pipelines that can process the given file.
    /// (Some files may be processable by multiple pipelines)
    /// </summary>
    IReadOnlyList<IPipeline> FindAllForFile(string filePath);
}

/// <summary>
/// Default implementation of pipeline registry.
/// </summary>
public class PipelineRegistry : IPipelineRegistry
{
    private readonly List<IPipeline> _pipelines = [];
    private readonly Dictionary<string, IPipeline> _byId = new(StringComparer.OrdinalIgnoreCase);

    public PipelineRegistry(IEnumerable<IPipeline> pipelines)
    {
        foreach (var pipeline in pipelines)
        {
            _pipelines.Add(pipeline);
            _byId[pipeline.PipelineId] = pipeline;
        }
    }

    public IReadOnlyList<IPipeline> GetAll() => _pipelines;

    public IPipeline? GetById(string pipelineId)
        => _byId.GetValueOrDefault(pipelineId);

    public IPipeline? FindForFile(string filePath)
        => _pipelines.FirstOrDefault(p => p.CanProcess(filePath));

    public IReadOnlyList<IPipeline> FindAllForFile(string filePath)
        => _pipelines.Where(p => p.CanProcess(filePath)).ToList();
}
