using Microsoft.Extensions.Logging;
using SignSummarizer.Models;
using SignSummarizer.Services;
using System.Collections.Concurrent;
using System.Linq;

namespace SignSummarizer.Pipelines;

public enum SignWaveProfile
{
    Fast = 0,
    Standard = 1,
    Detailed = 2,
    Research = 3
}

public interface ISignWaveCoordinator
{
    Task<SignAtom> ProcessSignAtomAsync(
        SignWaveContext context,
        SignWaveProfile profile = SignWaveProfile.Standard,
        CancellationToken cancellationToken = default);
    
    Task<List<SignAtom>> ProcessVideoAsync(
        string videoPath,
        SignWaveProfile profile = SignWaveProfile.Standard,
        CancellationToken cancellationToken = default);
    
    void RegisterWave(ISignWave wave);
    void UnregisterWave(string name);
    IEnumerable<ISignWave> GetRegisteredWaves();
}

public sealed class SignWaveCoordinator : ISignWaveCoordinator
{
    private readonly ILogger<SignWaveCoordinator> _logger;
    private readonly ConcurrentDictionary<string, ISignWave> _waves;
    private readonly ConcurrentDictionary<SignWaveProfile, List<string>> _profiles;
    
    public SignWaveCoordinator(ILogger<SignWaveCoordinator> logger)
    {
        _logger = logger;
        _waves = new ConcurrentDictionary<string, ISignWave>();
        _profiles = new ConcurrentDictionary<SignWaveProfile, List<string>>();
        
        InitializeProfiles();
    }
    
    public void RegisterWave(ISignWave wave)
    {
        _waves.TryAdd(wave.Name, wave);
        _logger.LogInformation("Registered wave: {Wave}", wave.Name);
    }
    
    public void UnregisterWave(string name)
    {
        if (_waves.TryRemove(name, out var wave))
        {
            _logger.LogInformation("Unregistered wave: {Wave}", name);
        }
    }
    
    public IEnumerable<ISignWave> GetRegisteredWaves()
    {
        return _waves.Values.OrderBy(w => w.Priority);
    }
    
    public async Task<SignAtom> ProcessSignAtomAsync(
        SignWaveContext context,
        SignWaveProfile profile = SignWaveProfile.Standard,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Processing sign atom {AtomId} with profile {Profile}",
            context.SignAtomId,
            profile);
        
        var waveNames = GetWavesForProfile(profile);
        var baseAtom = context.SignAtom ?? new SignAtom(
            AtomType.Hold,
            TimeSpan.Zero,
            TimeSpan.Zero);
        var atom = baseAtom with { Id = context.SignAtomId };
        
        foreach (var waveName in waveNames)
        {
            if (!_waves.TryGetValue(waveName, out var wave))
            {
                _logger.LogWarning("Wave not found: {Wave}", waveName);
                continue;
            }
            
            if (!wave.Enabled)
            {
                _logger.LogDebug("Wave disabled: {Wave}", waveName);
                continue;
            }
            
            _logger.LogDebug("Executing wave: {Wave} (priority: {Priority})",
                wave.Name, wave.Priority);
            
            try
            {
                var result = await wave.ExecuteAsync(context, cancellationToken);
                
                if (!result.IsSuccess)
                {
                    _logger.LogError(
                        "Wave failed: {Wave} - Error: {Error}",
                        wave.Name, result.Error);
                    continue;
                }
                
                UpdateAtomFromResult(atom, result, context);
                UpdateContextFromResult(context, result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Wave error: {Wave}", wave.Name);
            }
        }
        
        _logger.LogInformation(
            "Completed processing atom {AtomId} - Type: {Type}, Frames: {FrameCount}, Keyframes: {KeyframeCount}",
            atom.Id, atom.Type, atom.Frames.Count, atom.KeyFrameIndices.Count);
        
        return atom;
    }
    
    public async Task<List<SignAtom>> ProcessVideoAsync(
        string videoPath,
        SignWaveProfile profile = SignWaveProfile.Standard,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing video with profile {Profile}: {Path}", profile, videoPath);
        
        var atoms = await ProcessVideoInternalAsync(videoPath, profile, cancellationToken);
        
        _logger.LogInformation("Video processing complete: {Count} atoms", atoms.Count);
        
        return atoms;
    }
    
    private async Task<List<SignAtom>> ProcessVideoInternalAsync(
        string videoPath,
        SignWaveProfile profile,
        CancellationToken cancellationToken)
    {
        var atoms = new List<SignAtom>();
        
        await foreach (var atom in ProcessFramesAsync(videoPath, cancellationToken))
        {
            var context = CreateContext(atom, videoPath);
            var processedAtom = await ProcessSignAtomAsync(context, profile, cancellationToken);
            atoms.Add(processedAtom);
        }
        
        return atoms;
    }
    
    private async IAsyncEnumerable<SignAtom> ProcessFramesAsync(
        string videoPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var captureService = new VideoCaptureService(
            videoPath,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VideoCaptureService>.Instance);
        
        var handDetectionService = _waves.Values
            .OfType<HandPoseWave>()
            .FirstOrDefault()
            ?.GetHandDetectionService();
        
        if (handDetectionService == null)
        {
            yield break;
        }
        
        await foreach (var frameInfo in captureService.CaptureFramesAsync(
            targetFps: 15,
            cancellationToken: cancellationToken))
        {
            var (leftHand, rightHand) = await handDetectionService
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
            
            var atom = new SignAtom(
                AtomType.Hold,
                frameInfo.Timestamp,
                frameInfo.Timestamp.Add(TimeSpan.FromMilliseconds(50)))
            {
                Frames = new List<FrameLandmarks> { frameLandmarks }
            };
            
            yield return atom;
        }
        
        captureService.Dispose();
    }
    
    private void InitializeProfiles()
    {
        _profiles.TryAdd(SignWaveProfile.Fast, new List<string>
        {
            "hand_pose",
            "segmentation"
        });
        
        _profiles.TryAdd(SignWaveProfile.Standard, new List<string>
        {
            "hand_pose",
            "canonicalization",
            "segmentation",
            "filmstrip",
            "pose_embedding",
            "modifier_detection"
        });
        
        _profiles.TryAdd(SignWaveProfile.Detailed, new List<string>
        {
            "hand_pose",
            "canonicalization",
            "segmentation",
            "filmstrip",
            "pose_embedding",
            "modifier_detection",
            "sign_classification"
        });
        
        _profiles.TryAdd(SignWaveProfile.Research, new List<string>
        {
            "hand_pose",
            "canonicalization",
            "segmentation",
            "filmstrip",
            "pose_embedding",
            "modifier_detection",
            "sign_classification",
            "vision_llm",
            "rag_retrieval"
        });
    }
    
    private List<string> GetWavesForProfile(SignWaveProfile profile)
    {
        return _profiles.TryGetValue(profile, out var waves) ? waves : new List<string>();
    }
    
    private void UpdateAtomFromResult(SignAtom atom, SignWaveResult result, SignWaveContext context)
    {
        if (result.Data == null)
            return;
        
        if (result.GetData<PoseFilmstrip>("filmstrip") is var filmstrip && filmstrip != null)
        {
            foreach (var keyPose in filmstrip.KeyPoses)
            {
                atom.AddKeyFrame(keyPose.FrameIndex);
            }
        }
        
        if (result.GetData<float[]>("embedding") is var embedding && embedding != null)
        {
            atom.PoseEmbedding = embedding;
        }
        
        if (result.GetData<List<string>>("gloss_candidates") is var candidates && candidates != null)
        {
            atom.GlossCandidates = candidates;
        }
        
        if (result.GetData<NonManualModifiers>("modifiers") is var modifiers && modifiers != null)
        {
            atom.AddModifiers(modifiers);
        }
        
        if (result.GetScalar<float>("confidence") is var confidence && confidence != 0) // Value type check? confidence is float?
        {
             // GetScalar returns T? (Nullable<T>) if T is struct.
             // If I use GetScalar<float>, it returns float?
             // confidence is float?
             if (confidence.HasValue)
                atom.Confidence = confidence.Value;
        }
    }
    
    private void UpdateContextFromResult(SignWaveContext context, SignWaveResult result)
    {
        if (result.Data == null)
            return;
        
        if (result.GetData<PoseFilmstrip>("filmstrip") is var filmstrip && filmstrip != null)
        {
            context.SetCached("filmstrip", filmstrip);
        }
        
        if (result.GetData<float[]>("embedding") is var embedding && embedding != null)
        {
            context.SetCached("embedding", embedding);
        }
        
        if (result.GetData<CanonicalLandmarks[]>("canonical_landmarks") is var landmarks && landmarks != null)
        {
            context.SetCached("canonical_landmarks", landmarks);
        }
    }
    
    private SignWaveContext CreateContext(SignAtom atom, string videoPath)
    {
        return new SignWaveContext
        {
            SignAtomId = atom.Id,
            VideoPath = videoPath,
            SignAtom = atom,
            FrameLandmarks = atom.Frames,
            GlossCandidates = atom.GlossCandidates
        };
    }
}
