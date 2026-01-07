using FFMpegCore;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Diagnostics;
using SignSummarizer.UI.Models;

namespace SignSummarizer.UI.Services;

public interface IWhisperService
{
    Task<List<SubtitleSegment>> TranscribeAsync(
        string audioPath,
        string language = "en",
        CancellationToken cancellationToken = default);
    
    IAsyncEnumerable<DiarizedSegment> TranscribeStreamingAsync(
        string audioPath,
        string language = "en",
        WhisperChunkingMode chunkingMode = WhisperChunkingMode.ReadAhead,
        CancellationToken cancellationToken = default);
    
    Task<bool> InitializeAsync(CancellationToken cancellationToken = default);
    bool IsInitialized { get; }
}

public enum WhisperChunkingMode
{
    /// <summary>
    /// Process entire file at once (slowest, most accurate)
    /// </summary>
    FullFile,
    
    /// <summary>
    /// Read-ahead buffer with overlapping chunks for smooth streaming
    /// Best balance for real-time playback
    /// </summary>
    ReadAhead,
    
    /// <summary>
    /// Process chunks sequentially with minimal overlap
    /// Faster but may miss context at chunk boundaries
    /// </summary>
    MinimalOverlap,
    
    /// <summary>
    /// VAD (Voice Activity Detection) based chunking
    /// Only transcribe segments with speech detected
    /// Most efficient for long recordings with silence
    /// </summary>
    VADBased
}

public sealed class WhisperChunkingConfig
{
    /// <summary>
    /// Size of each chunk in seconds
    /// Default: 5s for Tiny, 10s for Base, 15s for larger models
    /// </summary>
    public TimeSpan ChunkSize { get; set; } = TimeSpan.FromSeconds(5);
    
    /// <summary>
    /// Overlap between chunks in seconds
    /// Helps maintain context across boundaries
    /// Default: 1s (20% of 5s chunk)
    /// </summary>
    public TimeSpan ChunkOverlap { get; set; } = TimeSpan.FromSeconds(1);
    
    /// <summary>
    /// Number of chunks to pre-load for read-ahead buffer
    /// Default: 3 chunks = ~15s lookahead
    /// </summary>
    public int ReadAheadCount { get; set; } = 3;
    
    /// <summary>
    /// Minimum silence duration for VAD chunking in seconds
    /// Default: 0.5s
    /// </summary>
    public TimeSpan VadSilenceThreshold { get; set; } = TimeSpan.FromMilliseconds(500);
    
    /// <summary>
    /// Energy threshold for VAD (0-1)
    /// Default: 0.3
    /// </summary>
    public float VadEnergyThreshold { get; set; } = 0.3f;
    
    public static WhisperChunkingConfig ForModel(WhisperModel model)
    {
        return model switch
        {
            WhisperModel.Tiny or WhisperModel.TinyEn => new WhisperChunkingConfig
            {
                ChunkSize = TimeSpan.FromSeconds(5),
                ChunkOverlap = TimeSpan.FromSeconds(1),
                ReadAheadCount = 3
            },
            WhisperModel.Base or WhisperModel.BaseEn => new WhisperChunkingConfig
            {
                ChunkSize = TimeSpan.FromSeconds(10),
                ChunkOverlap = TimeSpan.FromSeconds(2),
                ReadAheadCount = 2
            },
            WhisperModel.Small or WhisperModel.SmallEn => new WhisperChunkingConfig
            {
                ChunkSize = TimeSpan.FromSeconds(15),
                ChunkOverlap = TimeSpan.FromSeconds(3),
                ReadAheadCount = 2
            },
            _ => new WhisperChunkingConfig
            {
                ChunkSize = TimeSpan.FromSeconds(20),
                ChunkOverlap = TimeSpan.FromSeconds(4),
                ReadAheadCount = 1
            }
        };
    }
}

public enum WhisperModel
{
    Tiny,
    TinyEn,
    Base,
    BaseEn,
    Small,
    SmallEn,
    Medium,
    MediumEn,
    LargeV2,
    LargeV3
}

public sealed class WhisperService : IWhisperService
{
    private readonly ILogger<WhisperService> _logger;
    private readonly IModelDownloader _modelDownloader;
    private readonly string _modelsDirectory;
    private WhisperModel _model = WhisperModel.TinyEn;
    private bool _isInitialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    
    public bool IsInitialized => _isInitialized;
    
    public WhisperService(
        ILogger<WhisperService> logger,
        IModelDownloader modelDownloader)
    {
        _logger = logger;
        _modelDownloader = modelDownloader;
        _modelsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SignSummarizer",
            "WhisperModels");
        
        Directory.CreateDirectory(_modelsDirectory);
    }
    
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        
        try
        {
            if (_isInitialized)
                return true;
            
            _logger.LogInformation("Initializing Whisper service with model: {Model}", _model);
            
            var modelPath = await _modelDownloader.DownloadWhisperModelAsync(
                _model,
                _modelsDirectory,
                cancellationToken);
            
            _logger.LogInformation("Whisper model loaded: {ModelPath}", modelPath);
            
            _isInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Whisper service");
            _isInitialized = false;
            return false;
        }
        finally
        {
            _initLock.Release();
        }
    }
    
    public async Task<List<SubtitleSegment>> TranscribeAsync(
        string audioPath,
        string language = "en",
        CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            var success = await InitializeAsync(cancellationToken);
            if (!success)
                throw new InvalidOperationException("Whisper service failed to initialize");
        }
        
        _logger.LogInformation("Transcribing audio: {AudioPath} (language: {Language})", audioPath, language);
        
        return await TranscribeWithWhisperCppAsync(
            audioPath,
            language,
            cancellationToken);
    }
    
    public async IAsyncEnumerable<DiarizedSegment> TranscribeStreamingAsync(
        string audioPath,
        string language = "en",
        WhisperChunkingMode chunkingMode = WhisperChunkingMode.ReadAhead,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            var success = await InitializeAsync(cancellationToken);
            if (!success)
                throw new InvalidOperationException("Whisper service failed to initialize");
        }
        
        _logger.LogInformation("Streaming transcription: {AudioPath} (mode: {Mode})", audioPath, chunkingMode);
        
        var config = WhisperChunkingConfig.ForModel(_model);
        
        await foreach (var segment in chunkingMode switch
        {
            WhisperChunkingMode.FullFile => TranscribeFullFileAsync(audioPath, language, cancellationToken),
            WhisperChunkingMode.ReadAhead => TranscribeReadAheadAsync(audioPath, language, config, cancellationToken),
            WhisperChunkingMode.MinimalOverlap => TranscribeMinimalOverlapAsync(audioPath, language, config, cancellationToken),
            WhisperChunkingMode.VADBased => TranscribeVADBasedAsync(audioPath, language, config, cancellationToken),
            _ => TranscribeFullFileAsync(audioPath, language, cancellationToken)
        })
        {
            yield return segment;
        }
    }
    
    private async IAsyncEnumerable<DiarizedSegment> TranscribeFullFileAsync(
        string audioPath,
        string language,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var segments = await TranscribeWithWhisperCppAsync(audioPath, language, cancellationToken);
        
        var speakerSegments = AssignSpeakersDiarization(segments);
        
        foreach (var segment in speakerSegments)
        {
            yield return segment;
        }
    }
    
    private async IAsyncEnumerable<DiarizedSegment> TranscribeReadAheadAsync(
        string audioPath,
        string language,
        WhisperChunkingConfig config,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var audioDuration = GetAudioDuration(audioPath);
        var chunkCount = (int)Math.Ceiling(audioDuration.TotalSeconds / config.ChunkSize.TotalSeconds);
        
        var readAheadBuffer = Channel.CreateBounded<DiarizedSegment>(config.ReadAheadCount);
        var productionTask = Task.Run(() => ProduceChunksAsync(audioPath, language, config, readAheadBuffer, cancellationToken), cancellationToken);
        
        await foreach (var segment in readAheadBuffer.Reader.ReadAllAsync(cancellationToken))
        {
            yield return segment;
        }
        
        await productionTask;
    }
    
    private async Task ProduceChunksAsync(
        string audioPath,
        string language,
        WhisperChunkingConfig config,
        Channel<DiarizedSegment> output,
        CancellationToken cancellationToken)
    {
        var audioDuration = GetAudioDuration(audioPath);
        var chunkStart = TimeSpan.Zero;
        int chunkIndex = 0;
        
        while (chunkStart < audioDuration)
        {
            var chunkEnd = TimeSpan.FromTicks(Math.Min(
                chunkStart.Ticks + config.ChunkSize.Ticks,
                audioDuration.Ticks));
            
            var chunkPath = await ExtractChunkAsync(audioPath, chunkStart, chunkEnd, cancellationToken);
            
            var segments = await TranscribeChunkAsync(chunkPath, language, chunkStart, cancellationToken);
            
            foreach (var segment in segments)
            {
                await output.Writer.WriteAsync(segment, cancellationToken);
            }
            
            await CleanupChunkAsync(chunkPath);
            
            chunkStart = TimeSpan.FromTicks(chunkEnd.Ticks - config.ChunkOverlap.Ticks);
            chunkIndex++;
            
            _logger.LogDebug("Processed chunk {Index}/{Total} ({Start:F1}s - {End:F1}s)", 
                chunkIndex, 
                Math.Ceiling(audioDuration.TotalSeconds / config.ChunkSize.TotalSeconds),
                chunkStart.TotalSeconds,
                chunkEnd.TotalSeconds);
        }
        
        output.Writer.Complete();
    }
    
    private async IAsyncEnumerable<DiarizedSegment> TranscribeMinimalOverlapAsync(
        string audioPath,
        string language,
        WhisperChunkingConfig config,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var audioDuration = GetAudioDuration(audioPath);
        var chunkStart = TimeSpan.Zero;
        int chunkIndex = 0;
        
        while (chunkStart < audioDuration)
        {
            var chunkEnd = TimeSpan.FromTicks(Math.Min(
                chunkStart.Ticks + config.ChunkSize.Ticks,
                audioDuration.Ticks));
            
            var chunkPath = await ExtractChunkAsync(audioPath, chunkStart, chunkEnd, cancellationToken);
            
            var segments = await TranscribeChunkAsync(chunkPath, language, chunkStart, cancellationToken);
            
            foreach (var segment in segments)
            {
                yield return segment;
            }
            
            await CleanupChunkAsync(chunkPath);
            
            chunkStart = chunkEnd;
            chunkIndex++;
        }
    }
    
    private async IAsyncEnumerable<DiarizedSegment> TranscribeVADBasedAsync(
        string audioPath,
        string language,
        WhisperChunkingConfig config,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var speechRegions = DetectSpeechRegions(audioPath, config, cancellationToken);
        
        foreach (var (start, end) in speechRegions)
        {
            var chunkPath = await ExtractChunkAsync(audioPath, start, end, cancellationToken);
            
            var segments = await TranscribeChunkAsync(chunkPath, language, start, cancellationToken);
            
            foreach (var segment in segments)
            {
                yield return segment;
            }
            
            await CleanupChunkAsync(chunkPath);
        }
    }
    
    private async Task<List<DiarizedSegment>> TranscribeChunkAsync(
        string chunkPath,
        string language,
        TimeSpan offset,
        CancellationToken cancellationToken)
    {
        var segments = await TranscribeWithWhisperCppAsync(chunkPath, language, cancellationToken);
        var speakerSegments = AssignSpeakersDiarization(segments);
        
        var diarizedSegments = new List<DiarizedSegment>();
        foreach (var segment in speakerSegments)
        {
            diarizedSegments.Add(new DiarizedSegment(
                segment.Start + offset,
                segment.End + offset,
                segment.Text,
                segment.Speaker ?? "SPEAKER_0",
                segment.Confidence));
        }
        
        return diarizedSegments;
    }
    
    private async Task<string> ExtractChunkAsync(
        string audioPath,
        TimeSpan start,
        TimeSpan end,
        CancellationToken cancellationToken)
    {
        var chunkPath = Path.Combine(
            Path.GetTempPath(),
            $"whisper_chunk_{Guid.NewGuid()}.wav");
        
        await FFMpegCore.FFMpegArguments
            .FromFileInput(audioPath)
            .OutputToFile(chunkPath, true, options => options
                .Seek(start)
                .WithAudioBitrate(128)
                .WithAudioCodec("pcm_s16le"))
            .ProcessAsynchronously(true, null);
        
        return chunkPath;
    }
    
    private Task CleanupChunkAsync(string chunkPath)
    {
        return Task.Run(() =>
        {
            try
            {
                if (File.Exists(chunkPath))
                {
                    File.Delete(chunkPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup chunk: {ChunkPath}", chunkPath);
            }
        });
    }
    
    private TimeSpan GetAudioDuration(string audioPath)
    {
        var analysis = FFMpegCore.FFProbe.AnalyseAsync(audioPath).GetAwaiter().GetResult();
        
        return analysis.Duration;
    }
    
    private List<(TimeSpan Start, TimeSpan End)> DetectSpeechRegions(
        string audioPath,
        WhisperChunkingConfig config,
        CancellationToken cancellationToken)
    {
        var regions = new List<(TimeSpan, TimeSpan)>();
        var duration = GetAudioDuration(audioPath);
        
        var chunkSize = TimeSpan.FromSeconds(1);
        var isInSpeech = false;
        var currentStart = TimeSpan.Zero;
        var silenceStart = TimeSpan.Zero;
        
        for (var currentTime = TimeSpan.Zero; currentTime < duration; currentTime += chunkSize)
        {
            var energy = CalculateAudioEnergy(audioPath, currentTime, chunkSize);
            
            if (energy > config.VadEnergyThreshold)
            {
                if (!isInSpeech)
                {
                    currentStart = currentTime;
                    isInSpeech = true;
                }
                silenceStart = TimeSpan.Zero;
            }
            else
            {
                if (isInSpeech)
                {
                    silenceStart = currentTime;
                    
                    if (currentTime - currentStart >= config.VadSilenceThreshold)
                    {
                        regions.Add((currentStart, currentTime));
                        isInSpeech = false;
                    }
                }
            }
        }
        
        if (isInSpeech)
        {
            regions.Add((currentStart, duration));
        }
        
        return regions;
    }
    
    private float CalculateAudioEnergy(string audioPath, TimeSpan start, TimeSpan duration)
    {
        try
        {
            var chunkPath = Path.Combine(
                Path.GetTempPath(),
                $"vad_chunk_{Guid.NewGuid()}.wav");
            
            FFMpegCore.FFMpegArguments
                .FromFileInput(audioPath)
                .OutputToFile(chunkPath, true, options => options
                    .Seek(start)
                    .WithDuration(duration)
                    .WithAudioCodec("pcm_s16le"))
                .ProcessSynchronously();
            
            using var reader = new System.IO.BinaryReader(File.OpenRead(chunkPath));
            var samples = new List<float>();
            
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                var sample = reader.ReadInt16();
                samples.Add(Math.Abs(sample / 32768.0f));
            }
            
            File.Delete(chunkPath);
            
            return samples.Count > 0 ? samples.Average() : 0f;
        }
        catch
        {
            return 0f;
        }
    }
    
    private List<DiarizedSegment> AssignSpeakersDiarization(List<SubtitleSegment> segments)
    {
        if (segments.Count == 0)
            return new List<DiarizedSegment>();
        
        var diarizedSegments = new List<DiarizedSegment>();
        var currentSpeaker = "SPEAKER_0";
        var speakerCounts = new Dictionary<string, int>();
        
        foreach (var segment in segments)
        {
            var speaker = currentSpeaker;
            
            if (ShouldSwitchSpeaker(segment, segments, speakerCounts))
            {
                currentSpeaker = $"SPEAKER_{speakerCounts.Count}";
            }
            
            speakerCounts.TryGetValue(currentSpeaker, out var count);
            speakerCounts[currentSpeaker] = count + 1;
            
            diarizedSegments.Add(new DiarizedSegment(
                segment.Start,
                segment.End,
                segment.Text,
                speaker,
                segment.Confidence));
        }
        
        return diarizedSegments;
    }
    
    private bool ShouldSwitchSpeaker(
        SubtitleSegment segment,
        List<SubtitleSegment> allSegments,
        Dictionary<string, int> speakerCounts)
    {
        var segmentIndex = allSegments.IndexOf(segment);
        if (segmentIndex == 0)
            return false;
        
        var prevSegment = allSegments[segmentIndex - 1];
        var gap = segment.Start - prevSegment.End;
        
        var textSimilarity = CalculateTextSimilarity(segment.Text, prevSegment.Text);
        var durationRatio = segment.Duration.TotalSeconds / prevSegment.Duration.TotalSeconds;
        
        return gap > TimeSpan.FromSeconds(1.0) && textSimilarity < 0.5;
    }
    
    private float CalculateTextSimilarity(string text1, string text2)
    {
        var words1 = text1.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words2 = text2.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        if (words1.Length == 0 || words2.Length == 0)
            return 0f;
        
        var commonWords = words1.Intersect(words2).Count();
        var totalWords = words1.Concat(words2).Distinct().Count();
        
        return totalWords > 0 ? (float)commonWords / totalWords : 0f;
    }
    
    private async Task<List<SubtitleSegment>> TranscribeWithWhisperCppAsync(
        string audioPath,
        string language,
        CancellationToken cancellationToken)
    {
        var segments = new List<SubtitleSegment>();
        
        var modelPath = GetModelPath();
        if (!File.Exists(modelPath))
        {
            _logger.LogError("Whisper model not found: {ModelPath}", modelPath);
            return segments;
        }
        
        try
        {
            var arguments = BuildWhisperArgs(modelPath, audioPath, language);
            var startInfo = new ProcessStartInfo
            {
                FileName = GetWhisperExecutable(),
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = Process.Start(startInfo);
            
            if (process == null)
            {
                _logger.LogError("Failed to start Whisper process");
                return segments;
            }
            
            await Task.Run(() =>
            {
                while (!process.StandardOutput.EndOfStream)
                {
                    var line = process.StandardOutput.ReadLine();
                    if (line != null && !cancellationToken.IsCancellationRequested)
                    {
                        var segment = ParseWhisperLine(line);
                        if (segment != null)
                            segments.Add(segment);
                    }
                }
            }, cancellationToken);
            
            await process.WaitForExitAsync(cancellationToken);
            
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                _logger.LogError("Whisper process exited with code {ExitCode}: {Error}", process.ExitCode, error);
            }
            
            _logger.LogInformation("Transcription complete: {Count} segments", segments.Count);
            
            return segments;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Whisper transcription");
            return segments;
        }
    }
    
    private string GetModelPath()
    {
        return _model switch
        {
            WhisperModel.Tiny => Path.Combine(_modelsDirectory, "ggml-tiny.bin"),
            WhisperModel.TinyEn => Path.Combine(_modelsDirectory, "ggml-tiny.en.bin"),
            WhisperModel.Base => Path.Combine(_modelsDirectory, "ggml-base.bin"),
            WhisperModel.BaseEn => Path.Combine(_modelsDirectory, "ggml-base.en.bin"),
            WhisperModel.Small => Path.Combine(_modelsDirectory, "ggml-small.bin"),
            WhisperModel.SmallEn => Path.Combine(_modelsDirectory, "ggml-small.en.bin"),
            WhisperModel.Medium => Path.Combine(_modelsDirectory, "ggml-medium.bin"),
            WhisperModel.MediumEn => Path.Combine(_modelsDirectory, "ggml-medium.en.bin"),
            WhisperModel.LargeV2 => Path.Combine(_modelsDirectory, "ggml-large-v2.bin"),
            WhisperModel.LargeV3 => Path.Combine(_modelsDirectory, "ggml-large-v3.bin"),
            _ => Path.Combine(_modelsDirectory, "ggml-tiny.en.bin")
        };
    }
    
    private string GetWhisperExecutable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "whisper.exe";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "whisper";
        else
            return "whisper";
    }
    
    private string BuildWhisperArgs(string modelPath, string audioPath, string language)
    {
        return $"-m \"{modelPath}\" -f \"{audioPath}\" -l {language} --output-format srt --output-dir .";
    }
    
    private SubtitleSegment? ParseWhisperLine(string line)
    {
        try
        {
            var parts = line.Split(new[] { " --> " }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                return null;
            
            var timeRange = parts[0].Trim();
            var text = parts[1].Trim();
            
            var times = timeRange.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (times.Length != 2)
                return null;
            
            var start = TimeSpan.Parse(times[0].Trim());
            var end = TimeSpan.Parse(times[1].Trim());
            
            return new SubtitleSegment(start, end, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Whisper line: {Line}", line);
            return null;
        }
    }
}