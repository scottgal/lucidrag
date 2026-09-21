namespace Mostlylucid.Summarizer.Core.Analysis;

/// <summary>
///     Base interface for pluggable analysis components that produce signals.
///     Extends IWave so all analysis waves are also unified waves.
/// </summary>
public interface IAnalysisWave : IWave
{
    /// <summary>
    ///     Unique name identifying this analysis wave.
    /// </summary>
    new string Name { get; }

    /// <summary>
    ///     Human-readable description.
    /// </summary>
    string Description { get; }

    /// <summary>
    ///     Priority for execution order. Higher priority waves run first (100 = high, 0 = low).
    /// </summary>
    int Priority { get; }

    /// <summary>
    ///     Tags describing category and lane assignment.
    ///     Use "ml" or "llm" for ML lane, "io" for I/O lane, default is "fast" lane.
    /// </summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>
    ///     Whether this wave is enabled.
    /// </summary>
    bool Enabled { get; set; }
}

/// <summary>
///     Analysis wave for in-memory content (markdown text, structured data, etc.).
///     Provides a default IWave.ExecuteAsync that gets content from context cache.
/// </summary>
/// <typeparam name="T">Type of content to analyze.</typeparam>
public interface ITypedAnalysisWave<T> : IAnalysisWave
{
    /// <summary>
    ///     Default IWave bridge: gets typed content from cache key "content" and delegates to AnalyzeAsync.
    /// </summary>
    async Task<IReadOnlyList<Signal>> IWave.ExecuteAsync(WaveContext context, CancellationToken ct)
    {
        var content = context.Cache.Get<T>("content");
        if (content is null) return [];
        var analysisContext = new AnalysisContext { SelectedRoute = context.Route };
        foreach (var signal in context.Signals.All)
            analysisContext.AddSignal(signal);
        var result = await AnalyzeAsync(content, analysisContext, ct);
        return result.ToList();
    }

    /// <summary>
    ///     Check if this wave should run based on cheap preconditions.
    /// </summary>
    bool ShouldRun(T content, AnalysisContext context)
    {
        return true;
    }

    /// <summary>
    ///     Analyze content and produce signals.
    /// </summary>
    Task<IEnumerable<Signal>> AnalyzeAsync(
        T content,
        AnalysisContext context,
        CancellationToken ct = default);
}

/// <summary>
///     Analysis wave for path-based content (files on disk).
///     Provides a default IWave.ExecuteAsync that bridges to AnalyzeAsync.
/// </summary>
public interface IContentAnalysisWave : IAnalysisWave
{
    /// <summary>
    ///     Default IWave bridge: gets file path from context and delegates to AnalyzeAsync.
    /// </summary>
    async Task<IReadOnlyList<Signal>> IWave.ExecuteAsync(WaveContext context, CancellationToken ct)
    {
        if (context.FilePath is null) return [];
        var analysisContext = new AnalysisContext { SelectedRoute = context.Route };
        // Copy existing signals into analysis context
        foreach (var signal in context.Signals.All)
            analysisContext.AddSignal(signal);
        var result = await AnalyzeAsync(context.FilePath, analysisContext, ct);
        return result.ToList();
    }

    /// <summary>
    ///     Check if this wave should run.
    /// </summary>
    bool ShouldRun(string contentPath, AnalysisContext context)
    {
        return true;
    }

    /// <summary>
    ///     Analyze content and produce signals.
    /// </summary>
    Task<IEnumerable<Signal>> AnalyzeAsync(
        string contentPath,
        AnalysisContext context,
        CancellationToken ct = default);
}

/// <summary>
///     Result from wave execution with success/failure tracking.
/// </summary>
public record WaveResult
{
    public bool IsSuccess { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<Signal> Signals { get; init; } = [];
    public TimeSpan Duration { get; init; }
    public string WaveName { get; init; } = "";

    public static WaveResult Success(string waveName, IEnumerable<Signal> signals, TimeSpan duration)
    {
        return new WaveResult
        { IsSuccess = true, WaveName = waveName, Signals = signals.ToList(), Duration = duration };
    }

    public static WaveResult Failure(string waveName, string error, TimeSpan duration)
    {
        return new WaveResult { IsSuccess = false, WaveName = waveName, Error = error, Duration = duration };
    }
}