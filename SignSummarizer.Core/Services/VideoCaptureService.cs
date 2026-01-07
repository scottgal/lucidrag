using Microsoft.Extensions.Logging;
using OpenCvSharp;
using SignSummarizer.Models;

namespace SignSummarizer.Services;

public sealed class VideoCaptureService : IDisposable
{
    private VideoCapture? _capture;
    private readonly string _videoPath;
    private readonly ILogger<VideoCaptureService> _logger;
    
    public VideoCaptureService(string videoPath, ILogger<VideoCaptureService> logger)
    {
        _videoPath = videoPath;
        _logger = logger;
    }
    
    public async IAsyncEnumerable<FrameInfo> CaptureFramesAsync(
        int targetFps = 30,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _capture = new VideoCapture(_videoPath);
        
        if (!_capture.IsOpened())
            throw new InvalidOperationException($"Failed to open video: {_videoPath}");
        
        var frameCount = _capture.FrameCount;
        var fps = _capture.Fps;
        var frameInterval = (int)(1000.0 / Math.Min(targetFps, fps));
        var totalDuration = TimeSpan.FromSeconds(frameCount / fps);
        
        int frameIndex = 0;
        using var frame = new Mat();
        
        while (_capture.Read(frame) && !frame.Empty() && !cancellationToken.IsCancellationRequested)
        {
            var timestamp = TimeSpan.FromSeconds(frameIndex / fps);
            yield return new FrameInfo(frameIndex, timestamp, frame);
            frameIndex++;
        }
    }
    
    public void Dispose()
    {
        _capture?.Dispose();
    }
}

public sealed record FrameInfo(int Index, TimeSpan Timestamp, Mat Image);