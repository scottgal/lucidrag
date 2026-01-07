using Microsoft.Extensions.Logging;
using SignSummarizer.Models;
using SignSummarizer.Services;

namespace SignSummarizer.Pipelines;

public sealed class HandPoseWave : ISignWave
{
    private readonly ILogger<HandPoseWave> _logger;
    private readonly IHandDetectionService _handDetectionService;
    
    public string Name => "hand_pose";
    public string Description => "Extracts hand landmarks and confidence scores";
    public int Priority => 100;
    public bool Enabled { get; set; } = true;
    
    public HandPoseWave(
        ILogger<HandPoseWave> logger,
        IHandDetectionService handDetectionService)
    {
        _logger = logger;
        _handDetectionService = handDetectionService;
    }

    public IHandDetectionService GetHandDetectionService() => _handDetectionService;
    
    public async Task<SignWaveResult> ExecuteAsync(
        SignWaveContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(context.VideoPath))
        {
            return SignWaveResult.Failure("No video path provided");
        }
        
        _logger.LogDebug("Executing HandPoseWave for {AtomId}", context.SignAtomId);
        
        try
        {
            var landmarks = await ExtractLandmarksAsync(
                context.VideoPath,
                cancellationToken);
            
            var resultData = new Dictionary<string, object>
            {
                ["landmarks"] = landmarks,
                ["frame_count"] = landmarks.Count,
                ["avg_confidence"] = landmarks
                    .Where(f => f.HasLeftHand || f.HasRightHand)
                    .Average(f => Math.Max(
                        f.LeftHand?.Confidence ?? 0f,
                        f.RightHand?.Confidence ?? 0f)),
                ["hand_presence"] = landmarks.Count(f => f.HasAnyHand) / (double)landmarks.Count
            };
            
            return SignWaveResult.Success(data: resultData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandPoseWave failed");
            return SignWaveResult.Failure(ex.Message);
        }
    }
    
    private async Task<List<FrameLandmarks>> ExtractLandmarksAsync(
        string videoPath,
        CancellationToken cancellationToken)
    {
        var landmarks = new List<FrameLandmarks>();
        
        var captureService = new VideoCaptureService(
            videoPath,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VideoCaptureService>.Instance);
        
        await foreach (var frameInfo in captureService.CaptureFramesAsync(
            targetFps: 15,
            cancellationToken: cancellationToken))
        {
            var (leftHand, rightHand) = await _handDetectionService
                .DetectBothHandsAsync(
                    frameInfo.Image,
                    frameInfo.Index,
                    frameInfo.Timestamp,
                    cancellationToken);
            
            var frameLandmarks = new FrameLandmarks(
                frameInfo.Index,
                frameInfo.Timestamp,
                leftHand,
                rightHand);
            
            landmarks.Add(frameLandmarks);
        }
        
        captureService.Dispose();
        
        return landmarks;
    }
}