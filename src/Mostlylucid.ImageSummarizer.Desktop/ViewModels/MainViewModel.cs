using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.ImageSummarizer.Desktop.ViewModels;

/// <summary>
/// Entry in the live signal log
/// </summary>
public class SignalLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Message { get; set; } = "";
    public double? Confidence { get; set; }
    public bool HasConfidence => Confidence.HasValue;
    public string ConfidenceColor => Confidence switch
    {
        >= 0.8 => "#22C55E", // Green
        >= 0.5 => "#FBBF24", // Yellow
        _ => "#EF4444"       // Red
    };
}

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private WaveOrchestrator? _orchestrator;

    [ObservableProperty]
    private string? _imagePath;

    [ObservableProperty]
    private Bitmap? _imagePreview;

    [ObservableProperty]
    private string? _gifPath;

    [ObservableProperty]
    private bool _isAnimatedGif;

    [ObservableProperty]
    private bool _isStaticImage;

    [ObservableProperty]
    private Bitmap? _filmstripPreview;

    [ObservableProperty]
    private string _selectedPipeline = "auto";  // Smart routing: auto-selects fast/balanced/quality

    [ObservableProperty]
    private string _selectedOutput = "auto";  // Adaptive detailed description

    // Route selected by auto-routing (fast/balanced/quality)
    [ObservableProperty]
    private string? _selectedRoute;

    [ObservableProperty]
    private string? _routeReason;

    // Preferred route for auto mode (user can hint their preference)
    [ObservableProperty]
    private string _preferredRoute = "balanced";

    // Quality tiers for auto mode
    public ObservableCollection<string> QualityTiers { get; } = new()
    {
        "fast",      // Florence2 only, minimal processing
        "balanced",  // Florence2 + OCR + Motion, escalate to LLM if needed
        "quality"    // Full pipeline with VisionLLM
    };

    // Show quality tier dropdown when auto pipeline is selected
    public bool ShowQualityTier => SelectedPipeline == "auto";

    partial void OnSelectedPipelineChanged(string value)
    {
        OnPropertyChanged(nameof(ShowQualityTier));
    }

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private string? _statusText = "Drop an image or click Browse";

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private bool _enableVisionLlm = true;

    [ObservableProperty]
    private bool _enableOcr = true;

    [ObservableProperty]
    private string _ollamaUrl = "http://localhost:11434";

    [ObservableProperty]
    private string _visionModel = "minicpm-v:8b";

    // Available vision models from Ollama
    public ObservableCollection<string> AvailableModels { get; } = new() { "minicpm-v:8b", "llava:7b" };

    // Service availability status (traffic lights)
    [ObservableProperty]
    private bool _ocrAvailable = true; // Tesseract is bundled

    [ObservableProperty]
    private bool _ollamaAvailable;

    [ObservableProperty]
    private bool _openCvAvailable = true; // OpenCVSharp is bundled

    [ObservableProperty]
    private bool _florence2Available = true; // Florence-2 ONNX is bundled

    [ObservableProperty]
    private string _ocrStatus = "Tesseract (local)";

    [ObservableProperty]
    private string _ollamaStatus = "Checking...";

    [ObservableProperty]
    private string _openCvStatus = "OpenCV (local)";

    [ObservableProperty]
    private string _florence2Status = "Florence-2 (local)";

    [ObservableProperty]
    private string _fallbackMode = "Full analysis";

    // Signal log for live updates
    public ObservableCollection<SignalLogEntry> SignalLog { get; } = new();

    [ObservableProperty]
    private int _signalLogCount;

    // Color properties for status indicators (green=#22C55E, red=#EF4444, yellow=#FBBF24)
    public string OcrStatusColor => OcrAvailable ? "#22C55E" : "#EF4444";
    public string OpenCvStatusColor => OpenCvAvailable ? "#22C55E" : "#EF4444";
    public string OllamaStatusColor => OllamaAvailable ? "#22C55E" : "#EF4444";
    public string Florence2StatusColor => Florence2Available ? "#22C55E" : "#EF4444";

    partial void OnOcrAvailableChanged(bool value) => OnPropertyChanged(nameof(OcrStatusColor));
    partial void OnOpenCvAvailableChanged(bool value) => OnPropertyChanged(nameof(OpenCvStatusColor));
    partial void OnOllamaAvailableChanged(bool value) => OnPropertyChanged(nameof(OllamaStatusColor));
    partial void OnFlorence2AvailableChanged(bool value) => OnPropertyChanged(nameof(Florence2StatusColor));

    // === ML Model Status Indicators ===
    // EAST Text Detection Model
    [ObservableProperty]
    private bool _eastModelAvailable;
    [ObservableProperty]
    private bool _eastModelInUse;
    public string EastModelStatus => EastModelAvailable ? "EAST (downloaded)" : "EAST (not downloaded)";
    public string EastModelColor => EastModelInUse ? "#FBBF24" : (EastModelAvailable ? "#22C55E" : "#EF4444");
    partial void OnEastModelAvailableChanged(bool value) { OnPropertyChanged(nameof(EastModelColor)); OnPropertyChanged(nameof(EastModelStatus)); }
    partial void OnEastModelInUseChanged(bool value) => OnPropertyChanged(nameof(EastModelColor));

    // CRAFT Text Detection Model
    [ObservableProperty]
    private bool _craftModelAvailable;
    [ObservableProperty]
    private bool _craftModelInUse;
    public string CraftModelStatus => CraftModelAvailable ? "CRAFT (downloaded)" : "CRAFT (not downloaded)";
    public string CraftModelColor => CraftModelInUse ? "#FBBF24" : (CraftModelAvailable ? "#22C55E" : "#EF4444");
    partial void OnCraftModelAvailableChanged(bool value) { OnPropertyChanged(nameof(CraftModelColor)); OnPropertyChanged(nameof(CraftModelStatus)); }
    partial void OnCraftModelInUseChanged(bool value) => OnPropertyChanged(nameof(CraftModelColor));

    // CLIP Visual Embedding Model
    [ObservableProperty]
    private bool _clipModelAvailable;
    [ObservableProperty]
    private bool _clipModelInUse;
    public string ClipModelStatus => ClipModelAvailable ? "CLIP (downloaded)" : "CLIP (not downloaded)";
    public string ClipModelColor => ClipModelInUse ? "#FBBF24" : (ClipModelAvailable ? "#22C55E" : "#EF4444");
    partial void OnClipModelAvailableChanged(bool value) { OnPropertyChanged(nameof(ClipModelColor)); OnPropertyChanged(nameof(ClipModelStatus)); }
    partial void OnClipModelInUseChanged(bool value) => OnPropertyChanged(nameof(ClipModelColor));

    // Real-ESRGAN Super Resolution Model
    [ObservableProperty]
    private bool _esrganModelAvailable;
    [ObservableProperty]
    private bool _esrganModelInUse;
    public string EsrganModelStatus => EsrganModelAvailable ? "ESRGAN (downloaded)" : "ESRGAN (not downloaded)";
    public string EsrganModelColor => EsrganModelInUse ? "#FBBF24" : (EsrganModelAvailable ? "#22C55E" : "#EF4444");
    partial void OnEsrganModelAvailableChanged(bool value) { OnPropertyChanged(nameof(EsrganModelColor)); OnPropertyChanged(nameof(EsrganModelStatus)); }
    partial void OnEsrganModelInUseChanged(bool value) => OnPropertyChanged(nameof(EsrganModelColor));

    // Florence-2 Captioning Model (InUse tracking)
    [ObservableProperty]
    private bool _florence2InUse;
    public string Florence2ModelColor => Florence2InUse ? "#FBBF24" : (Florence2Available ? "#22C55E" : "#EF4444");
    partial void OnFlorence2InUseChanged(bool value) => OnPropertyChanged(nameof(Florence2ModelColor));

    // Tesseract OCR (InUse tracking)
    [ObservableProperty]
    private bool _tesseractInUse;
    public string TesseractModelColor => TesseractInUse ? "#FBBF24" : (OcrAvailable ? "#22C55E" : "#EF4444");
    partial void OnTesseractInUseChanged(bool value) => OnPropertyChanged(nameof(TesseractModelColor));

    // Pipelines: auto is default and recommended
    public ObservableCollection<string> Pipelines { get; } = new()
    {
        "auto",           // Smart routing - auto-selects fast/balanced/quality (recommended)
        "caption",        // Full caption pipeline
        "vision",         // Vision LLM only (no OCR)
        "florence2",      // Fast local ONNX captioning
        "motion",         // Motion analysis for GIFs
        "advancedocr",    // Full OCR pipeline
        "quality",        // Full quality pipeline
    };

    public ObservableCollection<string> OutputFormats { get; } = new()
    {
        "auto",           // Adaptive detailed description (recommended)
        "caption",        // Caption only
        "alttext",        // Alt text format
        "text",           // Route + caption + OCR
        "json",           // Full JSON
        "markdown",       // Markdown format
        "signals"         // Raw signals
    };

    public MainViewModel()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Warning));
        services.AddDocSummarizerImages(opt =>
        {
            opt.EnableOcr = true;
            opt.EnableVisionLlm = true;
            opt.VisionLlmModel = "minicpm-v:8b";
            opt.OllamaBaseUrl = "http://localhost:11434";
        });
        _serviceProvider = services.BuildServiceProvider();

        // Check service availability on startup
        _ = CheckServicesAsync();
        _ = CheckModelAvailabilityAsync();
    }

    /// <summary>
    /// Check Ollama availability and discover vision models.
    /// </summary>
    private async Task CheckServicesAsync()
    {
        // Check Ollama
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync($"{OllamaUrl}/api/tags");

            if (response.IsSuccessStatusCode)
            {
                OllamaAvailable = true;
                OllamaStatus = "Connected";

                // Parse model list
                var json = await response.Content.ReadAsStringAsync();
                var doc = System.Text.Json.JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("models", out var models))
                {
                    var visionModels = new System.Collections.Generic.List<string>();

                    foreach (var model in models.EnumerateArray())
                    {
                        if (model.TryGetProperty("name", out var nameEl))
                        {
                            var name = nameEl.GetString() ?? "";
                            // Filter for vision/multimodal models
                            var nameLower = name.ToLowerInvariant();
                            if (nameLower.Contains("llava") ||
                                nameLower.Contains("minicpm") ||
                                nameLower.Contains("bakllava") ||
                                nameLower.Contains("moondream") ||
                                nameLower.Contains("llama3.2-vision") ||
                                nameLower.Contains("cogvlm") ||
                                nameLower.Contains("fuyu") ||
                                nameLower.Contains("obsidian") ||
                                // Generic check for known vision model patterns
                                (model.TryGetProperty("details", out var details) &&
                                 details.TryGetProperty("families", out var families) &&
                                 families.GetRawText().Contains("vision")))
                            {
                                visionModels.Add(name);
                            }
                        }
                    }

                    // Update available models
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        AvailableModels.Clear();
                        if (visionModels.Count > 0)
                        {
                            foreach (var m in visionModels.OrderBy(x => x))
                                AvailableModels.Add(m);

                            // Select best available model if current not in list
                            if (!visionModels.Contains(VisionModel) && visionModels.Count > 0)
                            {
                                // Prefer minicpm-v:8b > minicpm-v > llava > first available
                                var preferred = visionModels.FirstOrDefault(m => m == "minicpm-v:8b")
                                    ?? visionModels.FirstOrDefault(m => m.StartsWith("minicpm-v"))
                                    ?? visionModels.FirstOrDefault(m => m.StartsWith("llava"))
                                    ?? visionModels[0];
                                VisionModel = preferred;
                            }

                            OllamaStatus = $"{visionModels.Count} vision model(s)";
                        }
                        else
                        {
                            AvailableModels.Add("(no vision models)");
                            OllamaStatus = "No vision models";
                            EnableVisionLlm = false;
                        }

                        UpdateFallbackMode();
                    });
                }
            }
            else
            {
                OllamaAvailable = false;
                OllamaStatus = "Not responding";
                EnableVisionLlm = false;
                UpdateFallbackMode();
            }
        }
        catch
        {
            OllamaAvailable = false;
            OllamaStatus = "Offline";
            EnableVisionLlm = false;
            UpdateFallbackMode();
        }
    }

    /// <summary>
    /// Check ML model availability on disk.
    /// </summary>
    private async Task CheckModelAvailabilityAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                // Get models directory (same as used by DocSummarizer)
                var modelsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LucidRAG", "models");

                var downloader = new Mostlylucid.DocSummarizer.Images.Services.Ocr.Models.ModelDownloader(
                    modelsDir, autoDownload: false);

                var status = downloader.GetModelStatus();

                // Check each model individually
                var eastAvail = status.TryGetValue(
                    Mostlylucid.DocSummarizer.Images.Services.Ocr.Models.ModelType.EAST, out var east) && east.Available;
                var craftAvail = status.TryGetValue(
                    Mostlylucid.DocSummarizer.Images.Services.Ocr.Models.ModelType.CRAFT, out var craft) && craft.Available;
                var clipAvail = status.TryGetValue(
                    Mostlylucid.DocSummarizer.Images.Services.Ocr.Models.ModelType.ClipVisual, out var clip) && clip.Available;
                var esrganAvail = status.TryGetValue(
                    Mostlylucid.DocSummarizer.Images.Services.Ocr.Models.ModelType.RealESRGAN, out var esrgan) && esrgan.Available;
                var tesseractAvail = status.TryGetValue(
                    Mostlylucid.DocSummarizer.Images.Services.Ocr.Models.ModelType.TesseractEng, out var tess) && tess.Available;

                // Check for Florence-2 models (separate location)
                var florence2Dir = Path.Combine(modelsDir, "florence2");
                var florence2Avail = Directory.Exists(florence2Dir) &&
                    Directory.GetFiles(florence2Dir, "*.onnx").Length > 0;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Update availability flags
                    EastModelAvailable = eastAvail;
                    CraftModelAvailable = craftAvail;
                    ClipModelAvailable = clipAvail;
                    EsrganModelAvailable = esrganAvail;
                    Florence2Available = florence2Avail;
                    OcrAvailable = tesseractAvail;

                    // Log model status for debugging
                    System.Diagnostics.Debug.WriteLine($"Model status: EAST={eastAvail}, CRAFT={craftAvail}, CLIP={clipAvail}, ESRGAN={esrganAvail}, Florence2={florence2Avail}, Tesseract={tesseractAvail}");
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking models: {ex.Message}");
                // Models not available - leave defaults (false)
            }
        });
    }

    /// <summary>
    /// Update the fallback mode description based on available services.
    /// </summary>
    private void UpdateFallbackMode()
    {
        if (OllamaAvailable && OcrAvailable && OpenCvAvailable && Florence2Available)
        {
            FallbackMode = "Full analysis (Vision LLM + Florence-2 + OCR + OpenCV)";
        }
        else if (Florence2Available && OcrAvailable && OpenCvAvailable)
        {
            FallbackMode = "Local mode (Florence-2 + OCR + OpenCV, no cloud LLM)";
        }
        else if (OcrAvailable && OpenCvAvailable)
        {
            FallbackMode = "Heuristic mode (OCR + OpenCV, no LLM)";
        }
        else if (OcrAvailable)
        {
            FallbackMode = "OCR only mode";
        }
        else
        {
            FallbackMode = "Minimal mode (basic signals only)";
        }
    }

    [RelayCommand]
    private async Task BrowseAsync(IStorageProvider? storageProvider)
    {
        if (storageProvider == null) return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.gif", "*.webp", "*.bmp" }
                }
            }
        });

        if (files.Count > 0)
        {
            await LoadImageAsync(files[0].Path.LocalPath);
        }
    }

    public async Task LoadImageAsync(string path)
    {
        if (!File.Exists(path)) return;

        ImagePath = path;
        StatusText = $"Loaded: {Path.GetFileName(path)}";

        // Reset animation state
        IsAnimatedGif = false;
        IsStaticImage = true;
        GifPath = null;
        FilmstripPreview = null;

        // Check if it's a GIF file (might be animated)
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var isGif = extension == ".gif";

        try
        {
            await using var stream = File.OpenRead(path);
            ImagePreview = new Bitmap(stream);
        }
        catch
        {
            // For GIFs, load first frame
            ImagePreview = new Bitmap(path);
        }

        // If it's a GIF, set the path for potential animated playback
        if (isGif)
        {
            GifPath = path;
        }

        // Auto-analyze on load
        await AnalyzeAsync();
    }

    /// <summary>
    /// Add a log entry to the signal log on the UI thread
    /// </summary>
    private void AddLogEntry(string message, double? confidence = null)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SignalLog.Add(new SignalLogEntry
            {
                Timestamp = DateTime.Now,
                Message = message,
                Confidence = confidence
            });
            SignalLogCount = SignalLog.Count;
        });
    }

    /// <summary>
    /// Reset all model InUse states to false.
    /// </summary>
    private void ResetModelInUseStates()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            EastModelInUse = false;
            CraftModelInUse = false;
            ClipModelInUse = false;
            EsrganModelInUse = false;
            Florence2InUse = false;
            TesseractInUse = false;
        });
    }

    /// <summary>
    /// Flash the appropriate model light based on signal key.
    /// Creates a visual flicker effect to show model activity.
    /// </summary>
    private async Task FlashModelLightForSignalAsync(string signalKey)
    {
        var keyLower = signalKey.ToLowerInvariant();

        // Determine which model to flash based on signal namespace
        Action<bool>? setInUse = null;
        if (keyLower.StartsWith("east.") || keyLower.Contains("east"))
            setInUse = v => EastModelInUse = v;
        else if (keyLower.StartsWith("craft.") || keyLower.Contains("craft"))
            setInUse = v => CraftModelInUse = v;
        else if (keyLower.StartsWith("clip.") || keyLower.Contains("clip") || keyLower.StartsWith("embedding."))
            setInUse = v => ClipModelInUse = v;
        else if (keyLower.StartsWith("esrgan.") || keyLower.Contains("esrgan") || keyLower.Contains("upscale"))
            setInUse = v => EsrganModelInUse = v;
        else if (keyLower.StartsWith("florence2.") || keyLower.Contains("florence"))
            setInUse = v => Florence2InUse = v;
        else if (keyLower.StartsWith("ocr.") || keyLower.StartsWith("tesseract.") || keyLower.Contains("text_detection"))
            setInUse = v => TesseractInUse = v;

        if (setInUse == null)
            return;

        // Flash: on -> delay -> off
        Avalonia.Threading.Dispatcher.UIThread.Post(() => setInUse(true));
        await Task.Delay(50); // Brief flash
        Avalonia.Threading.Dispatcher.UIThread.Post(() => setInUse(false));
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath))
        {
            StatusText = "No image loaded";
            return;
        }

        IsProcessing = true;
        StatusText = "Starting analysis...";
        ResultText = "";

        // Clear signal log
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SignalLog.Clear();
            SignalLogCount = 0;
        });
        AddLogEntry("▶ Starting analysis...");

        try
        {
            // Build services with current settings (needed for both fast and full mode)
            StatusText = "Initializing services...";
            AddLogEntry("⚙ Initializing services...");
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Warning));
            services.AddDocSummarizerImages(opt =>
            {
                // Use EnableOcr checkbox + pipeline constraints
                opt.EnableOcr = EnableOcr && SelectedPipeline != "stats" && SelectedPipeline != "vision";
                opt.EnableVisionLlm = EnableVisionLlm;
                opt.VisionLlmModel = VisionModel;
                opt.OllamaBaseUrl = OllamaUrl;
                opt.Ocr.PipelineName = SelectedPipeline;
            });

            var provider = services.BuildServiceProvider();

            _orchestrator = provider.GetRequiredService<WaveOrchestrator>();

            StatusText = "Running wave analysis...";
            AddLogEntry($"🌊 Running {SelectedPipeline} pipeline...");
            var profile = await _orchestrator.AnalyzeAsync(ImagePath);

            // Extract route info if using auto pipeline
            SelectedRoute = profile.GetValue<string>("route.selected");
            RouteReason = profile.GetValue<string>("route.reason");
            if (!string.IsNullOrEmpty(SelectedRoute))
            {
                AddLogEntry($"🎯 Route: {SelectedRoute.ToUpperInvariant()} ({RouteReason})", 1.0);

                // Update status bar to show actual route used (instead of static "Full analysis")
                var routeUpper = SelectedRoute.ToUpperInvariant();
                var wavesUsed = routeUpper switch
                {
                    "FAST" => "Florence-2 + filmstrip",
                    "BALANCED" => "Florence-2 + OCR + filmstrip",
                    "QUALITY" => "Vision LLM + Florence-2 + OCR",
                    _ => "auto-selected"
                };
                FallbackMode = $"{routeUpper} route ({wavesUsed})";
            }

            // Log all signals from the analysis and flash model lights
            ResetModelInUseStates();
            foreach (var signal in profile.GetAllSignals().OrderBy(s => s.Key))
            {
                var valueStr = FormatSignalValueCompact(signal.Value);
                AddLogEntry($"📊 {signal.Key}: {valueStr}", signal.Confidence);

                // Flash model lights based on signal source
                await FlashModelLightForSignalAsync(signal.Key);
            }
            ResetModelInUseStates(); // Reset after processing

            // Get escalation service for LLM caption
            // Call Vision LLM if: explicitly requested pipeline OR Florence2 says to escalate (e.g., for animated GIFs)
            string? llmCaption = null;
            var shouldEscalate = profile.GetValue<bool>("florence2.should_escalate");
            var explicitLlmPipeline = SelectedPipeline is "caption" or "alttext" or "socialmediaalt" or "vision";

            if (EnableVisionLlm && (explicitLlmPipeline || shouldEscalate))
            {
                var reason = shouldEscalate ? "escalation triggered" : "pipeline requested";
                StatusText = $"Calling Vision LLM ({VisionModel})...";
                AddLogEntry($"🤖 Calling Vision LLM ({VisionModel}) - {reason}...");
                var escalationService = provider.GetService<Mostlylucid.DocSummarizer.Images.Services.EscalationService>();
                if (escalationService != null)
                {
                    var result = await escalationService.AnalyzeWithEscalationAsync(
                        ImagePath,
                        forceEscalate: true,
                        enableOcr: SelectedPipeline != "vision");
                    llmCaption = result.LlmCaption;
                    if (!string.IsNullOrWhiteSpace(llmCaption))
                    {
                        var truncated = llmCaption.Length > 60 ? llmCaption[..57] + "..." : llmCaption;
                        AddLogEntry($"🎯 LLM: {truncated}", 0.85);
                    }
                }
            }

            // Generate filmstrip and enable animation for animated images
            var isAnimated = profile.GetValue<bool>("identity.is_animated");
            var frameCount = profile.GetValue<int>("identity.frame_count");
            if (isAnimated && frameCount > 1)
            {
                // Switch to animated display
                IsAnimatedGif = true;
                IsStaticImage = false;
                FilmstripPreview = await GenerateFilmstripAsync(ImagePath, frameCount);
                AddLogEntry($"🎞️ Filmstrip: {frameCount} frames (animated playback enabled)");
            }
            else
            {
                // Keep static display
                IsAnimatedGif = false;
                IsStaticImage = true;
                FilmstripPreview = null;
            }

            // Format output based on selected format
            StatusText = "Formatting output...";
            ResultText = FormatOutput(profile, llmCaption);
            AddLogEntry($"✓ Done in {profile.AnalysisDurationMs}ms");
            StatusText = $"Done in {profile.AnalysisDurationMs}ms - {profile.GetAllSignals().Count()} signals";
        }
        catch (Exception ex)
        {
            ResultText = $"Error: {ex.Message}";
            StatusText = "Analysis failed";
            AddLogEntry($"❌ Error: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private string FormatOutput(Mostlylucid.DocSummarizer.Images.Models.Dynamic.DynamicImageProfile profile, string? llmCaption)
    {
        var ledger = profile.GetLedger();

        // Extract clean caption from LLM response (handles JSON or verbose text)
        var cleanCaption = ExtractCleanCaption(llmCaption);

        return SelectedOutput switch
        {
            "auto" => GenerateAdaptiveDescription(profile, ledger, cleanCaption),
            "alttext" => GenerateAltText(profile, ledger, cleanCaption),
            "caption" => cleanCaption ?? profile.GetValue<string>("florence2.caption") ?? ledger.ToLlmSummary(),
            "text" => FormatTextOutput(profile, cleanCaption),
            "json" => System.Text.Json.JsonSerializer.Serialize(new
            {
                image = profile.ImagePath,
                duration_ms = profile.AnalysisDurationMs,
                caption = llmCaption,
                text = GetExtractedText(profile),
                identity = ledger.Identity,
                colors = ledger.Colors,
                motion = ledger.Motion
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            "markdown" => GenerateMarkdown(profile, ledger, llmCaption),
            "signals" => string.Join("\n", profile.GetAllSignals()
                .Select(s => $"{s.Key}: {FormatSignalValue(s.Value)} ({s.Confidence:P0})")),
            _ => llmCaption ?? ledger.ToLlmSummary()
        };
    }

    private string GenerateAltText(
        Mostlylucid.DocSummarizer.Images.Models.Dynamic.DynamicImageProfile profile,
        Mostlylucid.DocSummarizer.Images.Models.Dynamic.ImageLedger ledger,
        string? llmCaption)
    {
        var parts = new System.Collections.Generic.List<string>();

        if (!string.IsNullOrWhiteSpace(llmCaption))
        {
            // Use LLM caption (may already include animation context)
            var caption = llmCaption.Trim();
            // Remove any trailing incomplete sentences
            if (caption.EndsWith(",") || caption.EndsWith(" and") || caption.EndsWith(" with"))
            {
                caption = caption.TrimEnd(',', ' ');
                var lastPeriod = caption.LastIndexOf('.');
                if (lastPeriod > 0)
                    caption = caption[..(lastPeriod + 1)];
            }
            parts.Add(caption);

            // Only add animation context if LLM caption doesn't mention it
            var captionLower = caption.ToLowerInvariant();
            var mentionsAnimation = captionLower.Contains("animated") ||
                                    captionLower.Contains("animation") ||
                                    captionLower.Contains("gif") ||
                                    captionLower.Contains("moving") ||
                                    captionLower.Contains("motion");

            if (ledger.Identity.IsAnimated && ledger.Motion != null && !mentionsAnimation)
            {
                if (!string.IsNullOrWhiteSpace(ledger.Motion.Summary))
                {
                    parts.Add($"Animated with {ledger.Motion.Summary.ToLowerInvariant()}");
                }
                else
                {
                    parts.Add($"Animated GIF ({ledger.Motion.FrameCount} frames)");
                }
            }
        }
        else
        {
            // Try Florence2 caption first, then heuristic fallback
            var florence2Caption = profile.GetValue<string>("florence2.caption");
            if (!string.IsNullOrWhiteSpace(florence2Caption))
            {
                // Clean and use Florence2 caption
                var caption = florence2Caption.Trim();

                // Remove redundant prefixes
                var prefixPatterns = new[]
                {
                    @"^Animated\s+(?:GIF|PNG|WebP)\s*\(\d+\s*frames?\)[\s:,.-]*",
                    @"^In this (?:image|animated gif|gif)[\s:,.-]*",
                    @"^This (?:image|animated gif|gif) shows[\s:,.-]*",
                    @"^The image shows[\s:,.-]*"
                };

                foreach (var pattern in prefixPatterns)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        caption, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        caption = caption.Substring(match.Length).TrimStart();
                        break;
                    }
                }

                if (caption.Length > 0 && char.IsLower(caption[0]))
                    caption = char.ToUpper(caption[0]) + caption[1..];

                parts.Add(caption);

                // Add animation context if Florence2 didn't mention it
                var captionLower = caption.ToLowerInvariant();
                var mentionsAnimation = captionLower.Contains("animated") ||
                                        captionLower.Contains("animation") ||
                                        captionLower.Contains("gif") ||
                                        captionLower.Contains("moving") ||
                                        captionLower.Contains("motion");

                if (ledger.Identity.IsAnimated && ledger.Motion != null && !mentionsAnimation)
                {
                    if (!string.IsNullOrWhiteSpace(ledger.Motion.Summary))
                    {
                        parts.Add($"Animated with {ledger.Motion.Summary.ToLowerInvariant()}");
                    }
                    else
                    {
                        parts.Add($"Animated GIF ({ledger.Motion.FrameCount} frames)");
                    }
                }
            }
            else
            {
                // Ultimate fallback: use heuristic
                parts.Add(ledger.ToAltTextContext());
            }
        }

        // Add extracted text if present and not already in caption
        var text = GetExtractedText(profile);
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Check if LLM caption already includes the text
            var captionIncludesText = !string.IsNullOrWhiteSpace(llmCaption) &&
                                       llmCaption.Contains(text.Trim()[..Math.Min(20, text.Trim().Length)]);
            if (!captionIncludesText)
            {
                var truncated = text.Length > 80 ? text[..77] + "..." : text;
                parts.Add($"Text: \"{truncated.Trim()}\"");
            }
        }

        return string.Join(". ", parts);
    }

    private string GenerateMarkdown(
        Mostlylucid.DocSummarizer.Images.Models.Dynamic.DynamicImageProfile profile,
        Mostlylucid.DocSummarizer.Images.Models.Dynamic.ImageLedger ledger,
        string? llmCaption)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {Path.GetFileName(profile.ImagePath)}");
        sb.AppendLine();
        sb.AppendLine($"**Dimensions:** {ledger.Identity.Width}x{ledger.Identity.Height}");
        sb.AppendLine($"**Format:** {ledger.Identity.Format}");

        // Add animation info
        if (ledger.Identity.IsAnimated && ledger.Motion != null)
        {
            sb.AppendLine($"**Animation:** {ledger.Motion.FrameCount} frames");
            if (!string.IsNullOrWhiteSpace(ledger.Motion.Summary))
            {
                sb.AppendLine($"**Motion:** {ledger.Motion.Summary}");
            }
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(llmCaption))
        {
            sb.AppendLine("## Description");
            sb.AppendLine(llmCaption);
            sb.AppendLine();
        }

        var text = GetExtractedText(profile);
        if (!string.IsNullOrWhiteSpace(text))
        {
            sb.AppendLine("## Extracted Text");
            sb.AppendLine("```");
            sb.AppendLine(text.Trim());
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generate an adaptive, detailed description based on image content.
    /// Adapts the output style to the image type (animated, text-heavy, photo, etc.)
    /// </summary>
    private string GenerateAdaptiveDescription(
        Mostlylucid.DocSummarizer.Images.Models.Dynamic.DynamicImageProfile profile,
        Mostlylucid.DocSummarizer.Images.Models.Dynamic.ImageLedger ledger,
        string? llmCaption)
    {
        var sb = new System.Text.StringBuilder();

        // Get the best caption
        var caption = llmCaption
            ?? profile.GetValue<string>("florence2.caption")
            ?? ledger.ToLlmSummary();

        // Clean up the caption
        if (!string.IsNullOrWhiteSpace(caption))
        {
            caption = caption.Trim();

            // Remove redundant prefixes that we add ourselves
            var prefixPatterns = new[]
            {
                @"^Animated\s+(?:GIF|PNG|WebP)\s*\(\d+\s*frames?\)[\s:,.-]*",
                @"^In this (?:image|animated gif|gif)[\s:,.-]*",
                @"^This (?:image|animated gif|gif) shows[\s:,.-]*",
                @"^The image shows[\s:,.-]*"
            };

            foreach (var pattern in prefixPatterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    caption, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    caption = caption.Substring(match.Length).TrimStart();
                    break;
                }
            }

            // Capitalize first letter after cleanup
            if (caption.Length > 0 && char.IsLower(caption[0]))
            {
                caption = char.ToUpper(caption[0]) + caption[1..];
            }
        }

        // Check image characteristics
        var isAnimated = ledger.Identity.IsAnimated;
        var ocrText = GetExtractedText(profile);
        var hasText = !string.IsNullOrWhiteSpace(ocrText);
        var hasMotion = !string.IsNullOrWhiteSpace(profile.GetValue<string>("motion.summary"));
        var scene = profile.GetValue<string>("vision.llm.scene");

        // Build adaptive description
        if (isAnimated)
        {
            // ANIMATED GIF - Provide rich, detailed paragraph description
            var frameCount = ledger.Motion?.FrameCount ?? profile.GetValue<int>("identity.frame_count");
            var motionSummary = profile.GetValue<string>("motion.summary") ?? "";
            var motionType = ledger.Motion?.MotionType ?? "general";
            var colors = ledger.Colors?.DominantColors?.Take(3)
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList() ?? new System.Collections.Generic.List<string>();

            // Header line
            sb.AppendLine($"📽️ Animated {ledger.Identity.Format} ({frameCount} frames)");
            sb.AppendLine();

            // Build a rich paragraph description
            var paragraphParts = new System.Collections.Generic.List<string>();

            // Start with the main caption
            if (!string.IsNullOrWhiteSpace(caption))
            {
                paragraphParts.Add(caption.TrimEnd('.'));
            }

            // Add animation/motion details
            if (!string.IsNullOrWhiteSpace(motionSummary))
            {
                paragraphParts.Add($"The animation shows {motionSummary.ToLowerInvariant()}");
            }
            else if (hasMotion)
            {
                paragraphParts.Add($"The animation features {motionType} movement across {frameCount} frames");
            }

            // Add scene context
            if (!string.IsNullOrWhiteSpace(scene))
            {
                paragraphParts.Add($"Set in a {scene.ToLowerInvariant()} environment");
            }

            // Add color context for visual richness
            if (colors.Count > 0)
            {
                var colorList = string.Join(", ", colors.Take(2));
                paragraphParts.Add($"The color palette features predominantly {colorList.ToLowerInvariant()} tones");
            }

            // Build the paragraph
            sb.AppendLine(string.Join(". ", paragraphParts) + ".");

            // CRITICAL: Always include OCR text prominently for animated GIFs with text
            if (hasText)
            {
                sb.AppendLine();
                sb.AppendLine("📝 Extracted Text:");
                // Show more text for animated GIFs - up to 300 chars
                var displayText = Mostlylucid.DocSummarizer.Services.ShortTextSummarizer.Summarize(ocrText!.Trim(), 300);
                sb.AppendLine($"\"{displayText}\"");
            }
        }
        else
        {
            // STATIC IMAGE - Standard description
            sb.AppendLine(caption);

            // Show OCR text if available
            if (hasText)
            {
                sb.AppendLine();
                sb.AppendLine("📝 Text:");
                var displayText = Mostlylucid.DocSummarizer.Services.ShortTextSummarizer.Summarize(ocrText!.Trim(), 200);
                sb.AppendLine($"\"{displayText}\"");
            }

            // Add scene context if available and not already in caption
            if (!string.IsNullOrWhiteSpace(scene) &&
                !string.IsNullOrWhiteSpace(caption) &&
                !caption.ToLowerInvariant().Contains(scene.ToLowerInvariant()))
            {
                sb.AppendLine();
                sb.AppendLine($"📍 Scene: {scene}");
            }
        }

        // Check if OCR was skipped (for debugging)
        if (!hasText)
        {
            var ocrSkipped = profile.HasSignal("ocr.skipped") || profile.HasSignal("ocr.quality.not_evaluated");
            var textLikeliness = profile.GetValue<double>("content.text_likeliness");
            if (ocrSkipped && textLikeliness > 0.3)
            {
                sb.AppendLine();
                sb.AppendLine($"⚠️ OCR skipped (text_likeliness: {textLikeliness:P0})");
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Format text output with route info, caption, and OCR text.
    /// </summary>
    private string FormatTextOutput(
        Mostlylucid.DocSummarizer.Images.Models.Dynamic.DynamicImageProfile profile,
        string? llmCaption)
    {
        var parts = new System.Collections.Generic.List<string>();

        // Show route info if available (from auto pipeline)
        var route = profile.GetValue<string>("route.selected");
        var reason = profile.GetValue<string>("route.reason");
        if (!string.IsNullOrEmpty(route))
        {
            parts.Add($"[{route.ToUpperInvariant()} route: {reason}]");
        }

        // Show OCR text if found
        var ocrText = GetExtractedText(profile);
        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            parts.Add(ocrText.Trim());
        }

        // Show caption (prefer LLM, fallback to Florence2)
        var caption = llmCaption ?? profile.GetValue<string>("florence2.caption");
        if (!string.IsNullOrWhiteSpace(caption))
        {
            parts.Add($"Caption: {caption}");
        }

        // Show scene if available
        var scene = profile.GetValue<string>("vision.llm.scene");
        if (!string.IsNullOrWhiteSpace(scene))
        {
            parts.Add($"Scene: {scene}");
        }

        // Show motion info for animated images
        var motionSummary = profile.GetValue<string>("motion.summary");
        if (!string.IsNullOrWhiteSpace(motionSummary))
        {
            parts.Add($"Motion: {motionSummary}");
        }

        return parts.Count > 0 ? string.Join("\n", parts) : "No content extracted";
    }

    private string? GetExtractedText(Mostlylucid.DocSummarizer.Images.Models.Dynamic.DynamicImageProfile profile)
    {
        // Check all possible OCR signal keys in priority order
        var signalKeys = new[]
        {
            "vision.llm.text",           // Vision LLM extracted text (best quality)
            "ocr.final.corrected_text",  // Tier 2/3 corrections
            "ocr.corrected.text",        // Legacy Tier 3 signal
            "ocr.voting.consensus_text", // Temporal voting
            "ocr.temporal_median.full_text", // Temporal median
            "ocr.full_text",             // Full OCR text
            "ocr.text",                  // Raw OCR
            "florence2.ocr_text",        // Florence-2 OCR
            "content.extracted_text"     // Generic extracted text
        };

        foreach (var key in signalKeys)
        {
            if (profile.HasSignal(key))
            {
                var text = profile.GetValue<string>(key);
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 1)
                {
                    // Filter out obviously garbled OCR (repeated patterns, all caps nonsense)
                    if (IsGarbledText(text))
                        continue;
                    return text;
                }
            }
        }

        // Check motion-detected text (e.g., "Text "Back of the net" moving along bottom edge")
        var movingObjects = profile.GetValue<string[]>("motion.moving_objects");
        if (movingObjects != null)
        {
            foreach (var moving in movingObjects)
            {
                // Look for text patterns like: Text "..." or text "..."
                var match = System.Text.RegularExpressions.Regex.Match(
                    moving ?? "",
                    @"[Tt]ext\s*[""']([^""']+)[""']",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && match.Groups[1].Value.Length > 2)
                {
                    return match.Groups[1].Value;
                }
            }
        }

        // Check for text in Florence2 caption (last resort - extract quoted text)
        var caption = profile.GetValue<string>("florence2.caption");
        if (!string.IsNullOrWhiteSpace(caption))
        {
            // Look for "caption" patterns like: caption "..." or "..." in caption
            var captionMatch = System.Text.RegularExpressions.Regex.Match(
                caption,
                @"caption\s*[""']([^""']+)[""']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (captionMatch.Success && captionMatch.Groups[1].Value.Length > 2)
            {
                return captionMatch.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Check if OCR text appears to be garbled/nonsense.
    /// </summary>
    private static bool IsGarbledText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 3)
            return true;

        // Check for repeated short patterns (e.g., "LAPT LAP TAPT")
        var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 3)
        {
            var uniqueWords = new System.Collections.Generic.HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
            // If most words are nearly duplicates (edit distance 1-2), it's garbled
            if (uniqueWords.Count < words.Length / 2)
                return true;
        }

        // Check for excessive consonant clusters (no vowels)
        var consonantRun = 0;
        var maxConsonantRun = 0;
        var vowels = "aeiouAEIOU";
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                if (vowels.Contains(c))
                    consonantRun = 0;
                else
                    consonantRun++;
                maxConsonantRun = Math.Max(maxConsonantRun, consonantRun);
            }
        }
        if (maxConsonantRun >= 5) // 5+ consonants in a row is unusual in English
            return true;

        // Check for obvious OCR garbage patterns
        var garbledPatterns = new[]
        {
            @"^[A-Z\s]{5,}$",  // All caps with no lowercase or punctuation
            @"(.{2,4})\s*\1",  // Repeated short patterns
            @"[^\w\s]{3,}"     // 3+ special characters in a row
        };

        foreach (var pattern in garbledPatterns)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(text, pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Extract clean caption from LLM response, handling JSON or verbose text.
    /// Sanitizes to remove prompt leakage.
    /// </summary>
    private string? ExtractCleanCaption(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        string? rawCaption = null;

        // Try to extract from JSON format: {"caption": "..."}
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonStr = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = System.Text.Json.JsonDocument.Parse(jsonStr);

                // Check multiple property names
                foreach (var propName in new[] { "caption", "description", "scene", "summary" })
                {
                    if (doc.RootElement.TryGetProperty(propName, out var prop))
                    {
                        var val = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            rawCaption = val;
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
            // JSON parsing failed, continue to text cleanup
        }

        // Fallback: Try regex extraction for {"caption": "..."}
        if (rawCaption == null)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                response, @"""(?:caption|description)""\s*:\s*""([^""]+)""");
            if (match.Success && match.Groups.Count > 1)
            {
                rawCaption = match.Groups[1].Value;
            }
        }

        // If no JSON structure found, use plain text
        if (rawCaption == null && !response.TrimStart().StartsWith("{"))
        {
            rawCaption = response.Trim();
        }

        // Last resort: Try to find any quoted string
        if (rawCaption == null)
        {
            var quotedMatch = System.Text.RegularExpressions.Regex.Match(response, @"""([^""]{20,})""");
            if (quotedMatch.Success)
            {
                rawCaption = quotedMatch.Groups[1].Value;
            }
        }

        // Sanitize and return
        return SanitizeCaption(rawCaption);
    }

    /// <summary>
    /// Remove prompt leakage and instruction text from captions.
    /// </summary>
    private static string? SanitizeCaption(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return null;

        var result = caption.Trim();

        // Common prompt leakage patterns to strip
        var leakagePatterns = new[]
        {
            // Long verbose patterns (check first)
            @"^Based on (?:the )?(?:provided |given )?(?:visual )?(?:information|image|analysis).*?(?:here's|here is).*?(?:description|caption|summary).*?[:,]\s*",
            @"^(?:Here is|Here's) (?:a |the )?(?:structured )?(?:output|description|caption|summary).*?(?:in )?(?:JSON )?(?:format)?.*?[:,]\s*",
            @"^.*?(?:in JSON format|JSON format that|structured description|structured output).*?[:,]\s*",

            // "The provided/given image" patterns
            @"^(?:The |This )?(?:provided |given )?image (?:appears|seems) to (?:be |show |depict |display |feature |contain )?",
            @"^(?:The |This )?(?:provided |given )?image (?:shows|depicts|displays|features|contains|presents)\s*",

            // Standard patterns
            @"^Based on (?:the |this )?(provided |given )?image.*?[:,]\s*",
            @"^According to the (?:image|guidelines|analysis).*?[:,]\s*",
            @"^(?:The |This )?image (?:shows|depicts|displays|features|contains|presents)\s*",
            @"^In (?:the |this )?image,?\s*",
            @"^(?:Here is|Here's) (?:a|the) (?:caption|description).*?:\s*",
            @"^(?:Here is|Here's) (?:a |the )?(?:structured )?.*?[:,]\s*",
            @"^For accessibility[:,]\s*",
            @"^(?:Caption|Description|Summary):\s*",
            @"^\{[^}]*\}\s*", // Leading JSON
            @"^""[^""]*"":\s*""?", // Partial JSON key
            @"\s*\{[^}]*$", // Trailing incomplete JSON
            @"^```(?:json)?\s*", // Code block start
            @"\s*```$", // Code block end
            @"^I (?:can )?see\s+",
            @"^(?:Looking at (?:the|this) image,?\s*)?",
            @"^(?:Sure|Certainly|Of course)[!,.]?\s*",
        };

        foreach (var pattern in leakagePatterns)
        {
            result = System.Text.RegularExpressions.Regex.Replace(
                result, pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Clean up quotes and whitespace
        result = result.Trim('"', '\'', ' ');

        // Capitalize first letter
        if (result.Length > 0 && char.IsLower(result[0]))
        {
            result = char.ToUpper(result[0]) + result[1..];
        }

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>
    /// Format signal values for display, serializing complex types to JSON.
    /// </summary>
    private static string FormatSignalValue(object? value)
    {
        if (value == null)
            return "null";

        var type = value.GetType();

        // Primitive types - display directly
        if (type.IsPrimitive || value is string || value is decimal)
            return value.ToString() ?? "null";

        // DateTime
        if (value is DateTime dt)
            return dt.ToString("O");

        // Arrays of primitives
        if (value is float[] floatArray)
            return $"float[{floatArray.Length}]";
        if (value is double[] doubleArray)
            return $"double[{doubleArray.Length}]";

        // Check if it's a collection
        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var items = enumerable.Cast<object>().ToList();
            if (items.Count == 0)
                return "[]";

            // For small collections, try to serialize
            if (items.Count <= 5)
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Serialize(value,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
                }
                catch
                {
                    return $"[{items.Count} items]";
                }
            }

            return $"[{items.Count} items]";
        }

        // Complex objects - try JSON serialization
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(value,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
            // Truncate very long JSON
            if (json.Length > 200)
                return json[..197] + "...";
            return json;
        }
        catch
        {
            return value.ToString() ?? type.Name;
        }
    }

    /// <summary>
    /// Format signal values compactly for the log (max 40 chars)
    /// </summary>
    private static string FormatSignalValueCompact(object? value)
    {
        if (value == null)
            return "null";

        var str = value switch
        {
            bool b => b ? "true" : "false",
            string s => s.Length > 35 ? s[..32] + "..." : s,
            float[] fa => $"[{fa.Length} floats]",
            double[] da => $"[{da.Length} doubles]",
            int[] ia => $"[{ia.Length} ints]",
            System.Collections.IEnumerable e when e is not string => $"[{e.Cast<object>().Count()} items]",
            _ => value.ToString() ?? "?"
        };

        return str.Length > 40 ? str[..37] + "..." : str;
    }

    /// <summary>
    /// Truncate text to WCAG-compliant length (~125 chars max).
    /// </summary>
    private static string TruncateForWcag(string text, int maxLength = 125)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        text = text.Trim();

        if (text.Length <= maxLength)
            return text;

        // Find last sentence boundary within limit
        var truncated = text[..maxLength];
        var lastPeriod = truncated.LastIndexOf('.');
        if (lastPeriod > 40)
        {
            return truncated[..(lastPeriod + 1)];
        }

        // No good sentence boundary, truncate at word boundary
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > 40)
        {
            return truncated[..lastSpace] + "...";
        }

        return truncated[..(maxLength - 3)] + "...";
    }

    [RelayCommand]
    private async Task CopyToClipboardAsync(Avalonia.Input.Platform.IClipboard? clipboard)
    {
        if (clipboard == null || string.IsNullOrEmpty(ResultText)) return;

        await clipboard.SetTextAsync(ResultText);
        StatusText = "Copied to clipboard!";
    }

    public async Task HandleDropAsync(string[] files)
    {
        if (files.Length > 0)
        {
            await LoadImageAsync(files[0]);
        }
    }

    /// <summary>
    /// Generate a filmstrip thumbnail from an animated GIF.
    /// Creates a horizontal strip of evenly-sampled frames.
    /// </summary>
    private async Task<Bitmap?> GenerateFilmstripAsync(string imagePath, int totalFrames)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(imagePath);

                // Sample up to 6 frames evenly across the animation
                var maxFrames = Math.Min(6, totalFrames);
                var step = totalFrames / maxFrames;
                var frameIndices = Enumerable.Range(0, maxFrames).Select(i => i * step).ToList();

                // Target thumbnail size: 80px height per frame
                var thumbHeight = 80;
                var aspectRatio = (double)image.Width / image.Height;
                var thumbWidth = (int)(thumbHeight * aspectRatio);

                // Create filmstrip image
                var filmstripWidth = thumbWidth * maxFrames;
                using var filmstrip = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(filmstripWidth, thumbHeight);

                var frameCollection = image.Frames;
                for (var i = 0; i < frameIndices.Count; i++)
                {
                    var frameIndex = Math.Min(frameIndices[i], frameCollection.Count - 1);

                    // Clone the specific frame
                    using var frameImage = image.Frames.CloneFrame(frameIndex);

                    // Resize to thumbnail
                    frameImage.Mutate(ctx => ctx.Resize(thumbWidth, thumbHeight));

                    // Draw into filmstrip at the correct position
                    var xOffset = i * thumbWidth;
                    for (var y = 0; y < thumbHeight; y++)
                    {
                        for (var x = 0; x < thumbWidth; x++)
                        {
                            var pixel = frameImage[x, y];
                            filmstrip[xOffset + x, y] = pixel;
                        }
                    }
                }

                // Convert to Avalonia Bitmap
                using var ms = new MemoryStream();
                filmstrip.SaveAsPng(ms);
                ms.Position = 0;
                return new Bitmap(ms);
            }
            catch (Exception)
            {
                return null;
            }
        });
    }
}
