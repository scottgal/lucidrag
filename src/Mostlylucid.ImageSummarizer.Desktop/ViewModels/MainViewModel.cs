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

namespace Mostlylucid.ImageSummarizer.Desktop.ViewModels;

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
    private string _selectedPipeline = "socialmediaalt";

    [ObservableProperty]
    private string _selectedOutput = "alttext";

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private string? _statusText = "Drop an image or click Browse";

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private bool _enableVisionLlm = true;

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
    private string _ocrStatus = "Tesseract (local)";

    [ObservableProperty]
    private string _ollamaStatus = "Checking...";

    [ObservableProperty]
    private string _openCvStatus = "OpenCV (local)";

    [ObservableProperty]
    private string _fallbackMode = "Full analysis";

    // Color properties for status indicators (green=#22C55E, red=#EF4444, yellow=#FBBF24)
    public string OcrStatusColor => OcrAvailable ? "#22C55E" : "#EF4444";
    public string OpenCvStatusColor => OpenCvAvailable ? "#22C55E" : "#EF4444";
    public string OllamaStatusColor => OllamaAvailable ? "#22C55E" : "#EF4444";

    partial void OnOcrAvailableChanged(bool value) => OnPropertyChanged(nameof(OcrStatusColor));
    partial void OnOpenCvAvailableChanged(bool value) => OnPropertyChanged(nameof(OpenCvStatusColor));
    partial void OnOllamaAvailableChanged(bool value) => OnPropertyChanged(nameof(OllamaStatusColor));

    public ObservableCollection<string> Pipelines { get; } = new()
    {
        "socialmediaalt",
        "caption",
        "alttext",
        "vision",
        "motion",
        "advancedocr",
        "simpleocr",
        "quality",
        "stats"
    };

    public ObservableCollection<string> OutputFormats { get; } = new()
    {
        "alttext",
        "caption",
        "text",
        "json",
        "markdown",
        "signals"
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

                            // Select first available if current not in list
                            if (!visionModels.Contains(VisionModel) && visionModels.Count > 0)
                                VisionModel = visionModels[0];

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
    /// Update the fallback mode description based on available services.
    /// </summary>
    private void UpdateFallbackMode()
    {
        if (OllamaAvailable && OcrAvailable && OpenCvAvailable)
        {
            FallbackMode = "Full analysis (Vision LLM + OCR + OpenCV)";
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

        // Auto-analyze on load
        await AnalyzeAsync();
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

        try
        {
            // Rebuild services with current settings
            StatusText = "Initializing services...";
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Warning));
            services.AddDocSummarizerImages(opt =>
            {
                opt.EnableOcr = SelectedPipeline != "stats" && SelectedPipeline != "vision";
                opt.EnableVisionLlm = EnableVisionLlm;
                opt.VisionLlmModel = VisionModel;
                opt.OllamaBaseUrl = OllamaUrl;
                opt.Ocr.PipelineName = SelectedPipeline;
            });

            var provider = services.BuildServiceProvider();
            _orchestrator = provider.GetRequiredService<WaveOrchestrator>();

            StatusText = "Running wave analysis...";
            var profile = await _orchestrator.AnalyzeAsync(ImagePath);

            // Get escalation service for LLM caption
            string? llmCaption = null;
            if (EnableVisionLlm && SelectedPipeline is "caption" or "alttext" or "socialmediaalt" or "vision")
            {
                StatusText = $"Calling Vision LLM ({VisionModel})...";
                var escalationService = provider.GetService<Mostlylucid.DocSummarizer.Images.Services.EscalationService>();
                if (escalationService != null)
                {
                    var result = await escalationService.AnalyzeWithEscalationAsync(
                        ImagePath,
                        forceEscalate: true,
                        enableOcr: SelectedPipeline != "vision");
                    llmCaption = result.LlmCaption;
                }
            }

            // Format output based on selected format
            StatusText = "Formatting output...";
            ResultText = FormatOutput(profile, llmCaption);
            StatusText = $"Done in {profile.AnalysisDurationMs}ms - {profile.GetAllSignals().Count()} signals";
        }
        catch (Exception ex)
        {
            ResultText = $"Error: {ex.Message}";
            StatusText = "Analysis failed";
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
            "alttext" => GenerateAltText(profile, ledger, cleanCaption),
            "caption" => cleanCaption ?? ledger.ToLlmSummary(),
            "text" => GetExtractedText(profile) ?? "No text found",
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
            // Use heuristic fallback
            parts.Add(ledger.ToAltTextContext());
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

    private string? GetExtractedText(Mostlylucid.DocSummarizer.Images.Models.Dynamic.DynamicImageProfile profile)
    {
        if (profile.HasSignal("vision.llm.text"))
            return profile.GetValue<string>("vision.llm.text");
        if (profile.HasSignal("ocr.voting.consensus_text"))
            return profile.GetValue<string>("ocr.voting.consensus_text");
        if (profile.HasSignal("ocr.full_text"))
            return profile.GetValue<string>("ocr.full_text");
        return null;
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
}
