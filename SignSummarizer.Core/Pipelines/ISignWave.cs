using SignSummarizer.Models;

namespace SignSummarizer.Pipelines;

public interface ISignWave
{
    string Name { get; }
    string Description { get; }
    int Priority { get; }
    bool Enabled { get; }
    Task<SignWaveResult> ExecuteAsync(SignWaveContext context, CancellationToken cancellationToken);
}

public record SignWaveResult
{
    public bool IsSuccess { get; init; }
    public string? Error { get; init; }
    public Dictionary<string, object>? Data { get; init; }
    
    public T? GetData<T>(string key) where T : class
    {
        if (Data?.TryGetValue(key, out var value) == true && value is T typed)
            return typed;
        return null;
    }
    
    public T? GetScalar<T>(string key) where T : struct
    {
        if (Data?.TryGetValue(key, out var value) == true)
        {
            if (value is T typed)
                return typed;
        }
        return default;
    }
    
    public static SignWaveResult Success(Dictionary<string, object>? data = null)
    {
        return new SignWaveResult { IsSuccess = true, Data = data };
    }
    
    public static SignWaveResult Failure(string error)
    {
        return new SignWaveResult { IsSuccess = false, Error = error };
    }
}

public class SignWaveContext
{
    public Guid SignAtomId { get; init; }
    public string? VideoPath { get; init; }
    public string? GifPath { get; init; }
    public List<FrameLandmarks> FrameLandmarks { get; init; } = new();
    public SignAtom? SignAtom { get; init; }
    public PoseFilmstrip? Filmstrip { get; init; }
    public float[]? PoseEmbedding { get; init; }
    public List<string>? GlossCandidates { get; init; }
    public Dictionary<string, object> Cache { get; } = new();
    
    public void SetCached<T>(string key, T value) where T : class
    {
        Cache[key] = value;
    }
    
    public T? GetCached<T>(string key) where T : class
    {
        if (Cache.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return null;
    }
}
