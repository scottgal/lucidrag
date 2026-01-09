using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Mostlylucid.DocSummarizer.Images.Config;

namespace Mostlylucid.DocSummarizer.Images.Services;

/// <summary>
/// Factory for creating ONNX InferenceSessions with optimal execution providers.
/// Supports cross-platform GPU acceleration: DirectML (Windows), CUDA (Linux), CoreML (macOS).
/// </summary>
public class OnnxSessionFactory
{
    private readonly OnnxExecutionConfig _config;
    private readonly ILogger<OnnxSessionFactory>? _logger;
    private OnnxExecutionProvider? _detectedProvider;
    private readonly object _detectionLock = new();

    public OnnxSessionFactory(
        IOptions<ImageConfig> config,
        ILogger<OnnxSessionFactory>? logger = null)
    {
        _config = config.Value.OnnxExecution;
        _logger = logger;
    }

    /// <summary>
    /// Create SessionOptions with the best available execution provider.
    /// </summary>
    public SessionOptions CreateSessionOptions()
    {
        var options = new SessionOptions();

        // Enable graph optimization
        if (_config.EnableGraphOptimization)
        {
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        }

        // Set CPU thread count
        if (_config.CpuThreads > 0)
        {
            options.IntraOpNumThreads = _config.CpuThreads;
        }

        // Try to add GPU execution provider
        var provider = GetEffectiveProvider();
        TryAddExecutionProvider(options, provider);

        return options;
    }

    /// <summary>
    /// Create an InferenceSession with optimal settings.
    /// </summary>
    public InferenceSession CreateSession(string modelPath)
    {
        var options = CreateSessionOptions();
        return new InferenceSession(modelPath, options);
    }

    /// <summary>
    /// Get the execution provider that will be used.
    /// </summary>
    public OnnxExecutionProvider GetEffectiveProvider()
    {
        if (_config.PreferredProvider != OnnxExecutionProvider.Auto)
        {
            return _config.PreferredProvider;
        }

        lock (_detectionLock)
        {
            if (_detectedProvider.HasValue)
            {
                return _detectedProvider.Value;
            }

            _detectedProvider = DetectBestProvider();
            _logger?.LogInformation("Auto-detected ONNX execution provider: {Provider}", _detectedProvider);
            return _detectedProvider.Value;
        }
    }

    private OnnxExecutionProvider DetectBestProvider()
    {
        // Platform-specific detection - prefer CUDA for NVIDIA GPUs (faster than DirectML)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Try CUDA first on Windows (better for NVIDIA GPUs like A4000)
            if (TryDetectCuda())
            {
                _logger?.LogInformation("NVIDIA GPU detected, using CUDA provider (device {DeviceId})", _config.DeviceId);
                return OnnxExecutionProvider.CUDA;
            }

            // Fall back to DirectML (works with AMD, Intel, and NVIDIA)
            if (TryDetectDirectML())
            {
                _logger?.LogInformation("Using DirectML provider (device {DeviceId})", _config.DeviceId);
                return OnnxExecutionProvider.DirectML;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Try CUDA on Linux
            if (TryDetectCuda())
            {
                return OnnxExecutionProvider.CUDA;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // CoreML on macOS
            if (TryDetectCoreML())
            {
                return OnnxExecutionProvider.CoreML;
            }
        }

        _logger?.LogInformation("No GPU provider detected, using CPU");
        return OnnxExecutionProvider.CPU;
    }

    private void TryAddExecutionProvider(SessionOptions options, OnnxExecutionProvider provider)
    {
        try
        {
            switch (provider)
            {
                case OnnxExecutionProvider.DirectML:
                    TryAddDirectML(options);
                    break;

                case OnnxExecutionProvider.CUDA:
                    TryAddCuda(options);
                    break;

                case OnnxExecutionProvider.CoreML:
                    TryAddCoreML(options);
                    break;

                case OnnxExecutionProvider.CPU:
                default:
                    // CPU is always available as fallback
                    _logger?.LogDebug("Using CPU execution provider");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to add {Provider} execution provider, falling back to CPU", provider);
        }
    }

    private void TryAddDirectML(SessionOptions options)
    {
        try
        {
            // DirectML is available via Microsoft.ML.OnnxRuntime.DirectML package
            options.AppendExecutionProvider_DML(_config.DeviceId);
            _logger?.LogInformation("Added DirectML execution provider (GPU device {DeviceId})", _config.DeviceId);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "DirectML not available, using CPU");
        }
    }

    private void TryAddCuda(SessionOptions options)
    {
        try
        {
            // CUDA is available via Microsoft.ML.OnnxRuntime.Gpu package
            options.AppendExecutionProvider_CUDA(_config.DeviceId);
            _logger?.LogInformation("Added CUDA execution provider (GPU device {DeviceId})", _config.DeviceId);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "CUDA not available, using CPU");
        }
    }

    private void TryAddCoreML(SessionOptions options)
    {
        try
        {
            // CoreML for macOS
            options.AppendExecutionProvider_CoreML();
            _logger?.LogInformation("Added CoreML execution provider");
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "CoreML not available, using CPU");
        }
    }

    private bool TryDetectDirectML()
    {
        // Check if DirectML DLL is available
        try
        {
            // Quick probe - the package presence should indicate availability
            var assembly = typeof(SessionOptions).Assembly;
            // DirectML requires Windows 10 1903+ and DX12
            if (Environment.OSVersion.Version >= new Version(10, 0, 18362))
            {
                _logger?.LogDebug("DirectML should be available (Windows 10 1903+)");
                return true;
            }
        }
        catch
        {
            // DirectML not available
        }

        return false;
    }

    private bool TryDetectCuda()
    {
        // Check for NVIDIA CUDA availability
        try
        {
            // Look for CUDA libraries
            var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrEmpty(cudaPath))
            {
                _logger?.LogDebug("CUDA_PATH detected: {Path}", cudaPath);
                return true;
            }

            // Check for nvidia-smi (works on Windows and Linux)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows: Check common NVIDIA driver locations
                var nvidiaSmis = new[]
                {
                    @"C:\Windows\System32\nvidia-smi.exe",
                    @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
                };
                if (nvidiaSmis.Any(File.Exists))
                {
                    _logger?.LogDebug("NVIDIA drivers detected on Windows");
                    return true;
                }

                // Also check if cudart64 DLLs exist
                var systemPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (Directory.GetFiles(systemPath, "cudart64*.dll").Length > 0)
                {
                    _logger?.LogDebug("CUDA runtime DLLs detected");
                    return true;
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (File.Exists("/usr/bin/nvidia-smi"))
                {
                    _logger?.LogDebug("NVIDIA drivers detected on Linux");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error detecting CUDA");
        }

        return false;
    }

    private bool TryDetectCoreML()
    {
        // CoreML is available on macOS 10.15+
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
               Environment.OSVersion.Version >= new Version(10, 15);
    }

    /// <summary>
    /// Get a summary of the current ONNX configuration for diagnostics.
    /// </summary>
    public string GetConfigurationSummary()
    {
        var provider = GetEffectiveProvider();
        var lines = new List<string>
        {
            $"ONNX Execution Provider: {provider}",
            $"Preferred Provider: {_config.PreferredProvider}",
            $"Device ID: {_config.DeviceId}",
            $"Graph Optimization: {_config.EnableGraphOptimization}",
            $"CPU Threads: {(_config.CpuThreads == 0 ? "Auto" : _config.CpuThreads.ToString())}",
            $"Platform: {RuntimeInformation.OSDescription}"
        };

        return string.Join(Environment.NewLine, lines);
    }
}
