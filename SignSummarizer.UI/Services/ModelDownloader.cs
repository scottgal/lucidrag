using Microsoft.Extensions.Logging;

namespace SignSummarizer.UI.Services;

public interface IModelDownloader
{
    Task<string> DownloadWhisperModelAsync(
        WhisperModel model,
        string outputDirectory,
        CancellationToken cancellationToken = default);
    
    Task<bool> ModelExistsAsync(WhisperModel model, CancellationToken cancellationToken = default);
    
    Task<string> DownloadModelAsync(
        string url,
        string fileName,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}

public sealed class ModelDownloader : IModelDownloader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ModelDownloader> _logger;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    
    private static readonly Dictionary<WhisperModel, string> ModelUrls = new()
    {
        { WhisperModel.Tiny, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin" },
        { WhisperModel.TinyEn, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin" },
        { WhisperModel.Base, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin" },
        { WhisperModel.BaseEn, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin" },
        { WhisperModel.Small, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin" },
        { WhisperModel.SmallEn, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin" },
        { WhisperModel.Medium, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin" },
        { WhisperModel.MediumEn, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.en.bin" },
        { WhisperModel.LargeV2, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v2.bin" },
        { WhisperModel.LargeV3, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin" }
    };
    
    private static readonly Dictionary<WhisperModel, (int SizeMB, string Description)> ModelInfo = new()
    {
        { WhisperModel.Tiny, (39, "Tiny - Fastest, ~32x faster than large") },
        { WhisperModel.TinyEn, (39, "Tiny (English) - Fastest English-only") },
        { WhisperModel.Base, (74, "Base - Good balance of speed/accuracy") },
        { WhisperModel.BaseEn, (74, "Base (English) - Good English-only balance") },
        { WhisperModel.Small, (244, "Small - ~6x faster than large") },
        { WhisperModel.SmallEn, (244, "Small (English) - ~6x faster large (English)") },
        { WhisperModel.Medium, (769, "Medium - ~2x faster than large") },
        { WhisperModel.MediumEn, (769, "Medium (English) - ~2x faster large (English)") },
        { WhisperModel.LargeV2, (1550, "Large v2 - Best accuracy, slowest") },
        { WhisperModel.LargeV3, (1550, "Large v3 - Best accuracy, slowest") }
    };
    
    public ModelDownloader(HttpClient httpClient, ILogger<ModelDownloader> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }
    
    public async Task<bool> ModelExistsAsync(WhisperModel model, CancellationToken cancellationToken = default)
    {
        var modelPath = GetModelPath(model);
        return File.Exists(modelPath);
    }
    
    public async Task<string> DownloadWhisperModelAsync(
        WhisperModel model,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!ModelUrls.TryGetValue(model, out var url))
        {
            throw new ArgumentException($"Unknown Whisper model: {model}");
        }
        
        var fileName = Path.GetFileName(url);
        var outputPath = Path.Combine(outputDirectory, fileName);
        
        Directory.CreateDirectory(outputDirectory);
        
        if (File.Exists(outputPath))
        {
            _logger.LogInformation("Whisper model already exists: {FileName}", fileName);
            return outputPath;
        }
        
        var (size, description) = ModelInfo[model];
        _logger.LogInformation("Downloading Whisper {Model}: {Description} ({Size}MB) from {Url}", 
            model, description, size, url);
        
        await _downloadLock.WaitAsync(cancellationToken);
        
        try
        {
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var totalBytes = response.Content.Headers.ContentLength ?? size * 1024 * 1024;
            
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            
            var buffer = new byte[8192];
            var totalRead = 0L;
            var lastProgress = 0;
            
            int read;
            while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
                
                var progress = (int)((totalRead * 100) / totalBytes);
                if (progress > lastProgress)
                {
                    _logger.LogInformation("Download progress: {Progress}% ({DownloadedMB:F1}MB / {TotalMB:F1}MB)", 
                        progress, totalRead / 1024.0 / 1024.0, totalBytes / 1024.0 / 1024.0);
                    lastProgress = progress;
                }
            }
            
            _logger.LogInformation("Whisper model downloaded successfully: {FileName}", fileName);
            
            return outputPath;
        }
        finally
        {
            _downloadLock.Release();
        }
    }
    
    public async Task<string> DownloadModelAsync(
        string url,
        string fileName,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, fileName);
        
        if (File.Exists(outputPath))
        {
            _logger.LogInformation("Model already exists: {FileName}", fileName);
            return outputPath;
        }
        
        _logger.LogInformation("Downloading model from {Url}", url);
        
        var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        
        await contentStream.CopyToAsync(fileStream, cancellationToken);
        
        _logger.LogInformation("Model downloaded to: {OutputPath}", outputPath);
        
        return outputPath;
    }
    
    private static string GetModelPath(WhisperModel model)
    {
        return model switch
        {
            WhisperModel.Tiny => "ggml-tiny.bin",
            WhisperModel.TinyEn => "ggml-tiny.en.bin",
            WhisperModel.Base => "ggml-base.bin",
            WhisperModel.BaseEn => "ggml-base.en.bin",
            WhisperModel.Small => "ggml-small.bin",
            WhisperModel.SmallEn => "ggml-small.en.bin",
            WhisperModel.Medium => "ggml-medium.bin",
            WhisperModel.MediumEn => "ggml-medium.en.bin",
            WhisperModel.LargeV2 => "ggml-large-v2.bin",
            WhisperModel.LargeV3 => "ggml-large-v3.bin",
            _ => "ggml-tiny.en.bin"
        };
    }
}