using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SignSummarizer.Pipelines;

public interface ISignWaveManifest
{
    string Name { get; }
    string Description { get; }
    List<SignWaveDefinition> Waves { get; }
    string? PipelineName { get; }
}

public class SignWaveDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Priority { get; init; } = 100;
    public bool Enabled { get; init; } = true;
    public Dictionary<string, object>? Parameters { get; init; }
}

public sealed record SignWaveManifest(
    string Name,
    string Description,
    List<SignWaveDefinition> Waves,
    string? PipelineName = null) : ISignWaveManifest;

public sealed class SignWaveManifestLoader
{
    private readonly ILogger<SignWaveManifestLoader> _logger;
    private readonly IDeserializer _yamlDeserializer;
    
    public SignWaveManifestLoader(ILogger<SignWaveManifestLoader> logger)
    {
        _logger = logger;
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }
    
    public ISignWaveManifest LoadFromYaml(string yamlPath)
    {
        _logger.LogInformation("Loading wave manifest from: {Path}", yamlPath);
        
        var yaml = File.ReadAllText(yamlPath);
        var waves = _yamlDeserializer.Deserialize<List<SignWaveDefinition>>(yaml);
        
        var name = Path.GetFileNameWithoutExtension(yamlPath);
        var description = waves.Count > 0 
            ? waves[0].Description 
            : "No description";
        
        _logger.LogInformation("Loaded {Count} waves from {Name}", waves.Count, name);
        
        return new SignWaveManifest(name, description, waves);
    }
    
    public List<ISignWaveManifest> LoadFromDirectory(string directory)
    {
        var manifests = new List<ISignWaveManifest>();
        
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("Wave manifest directory not found: {Directory}", directory);
            return manifests;
        }
        
        var yamlFiles = Directory.GetFiles(directory, "*.wave.yaml", SearchOption.TopDirectoryOnly);
        
        foreach (var yamlFile in yamlFiles)
        {
            try
            {
                var manifest = LoadFromYaml(yamlFile);
                manifests.Add(manifest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load wave manifest: {File}", yamlFile);
            }
        }
        
        _logger.LogInformation("Loaded {Count} wave manifests from {Directory}", manifests.Count, directory);
        
        return manifests.OrderBy(m => m.Name).ToList();
    }
    
    public ISignWaveManifest LoadFromAssembly(Assembly assembly, string resourceName)
    {
        _logger.LogInformation("Loading embedded wave manifest: {Resource}", resourceName);
        
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        
        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();
        var waves = _yamlDeserializer.Deserialize<List<SignWaveDefinition>>(yaml);
        
        var name = Path.GetFileNameWithoutExtension(resourceName);
        var description = waves.Count > 0 
            ? waves[0].Description 
            : "No description";
        
        return new SignWaveManifest(name, description, waves);
    }
}