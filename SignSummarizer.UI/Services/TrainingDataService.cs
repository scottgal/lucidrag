using Microsoft.Extensions.Logging;

namespace SignSummarizer.UI.Services;

public interface ITrainingDataService
{
    Task<TrainingDataDirectory> GetTrainingDirectoryAsync(CancellationToken cancellationToken = default);
    
    Task<List<string>> DownloadSignLanguageGifsAsync(
        string sign,
        int limit = 25,
        int offset = 0,
        CancellationToken cancellationToken = default);
    
    Task<List<TrainingSample>> GetAllTrainingSamplesAsync(CancellationToken cancellationToken = default);
    
    Task<TrainingSample> CreateTrainingSampleAsync(
        string sign,
        string videoPath,
        string? label = null,
        CancellationToken cancellationToken = default);
}

public sealed record TrainingDataDirectory(
    string Root,
    string Videos,
    string Gifs,
    string Audio,
    string Models,
    string Metadata
);

public sealed record TrainingSample(
    string Sign,
    string VideoPath,
    string? Label,
    DateTime CreatedAt,
    string? GifPath = null,
    string? AudioPath = null
);

public sealed class TrainingDataService : ITrainingDataService
{
    private readonly ILogger<TrainingDataService> _logger;
    private readonly IGiphyDownloader _giphyDownloader;
    private readonly string _trainingRoot;
    
    public TrainingDataService(
        ILogger<TrainingDataService> logger,
        IGiphyDownloader giphyDownloader)
    {
        _logger = logger;
        _giphyDownloader = giphyDownloader;
        
        _trainingRoot = Path.Combine(
            Environment.CurrentDirectory,
            "TrainingData");
        
        InitializeDirectories();
    }
    
    public async Task<TrainingDataDirectory> GetTrainingDirectoryAsync(CancellationToken cancellationToken = default)
    {
        return new TrainingDataDirectory(
            Root: _trainingRoot,
            Videos: Path.Combine(_trainingRoot, "Videos"),
            Gifs: Path.Combine(_trainingRoot, "Gifs"),
            Audio: Path.Combine(_trainingRoot, "Audio"),
            Models: Path.Combine(_trainingRoot, "Models"),
            Metadata: Path.Combine(_trainingRoot, "Metadata"));
    }
    
    public async Task<List<string>> DownloadSignLanguageGifsAsync(
        string sign,
        int limit = 25,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Downloading GIFs for sign: {Sign} (limit: {Limit}, offset: {Offset})", sign, limit, offset);
        
        var dirs = await GetTrainingDirectoryAsync(cancellationToken);
        var signGifDir = Path.Combine(dirs.Gifs, SanitizeSignName(sign));
        
        var downloadedPaths = await _giphyDownloader.DownloadSearchResultsAsync(
            $"#{sign}",
            signGifDir,
            limit,
            offset,
            cancellationToken);
        
        _logger.LogInformation("Downloaded {Count} GIFs for sign: {Sign}", downloadedPaths.Count, sign);
        
        return downloadedPaths;
    }
    
    public async Task<List<TrainingSample>> GetAllTrainingSamplesAsync(CancellationToken cancellationToken = default)
    {
        var dirs = await GetTrainingDirectoryAsync(cancellationToken);
        var samples = new List<TrainingSample>();
        
        if (!Directory.Exists(dirs.Videos))
            return samples;
        
        foreach (var signDir in Directory.GetDirectories(dirs.Videos))
        {
            var sign = Path.GetFileName(signDir);
            
            foreach (var videoFile in Directory.GetFiles(signDir, "*.mp4"))
            {
                var metadataPath = Path.ChangeExtension(videoFile, ".json");
                string? label = null;
                
                if (File.Exists(metadataPath))
                {
                    try
                    {
                        var metadata = await File.ReadAllTextAsync(metadataPath, cancellationToken);
                        var doc = System.Text.Json.JsonDocument.Parse(metadata);
                        if (doc.RootElement.TryGetProperty("label", out var labelProp))
                            label = labelProp.GetString();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read metadata for: {VideoFile}", videoFile);
                    }
                }
                
                samples.Add(new TrainingSample(
                    Sign: sign,
                    VideoPath: videoFile,
                    Label: label,
                    CreatedAt: File.GetCreationTime(videoFile)));
            }
        }
        
        _logger.LogInformation("Found {Count} training samples", samples.Count);
        
        return samples;
    }
    
    public async Task<TrainingSample> CreateTrainingSampleAsync(
        string sign,
        string videoPath,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        var dirs = await GetTrainingDirectoryAsync(cancellationToken);
        var signDir = Path.Combine(dirs.Videos, SanitizeSignName(sign));
        
        Directory.CreateDirectory(signDir);
        
        var fileName = $"{Guid.NewGuid()}.mp4";
        var outputPath = Path.Combine(signDir, fileName);
        
        File.Copy(videoPath, outputPath, overwrite: true);
        
        if (!string.IsNullOrEmpty(label))
        {
            var metadataPath = Path.ChangeExtension(outputPath, ".json");
            var metadata = System.Text.Json.JsonSerializer.Serialize(new { label, sign });
            await File.WriteAllTextAsync(metadataPath, metadata, cancellationToken);
        }
        
        var sample = new TrainingSample(
            Sign: sign,
            VideoPath: outputPath,
            Label: label,
            CreatedAt: DateTime.Now);
        
        _logger.LogInformation("Created training sample: {Sign} - {VideoPath}", sign, outputPath);
        
        return sample;
    }
    
    private void InitializeDirectories()
    {
        Directory.CreateDirectory(_trainingRoot);
        Directory.CreateDirectory(Path.Combine(_trainingRoot, "Videos"));
        Directory.CreateDirectory(Path.Combine(_trainingRoot, "Gifs"));
        Directory.CreateDirectory(Path.Combine(_trainingRoot, "Audio"));
        Directory.CreateDirectory(Path.Combine(_trainingRoot, "Models"));
        Directory.CreateDirectory(Path.Combine(_trainingRoot, "Metadata"));
        
        _logger.LogInformation("Training data directory initialized: {Path}", _trainingRoot);
    }
    
    private static string SanitizeSignName(string sign)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", sign.Split(invalidChars));
    }
}