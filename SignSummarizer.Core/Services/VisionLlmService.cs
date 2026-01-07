using Microsoft.Extensions.Logging;
using SignSummarizer.Models;
using SignSummarizer.Services;
using System.Runtime.InteropServices;

namespace SignSummarizer.Services;

public interface IVisionLlmService
{
    Task<string> DescribeAsync(
        string imagePath,
        string? prompt = null,
        CancellationToken cancellationToken = default);
    
    Task<string> DetectObjectsAsync(
        string imagePath,
        CancellationToken cancellationToken = default);
    
    Task<string> GenerateCaptionAsync(
        string imagePath,
        CancellationToken cancellationToken = default);
    
    string ModelName { get; }
    bool IsInitialized { get; }
    Task<bool> InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed class VisionLlmService : IVisionLlmService
{
    private readonly ILogger<VisionLlmService> _logger;
    private OnnxRunner? _captionRunner; // Removed readonly
    private readonly string _modelsDirectory;
    private bool _isInitialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    
    public string ModelName => "Florence-2-base";
    public bool IsInitialized => _isInitialized;
    
    public VisionLlmService(
        IModelLoader modelLoader,
        ILogger<VisionLlmService> logger)
    {
        _logger = logger;
        _modelsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SignSummarizer",
            "VisionModels");
        
        Directory.CreateDirectory(_modelsDirectory);
        
        try
        {
            InitializeCaptionModel(modelLoader);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize vision LLM models");
        }
    }
    
    private void InitializeCaptionModel(IModelLoader modelLoader)
    {
        try
        {
            var captionModelPath = modelLoader.LoadModelAsync("florence2_caption").GetAwaiter().GetResult();
            _captionRunner = new OnnxRunner(
                captionModelPath,
                "pixel_values",
                "logits",
                new[] { 1, 3, 384, 384 });
            
            _logger.LogInformation("Initialized Florence-2 caption model");
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Florence-2 caption model not available");
        }
    }
    
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        
        try
        {
            if (_isInitialized)
                return true;
            
            _logger.LogInformation("Initializing Vision LLM service");
            
            await Task.Delay(100);
            _isInitialized = _captionRunner != null;
            
            return _isInitialized;
        }
        finally
        {
            _initLock.Release();
        }
    }
    
    public async Task<string> DescribeAsync(
        string imagePath,
        string? prompt = null,
        CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            await InitializeAsync(cancellationToken);
            if (!_isInitialized)
                throw new InvalidOperationException("Vision LLM service not available");
        }
        
        _logger.LogInformation("Describing image: {ImagePath}", imagePath);
        
        try
        {
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("Image file not found", imagePath);
            
            var defaultPrompt = prompt ?? "Describe this sign language gesture in detail, including hand position, shape, movement, and any facial expressions.";
            
            var description = await GenerateCaptionWithFallback(
                imagePath,
                defaultPrompt,
                cancellationToken);
            
            _logger.LogDebug("Image description: {Description}", description);
            
            return description;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to describe image");
            return $"Error generating description: {ex.Message}";
        }
    }
    
    public async Task<string> DetectObjectsAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            await InitializeAsync(cancellationToken);
            if (!_isInitialized)
                throw new InvalidOperationException("Vision LLM service not available");
        }
        
        _logger.LogInformation("Detecting objects in: {ImagePath}", imagePath);
        
        return "Object detection not implemented - using caption instead";
    }
    
    public async Task<string> GenerateCaptionAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        return await DescribeAsync(
            imagePath,
            "Generate a detailed caption for this image, focusing on main subject and action.",
            cancellationToken);
    }
    
    private async Task<string> GenerateCaptionWithFallback(
        string imagePath,
        string prompt,
        CancellationToken cancellationToken)
    {
        if (_captionRunner == null)
        {
            return "Florence-2 model not available. Description unavailable.";
        }
        
        try
        {
            var input = PreprocessImage(imagePath);
            var output = _captionRunner.Run(input);
            
            var caption = DecodeCaption(output);
            
            return string.IsNullOrEmpty(caption)
                ? "Sign language gesture detected (unable to generate detailed description)"
                : caption;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Caption generation failed");
            return "Sign language gesture detected (caption generation failed)";
        }
    }
    
    private float[] PreprocessImage(string imagePath)
    {
        try
        {
            using var image = OpenCvSharp.Cv2.ImRead(imagePath);
            
            if (image.Empty())
            {
                _logger.LogWarning("Failed to load image: {ImagePath}", imagePath);
                return new float[3 * 384 * 384];
            }
            
            using var resized = new OpenCvSharp.Mat();
            using var normalized = new OpenCvSharp.Mat();
            
            OpenCvSharp.Cv2.Resize(image, resized, new OpenCvSharp.Size(384, 384));
            OpenCvSharp.Cv2.CvtColor(resized, normalized, OpenCvSharp.ColorConversionCodes.BGR2RGB);
            
            var input = new float[3 * 384 * 384];
            
            int totalBytes = (int)(normalized.Total() * normalized.ElemSize());
            byte[] pixelData = new byte[totalBytes];
            Marshal.Copy(normalized.Data, pixelData, 0, totalBytes);
            
            for (int i = 0; i < pixelData.Length; i++)
            {
                input[i] = pixelData[i] / 255.0f;
            }
            
            return input;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to preprocess image: {ImagePath}", imagePath);
            return new float[3 * 384 * 384];
        }
    }
    
    private string DecodeCaption(float[] output)
    {
        var tokenIds = new List<int>();
        
        for (int i = 0; i < output.Length; i++)
        {
            if (output[i] > 0 && output[i] < output.Length)
            {
                tokenIds.Add((int)output[i]);
            }
        }
        
        var text = new System.Text.StringBuilder();
        
        foreach (var tokenId in tokenIds)
        {
            if (tokenId < 256)
            {
                text.Append((char)tokenId);
            }
            else
            {
                text.Append($"[UNK_{tokenId}]");
            }
        }
        
        return text.ToString();
    }
    
    public void Dispose()
    {
        _captionRunner?.Dispose();
        _initLock.Dispose();
    }
}
