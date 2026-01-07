using Microsoft.Extensions.Logging;
using OpenCvSharp;
using SignSummarizer.Models;

namespace SignSummarizer.Services;

public interface IModifierDetectionService
{
    NonManualModifiers? DetectModifiers(OpenCvSharp.Mat frame, TimeSpan timestamp);
    Task<NonManualModifiers?> DetectModifiersAsync(OpenCvSharp.Mat frame, TimeSpan timestamp, CancellationToken cancellationToken = default);
}

public sealed class ModifierDetectionService : IModifierDetectionService
{
    private readonly ILogger<ModifierDetectionService> _logger;
    private readonly OnnxRunner? _faceRunner;
    private readonly OnnxRunner? _poseRunner;
    private readonly float _confidenceThreshold;
    
    public ModifierDetectionService(
        IModelLoader modelLoader,
        ILogger<ModifierDetectionService> logger,
        string faceModelName = "face_landmarks",
        string poseModelName = "pose_landmarks",
        float confidenceThreshold = 0.5f)
    {
        _logger = logger;
        _confidenceThreshold = confidenceThreshold;
        
        try
        {
            if (modelLoader.IsModelAvailable(faceModelName))
            {
                var faceModelPath = modelLoader.LoadModelAsync(faceModelName).GetAwaiter().GetResult();
                _faceRunner = new OnnxRunner(
                    faceModelPath,
                    "input",
                    "output",
                    new[] { 1, 3, 256, 256 });
            }
            
            if (modelLoader.IsModelAvailable(poseModelName))
            {
                var poseModelPath = modelLoader.LoadModelAsync(poseModelName).GetAwaiter().GetResult();
                _poseRunner = new OnnxRunner(
                    poseModelPath,
                    "input",
                    "output",
                    new[] { 1, 3, 256, 256 });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize modifier detection models");
        }
    }
    
    public NonManualModifiers? DetectModifiers(OpenCvSharp.Mat frame, TimeSpan timestamp)
    {
        try
        {
            var browPosition = DetectBrowPosition(frame);
            var headMotion = HeadMotion.None;
            var mouthShape = DetectMouthShape(frame);
            var torsoShift = DetectTorsoShift(frame);
            
            var modifiers = new NonManualModifiers
            {
                BrowPosition = browPosition,
                HeadMotion = headMotion,
                MouthShape = mouthShape,
                TorsoShift = torsoShift,
                Timestamp = timestamp,
                Confidence = 0.7f
            };
            
            return modifiers.HasModifiers ? modifiers : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting modifiers");
            return null;
        }
    }
    
    public Task<NonManualModifiers?> DetectModifiersAsync(
        OpenCvSharp.Mat frame,
        TimeSpan timestamp,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(DetectModifiers(frame, timestamp));
    }
    
    private BrowPosition DetectBrowPosition(OpenCvSharp.Mat frame)
    {
        if (_faceRunner == null)
            return BrowPosition.Neutral;
        
        try
        {
            var input = PreprocessFrame(frame);
            var output = _faceRunner.Run(input);
            
            if (output.Length >= 2)
            {
                var leftBrowY = output[0];
                var rightBrowY = output[1];
                var avgBrowY = (leftBrowY + rightBrowY) / 2;
                
                if (avgBrowY < 0.4f)
                    return BrowPosition.Raised;
                if (avgBrowY > 0.6f)
                    return BrowPosition.Furrowed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error detecting brow position");
        }
        
        return BrowPosition.Neutral;
    }
    
    private MouthShape DetectMouthShape(OpenCvSharp.Mat frame)
    {
        if (_faceRunner == null)
            return MouthShape.Unknown;
        
        try
        {
            var input = PreprocessFrame(frame);
            var output = _faceRunner.Run(input);
            
            if (output.Length >= 4)
            {
                var upperLipY = output[2];
                var lowerLipY = output[3];
                var mouthHeight = Math.Abs(lowerLipY - upperLipY);
                
                if (mouthHeight > 0.1f)
                    return MouthShape.Open;
                if (mouthHeight < 0.02f)
                    return MouthShape.Closed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error detecting mouth shape");
        }
        
        return MouthShape.Neutral;
    }
    
    private float DetectTorsoShift(OpenCvSharp.Mat frame)
    {
        if (_poseRunner == null)
            return 0f;
        
        try
        {
            var input = PreprocessFrame(frame);
            var output = _poseRunner.Run(input);
            
            if (output.Length >= 1)
            {
                return output[0] - 0.5f;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error detecting torso shift");
        }
        
        return 0f;
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
}