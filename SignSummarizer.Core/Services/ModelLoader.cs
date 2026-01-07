using Microsoft.Extensions.Logging;

namespace SignSummarizer.Services;

public interface IModelLoader
{
    Task<string> LoadModelAsync(string modelName, CancellationToken cancellationToken = default);
    bool IsModelAvailable(string modelName);
    string GetModelPath(string modelName);
}

public sealed class ModelLoader : IModelLoader
{
    private readonly ILogger<ModelLoader> _logger;
    private readonly string _modelsDirectory;
    private readonly Dictionary<string, string> _loadedModels = new();
    
    public ModelLoader(ILogger<ModelLoader> logger, string? modelsDirectory = null)
    {
        _logger = logger;
        _modelsDirectory = modelsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SignSummarizer",
            "Models");
        
        Directory.CreateDirectory(_modelsDirectory);
    }
    
    public async Task<string> LoadModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (_loadedModels.TryGetValue(modelName, out var path))
            return path;
        
        var modelPath = Path.Combine(_modelsDirectory, $"{modelName}.onnx");
        
        if (!File.Exists(modelPath))
        {
            _logger.LogInformation("Model {ModelName} not found at {ModelPath}", modelName, modelPath);
            throw new FileNotFoundException($"Model {modelName} not found", modelPath);
        }
        
        _loadedModels[modelName] = modelPath;
        _logger.LogInformation("Loaded model {ModelName} from {ModelPath}", modelName, modelPath);
        
        return modelPath;
    }
    
    public bool IsModelAvailable(string modelName)
    {
        var modelPath = Path.Combine(_modelsDirectory, $"{modelName}.onnx");
        return File.Exists(modelPath);
    }
    
    public string GetModelPath(string modelName)
    {
        return Path.Combine(_modelsDirectory, $"{modelName}.onnx");
    }
}