using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SignSummarizer.UI.Models;
using SignSummarizer.UI.Services;

namespace SignSummarizer.UI.ViewModels;

public partial class VideoPlayerViewModel : ObservableObject
{
    private readonly IVideoPlayerService _videoPlayerService;
    private readonly IWhisperService _whisperService;
    
    [ObservableProperty]
    private string _videoPath = string.Empty;
    
    [ObservableProperty]
    private bool _isVideoLoaded;
    
    [ObservableProperty]
    private bool _isLoading;
    
    [ObservableProperty]
    private bool _isPlaying;
    
    [ObservableProperty]
    private bool _isMuted;
    
    [ObservableProperty]
    private bool _isGif;
    
    [ObservableProperty]
    private bool _areSubtitlesVisible;
    
    [ObservableProperty]
    private TimeSpan _currentTime;
    
    [ObservableProperty]
    private TimeSpan _duration;
    
    [ObservableProperty]
    private string _currentTimeString = "00:00";
    
    [ObservableProperty]
    private string _durationString = "00:00";
    
    [ObservableProperty]
    private string _currentSubtitle = string.Empty;
    
    [ObservableProperty]
    private string _statusMessage = "Ready";
    
    [ObservableProperty]
    private double _volume = 1.0;
    
    [ObservableProperty]
    private double _playbackSpeed = 1.0;
    
    private readonly ObservableCollection<SubtitleSegment> _subtitles = new();
    private Timer? _playbackTimer;
    private int _currentSubtitleIndex = -1;
    
    public VideoPlayerViewModel(
        IVideoPlayerService videoPlayerService,
        IWhisperService whisperService)
    {
        _videoPlayerService = videoPlayerService;
        _whisperService = whisperService;
        
        _subtitles.CollectionChanged += (s, e) => OnPropertyChanged(nameof(SubtitlesCount));
    }
    
    public int SubtitlesCount => _subtitles.Count;
    
    public IAsyncRelayCommand LoadVideoCommand => new AsyncRelayCommand<string>(async (path) =>
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;
        
        await LoadVideoAsync(path);
    });
    
    public IAsyncRelayCommand GenerateSubtitlesCommand => new AsyncRelayCommand(async () =>
    {
        if (string.IsNullOrEmpty(VideoPath) || !File.Exists(VideoPath))
            return;
        
        await GenerateSubtitlesAsync();
    });
    
    public IRelayCommand PlayPauseCommand => new RelayCommand(() =>
    {
        IsPlaying = !IsPlaying;
        StatusMessage = IsPlaying ? "Playing" : "Paused";
        UpdatePlaybackTimer();
    });
    
    public IRelayCommand StopCommand => new RelayCommand(() =>
    {
        IsPlaying = false;
        CurrentTime = TimeSpan.Zero;
        StatusMessage = "Stopped";
        UpdateCurrentSubtitle();
        _playbackTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    });
    
    public IRelayCommand ToggleMuteCommand => new RelayCommand(() =>
    {
        IsMuted = !IsMuted;
        Volume = IsMuted ? 0.0 : 1.0;
    });
    
    public IRelayCommand ToggleSubtitlesCommand => new RelayCommand(() =>
    {
        AreSubtitlesVisible = !AreSubtitlesVisible;
    });
    
    public IRelayCommand SkipBackwardCommand => new RelayCommand(() =>
    {
        CurrentTime = TimeSpan.FromSeconds(Math.Max(0, CurrentTime.TotalSeconds - 5));
        UpdateCurrentSubtitle();
    });
    
    public IRelayCommand SkipForwardCommand => new RelayCommand(() =>
    {
        CurrentTime = TimeSpan.FromSeconds(
            Math.Min(Duration.TotalSeconds, CurrentTime.TotalSeconds + 5));
        UpdateCurrentSubtitle();
    });
    
    public IRelayCommand SeekToStartCommand => new RelayCommand(() =>
    {
        CurrentTime = TimeSpan.Zero;
        UpdateCurrentSubtitle();
    });
    
    public IRelayCommand SeekToEndCommand => new RelayCommand(() =>
    {
        CurrentTime = Duration;
        UpdateCurrentSubtitle();
    });
    
    private async Task LoadVideoAsync(string path)
    {
        StatusMessage = "Loading...";
        IsLoading = true;
        
        try
        {
            VideoPath = path;
            IsGif = Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
            Duration = await GetVideoDurationAsync(path);
            IsVideoLoaded = true;
            CurrentTime = TimeSpan.Zero;
            
            StatusMessage = $"Loaded: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading video: {ex.Message}";
            IsVideoLoaded = false;
        }
        finally
        {
            IsLoading = false;
        }
    }
    
    private async Task<TimeSpan> GetVideoDurationAsync(string path)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            if (double.TryParse(output, out var seconds))
                return TimeSpan.FromSeconds(seconds);
            
            return TimeSpan.Zero;
        }
        catch
        {
            return TimeSpan.FromSeconds(30); // Default duration
        }
    }
    
    private async Task GenerateSubtitlesAsync()
    {
        StatusMessage = "Generating subtitles...";
        IsLoading = true;
        IsPlaying = false;
        
        try
        {
            await _whisperService.InitializeAsync();
            
            var segments = await _whisperService.TranscribeAsync(
                VideoPath,
                "en",
                CancellationToken.None);
            
            _subtitles.Clear();
            foreach (var segment in segments)
            {
                _subtitles.Add(segment);
            }
            
            StatusMessage = $"Generated {segments.Count} subtitle segments";
            AreSubtitlesVisible = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error generating subtitles: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
    
    private void UpdatePlaybackTimer()
    {
        _playbackTimer?.Dispose();
        
        if (IsPlaying)
        {
            _playbackTimer = new Timer(state =>
            {
                if (IsPlaying && CurrentTime < Duration)
                {
                    CurrentTime += TimeSpan.FromMilliseconds(50);
                    UpdateCurrentSubtitle();
                }
                else
                {
                    IsPlaying = false;
                    StatusMessage = "Finished";
                    _playbackTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }, null, 0, 50);
        }
    }
    
    private void UpdateCurrentSubtitle()
    {
        var matchingIndex = _subtitles
            .Select((segment, index) => new { segment, index })
            .FirstOrDefault(x => x.segment.Start <= CurrentTime && x.segment.End >= CurrentTime)?.index ?? -1;
        
        if (matchingIndex != _currentSubtitleIndex)
        {
            _currentSubtitleIndex = matchingIndex;
            CurrentSubtitle = matchingIndex >= 0 ? _subtitles[matchingIndex].Text : string.Empty;
        }
    }
    
    partial void OnCurrentTimeChanged(TimeSpan value)
    {
        CurrentTimeString = FormatTime(value);
        UpdateCurrentSubtitle();
    }
    
    partial void OnDurationChanged(TimeSpan value)
    {
        DurationString = FormatTime(value);
    }
    
    private static string FormatTime(TimeSpan time)
    {
        return $"{(int)time.TotalMinutes:D2}:{time.Seconds:D2}";
    }
    
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        
        if (e.PropertyName == nameof(IsPlaying))
        {
            UpdatePlaybackTimer();
        }
    }
    
    public void Dispose()
    {
        _playbackTimer?.Dispose();
        _subtitles.Clear();
    }
}