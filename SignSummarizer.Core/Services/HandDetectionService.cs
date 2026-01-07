using Microsoft.Extensions.Logging;
using OpenCvSharp;
using SignSummarizer.Models;

namespace SignSummarizer.Services;

public interface IHandDetectionService
{
    Task<HandLandmarks?> DetectHandLandmarksAsync(OpenCvSharp.Mat frame, int frameIndex, TimeSpan timestamp, CancellationToken cancellationToken = default);
    Task<(HandLandmarks? Left, HandLandmarks? Right)> DetectBothHandsAsync(OpenCvSharp.Mat frame, int frameIndex, TimeSpan timestamp, CancellationToken cancellationToken = default);
}

public sealed class HandDetectionService : IHandDetectionService, IDisposable
{
    private readonly OnnxRunner? _landmarkRunner;
    private readonly ILogger<HandDetectionService> _logger;
    private readonly float _confidenceThreshold;
    
    public HandDetectionService(
        IModelLoader modelLoader,
        ILogger<HandDetectionService> logger,
        string landmarkModelName = "hand_landmarks",
        float confidenceThreshold = 0.5f)
    {
        _logger = logger;
        _confidenceThreshold = confidenceThreshold;
        
        try
        {
            var modelPath = modelLoader.LoadModelAsync(landmarkModelName).GetAwaiter().GetResult();
            _landmarkRunner = new OnnxRunner(
                modelPath,
                "input",
                "output",
                new[] { 1, 3, 256, 256 });
            
            _logger.LogInformation("Hand detection service initialized with model: {Model}", landmarkModelName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize hand detection service");
            _landmarkRunner = null;
        }
    }
    
    public async Task<HandLandmarks?> DetectHandLandmarksAsync(
        OpenCvSharp.Mat frame,
        int frameIndex,
        TimeSpan timestamp,
        CancellationToken cancellationToken = default)
    {
        if (_landmarkRunner == null)
        {
            _logger.LogWarning("Hand detection not available - no model loaded");
            return null;
        }
        
        var input = PreprocessFrame(frame);
        var output = _landmarkRunner.Run(input);
        
        var landmarks = ParseLandmarks(output, HandSide.Unknown, frameIndex, timestamp);
        
        if (landmarks?.Confidence < _confidenceThreshold)
            return null;
        
        return landmarks;
    }
    
    public async Task<(HandLandmarks? Left, HandLandmarks? Right)> DetectBothHandsAsync(
        OpenCvSharp.Mat frame,
        int frameIndex,
        TimeSpan timestamp,
        CancellationToken cancellationToken = default)
    {
        var detected = await DetectHandLandmarksAsync(frame, frameIndex, timestamp, cancellationToken);
        
        if (detected == null)
            return (null, null);
        
        return (detected.Side == HandSide.Left ? detected : null,
                detected.Side == HandSide.Right ? detected : null);
    }
    
    private float[] PreprocessFrame(OpenCvSharp.Mat frame)
    {
        var resized = frame.Resize(new OpenCvSharp.Size(256, 256));
        var normalized = new float[3 * 256 * 256];
        
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < 256 * 256; i++)
            {
                var val = resized.At<Vec3b>(i / 256, i % 256)[2 - c];
                normalized[c * 256 * 256 + i] = val / 255.0f;
            }
        }
        
        resized.Dispose();
        return normalized;
    }
    
    private HandLandmarks? ParseLandmarks(
        float[] output,
        HandSide side,
        int frameIndex,
        TimeSpan timestamp)
    {
        if (output.Length < 63)
            return null;
        
        var points = new Point3D[21];
        for (int i = 0; i < 21; i++)
        {
            points[i] = new Point3D(
                output[i * 3],
                output[i * 3 + 1],
                output[i * 3 + 2]);
        }
        
        var confidence = output.Length > 63 ? output[63] : 1.0f;
        
        return new HandLandmarks(points, side, confidence, frameIndex, timestamp);
    }
    
    public void Dispose()
    {
        _landmarkRunner?.Dispose();
    }
}