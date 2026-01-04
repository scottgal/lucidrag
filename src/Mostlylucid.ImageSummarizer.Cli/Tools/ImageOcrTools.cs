using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;

namespace Mostlylucid.ImageSummarizer.Cli.Tools;

[McpServerToolType]
public static class ImageOcrTools
{
    private static readonly string DefaultPipeline = Environment.GetEnvironmentVariable("OCR_PIPELINE") ?? "advancedocr";
    private static readonly string DefaultLanguage = Environment.GetEnvironmentVariable("OCR_LANGUAGE") ?? "en_US";

    /// <summary>
    /// Extract text from images using advanced OCR.
    /// </summary>
    [McpServerTool(Name = "extract_text_from_image")]
    [Description("Extract text from images (all ImageSharp formats: JPEG, PNG, GIF, BMP, TIFF, TGA, WebP, PBM) using advanced OCR. Supports animations with temporal voting for improved accuracy.")]
    public static async Task<string> ExtractTextFromImageAsync(
        [Description("Path to image file (supports all ImageSharp formats: JPEG, PNG, GIF, BMP, TIFF, TGA, WebP, PBM)")]
        string imagePath,
        [Description("OCR pipeline: 'simple' (fast), 'advanced' (balanced), or 'quality' (best accuracy)")]
        string? pipeline = null,
        [Description("Include quality signals and metadata in response")]
        bool includeSignals = false)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"File not found: {imagePath}"
                });
            }

            var services = new ServiceCollection();
            services.AddDocSummarizerImages(opt =>
            {
                opt.EnableOcr = true;
                opt.Ocr.SpellCheckLanguage = DefaultLanguage;
                opt.Ocr.PipelineName = pipeline ?? DefaultPipeline;
                opt.Ocr.UseAdvancedPipeline = true;
                opt.Ocr.TextDetectionConfidenceThreshold = 0;
            });

            var provider = services.BuildServiceProvider();
            var orchestrator = provider.GetRequiredService<WaveOrchestrator>();

            var profile = await orchestrator.AnalyzeAsync(imagePath);
            var ledger = profile.GetLedger();

            var result = new
            {
                success = true,
                file = Path.GetFileName(imagePath),
                text = ledger.Text.ExtractedText,
                confidence = ledger.Text.Confidence,
                duration_ms = profile.AnalysisDurationMs,
                quality = new
                {
                    spell_check_score = profile.GetValue<double>("ocr.quality.spell_check_score"),
                    is_garbled = profile.GetValue<bool>("ocr.quality.is_garbled"),
                    text_likeliness = profile.GetValue<double>("content.text_likeliness")
                },
                metadata = new
                {
                    frames_processed = profile.GetValue<int>("ocr.frames.extracted"),
                    stabilization_quality = profile.GetValue<double>("ocr.stabilization.confidence"),
                    frame_agreement = profile.GetValue<double>("ocr.voting.agreement_score"),
                    pipeline_used = pipeline ?? DefaultPipeline,
                    waves_executed = profile.ContributingWaves.Count
                },
                signals = includeSignals ? profile.GetAllSignals().Select(s => new
                {
                    source = s.Source,
                    key = s.Key,
                    value = s.Value?.ToString(),
                    confidence = s.Confidence
                }).ToList() : null
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                file = imagePath
            });
        }
    }

    /// <summary>
    /// Analyze image quality metrics without full OCR.
    /// </summary>
    [McpServerTool(Name = "analyze_image_quality")]
    [Description("Analyze image quality metrics: text likeliness, sharpness, color analysis, motion (for GIFs). Faster than full OCR.")]
    public static async Task<string> AnalyzeImageQualityAsync(
        [Description("Path to image file")] string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"File not found: {imagePath}"
                });
            }

            var services = new ServiceCollection();
            services.AddDocSummarizerImages(opt =>
            {
                opt.EnableOcr = false; // Quality analysis only, skip OCR
            });

            var provider = services.BuildServiceProvider();
            var orchestrator = provider.GetRequiredService<WaveOrchestrator>();

            var profile = await orchestrator.AnalyzeAsync(imagePath);
            var ledger = profile.GetLedger();

            var result = new
            {
                success = true,
                file = Path.GetFileName(imagePath),
                quality = new
                {
                    text_likeliness = ledger.Text.TextLikeliness,
                    sharpness = ledger.Quality.Sharpness,
                    brightness = ledger.Composition.Brightness,
                    saturation = ledger.Colors.MeanSaturation,
                    is_grayscale = ledger.Colors.IsGrayscale
                },
                identity = new
                {
                    format = ledger.Identity.Format,
                    width = ledger.Identity.Width,
                    height = ledger.Identity.Height,
                    aspect_ratio = ledger.Identity.AspectRatio,
                    file_size_kb = new FileInfo(imagePath).Length / 1024.0
                },
                colors = new
                {
                    dominant = ledger.Colors.DominantColors.Take(5).Select(c => new
                    {
                        hex = c.Hex,
                        percentage = c.Percentage,
                        name = c.Name
                    }).ToList()
                },
                motion = ledger.Motion != null ? new
                {
                    is_animated = ledger.Identity.IsAnimated,
                    frame_count = ledger.Motion.FrameCount,
                    motion_intensity = ledger.Motion.MotionIntensity
                } : null,
                duration_ms = profile.AnalysisDurationMs
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                file = imagePath
            });
        }
    }

    /// <summary>
    /// List available OCR pipelines.
    /// </summary>
    [McpServerTool(Name = "list_ocr_pipelines")]
    [Description("List available OCR pipelines with details on speed, quality, and features. Helps choose the right pipeline for the task.")]
    public static async Task<string> ListOcrPipelinesAsync()
    {
        try
        {
            var pipelineService = new Mostlylucid.DocSummarizer.Images.Services.Pipelines.PipelineService();
            var config = await pipelineService.LoadPipelinesAsync();

            var pipelines = config.Pipelines.Select(p => new
            {
                name = p.Name,
                display_name = p.DisplayName,
                description = p.Description,
                is_default = p.IsDefault,
                estimated_duration_seconds = p.EstimatedDurationSeconds,
                accuracy_improvement = p.AccuracyImprovement,
                phase_count = p.Phases.Count,
                phases = p.Phases.Select(ph => ph.Name).ToList()
            }).ToList();

            var result = new
            {
                success = true,
                default_pipeline = config.DefaultPipeline ?? "advancedocr",
                pipeline_count = pipelines.Count,
                pipelines,
                recommendation = new
                {
                    simple = "Clear text, speed critical (<1s)",
                    advanced = "GIFs, balanced quality/speed (2-3s) [DEFAULT]",
                    quality = "Forensic accuracy, willing to wait (10-15s)"
                }
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Batch extract text from multiple images.
    /// </summary>
    [McpServerTool(Name = "batch_extract_text")]
    [Description("Extract text from multiple images in a directory. Returns aggregated results with quality statistics.")]
    public static async Task<string> BatchExtractTextAsync(
        [Description("Directory path containing images")] string directoryPath,
        [Description("File pattern (e.g., '*.gif', '*.png')")] string pattern = "*.gif",
        [Description("OCR pipeline to use")] string? pipeline = null,
        [Description("Maximum number of files to process")] int maxFiles = 10)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Directory not found: {directoryPath}"
                });
            }

            var files = Directory.GetFiles(directoryPath, pattern)
                .Take(maxFiles)
                .ToList();

            if (files.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"No files matching pattern '{pattern}' found in directory"
                });
            }

            var results = new List<object>();
            var totalDuration = 0.0;
            var successCount = 0;

            foreach (var file in files)
            {
                var extractResult = await ExtractTextFromImageAsync(file, pipeline);
                var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extractResult);

                if (data != null && data.ContainsKey("success") && data["success"].GetBoolean())
                {
                    successCount++;
                    totalDuration += data["duration_ms"].GetDouble();

                    results.Add(new
                    {
                        file = Path.GetFileName(file),
                        text = data["text"].GetString(),
                        confidence = data["confidence"].GetDouble(),
                        quality_score = data["quality"].GetProperty("spell_check_score").GetDouble()
                    });
                }
                else
                {
                    results.Add(new
                    {
                        file = Path.GetFileName(file),
                        error = data?["error"].GetString() ?? "Unknown error"
                    });
                }
            }

            var finalResult = new
            {
                success = true,
                directory = directoryPath,
                pattern,
                files_found = files.Count,
                files_processed = successCount,
                total_duration_ms = totalDuration,
                average_duration_ms = totalDuration / Math.Max(successCount, 1),
                results
            };

            return JsonSerializer.Serialize(finalResult, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Summarize an animated GIF with motion analysis.
    /// </summary>
    [McpServerTool(Name = "summarize_animated_gif")]
    [Description("Generate a motion-aware summary of an animated GIF, including temporal changes, text extraction, and key visual features.")]
    public static async Task<string> SummarizeAnimatedGifAsync(
        [Description("Path to GIF file")] string imagePath,
        [Description("Include OCR text extraction")] bool includeText = true)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"File not found: {imagePath}"
                });
            }

            var services = new ServiceCollection();
            services.AddDocSummarizerImages(opt =>
            {
                opt.EnableOcr = includeText;
                opt.Ocr.SpellCheckLanguage = DefaultLanguage;
                opt.Ocr.PipelineName = DefaultPipeline;
                opt.Ocr.UseAdvancedPipeline = true;
            });

            var provider = services.BuildServiceProvider();
            var orchestrator = provider.GetRequiredService<WaveOrchestrator>();

            var profile = await orchestrator.AnalyzeAsync(imagePath);
            var ledger = profile.GetLedger();

            // Build motion-aware summary
            var summary = new System.Text.StringBuilder();

            // Basic identity
            summary.AppendLine($"{ledger.Identity.Format} image ({ledger.Identity.Width}×{ledger.Identity.Height})");

            // Animation details
            if (ledger.Motion != null && ledger.Identity.IsAnimated)
            {
                summary.AppendLine($"Animated: {ledger.Motion.FrameCount} frames");
                if (ledger.Motion.Duration.HasValue)
                {
                    summary.AppendLine($"Duration: {ledger.Motion.Duration:F1}s");
                }
                if (ledger.Motion.MotionIntensity > 0)
                {
                    var motionDesc = ledger.Motion.MotionIntensity switch
                    {
                        > 0.7 => "high motion",
                        > 0.4 => "moderate motion",
                        _ => "subtle motion"
                    };
                    summary.AppendLine($"Motion: {motionDesc} (intensity: {ledger.Motion.MotionIntensity:F2})");
                }
            }

            // Color theme
            if (ledger.Colors.IsGrayscale)
            {
                summary.AppendLine("Colors: Grayscale");
            }
            else if (ledger.Colors.DominantColors.Count > 0)
            {
                var topColors = string.Join(", ", ledger.Colors.DominantColors.Take(3).Select(c => c.Name ?? c.Hex));
                summary.AppendLine($"Colors: {topColors}");
            }

            // Text content
            if (includeText && !string.IsNullOrWhiteSpace(ledger.Text.ExtractedText))
            {
                summary.AppendLine($"Text: \"{ledger.Text.ExtractedText}\"");
                if (ledger.Text.Confidence > 0)
                {
                    summary.AppendLine($"Text confidence: {ledger.Text.Confidence:P0}");
                }
            }

            // Quality assessment
            if (ledger.Quality.Sharpness.HasValue)
            {
                var sharpnessDesc = ledger.Quality.Sharpness switch
                {
                    > 1000 => "very sharp",
                    > 500 => "sharp",
                    > 100 => "moderate",
                    _ => "soft"
                };
                summary.AppendLine($"Quality: {sharpnessDesc}");
            }

            var result = new
            {
                success = true,
                file = Path.GetFileName(imagePath),
                summary = summary.ToString().Trim(),
                structured = new
                {
                    identity = new
                    {
                        format = ledger.Identity.Format,
                        dimensions = $"{ledger.Identity.Width}×{ledger.Identity.Height}",
                        is_animated = ledger.Identity.IsAnimated
                    },
                    motion = ledger.Motion != null ? new
                    {
                        frame_count = ledger.Motion.FrameCount,
                        duration_seconds = ledger.Motion.Duration,
                        motion_intensity = ledger.Motion.MotionIntensity,
                        optical_flow_magnitude = ledger.Motion.OpticalFlowMagnitude
                    } : null,
                    colors = new
                    {
                        is_grayscale = ledger.Colors.IsGrayscale,
                        dominant = ledger.Colors.DominantColors.Take(5).Select(c => new
                        {
                            color = c.Name ?? c.Hex,
                            percentage = c.Percentage
                        }).ToList()
                    },
                    text = includeText ? new
                    {
                        extracted = ledger.Text.ExtractedText,
                        confidence = ledger.Text.Confidence,
                        word_count = ledger.Text.WordCount
                    } : null,
                    quality = new
                    {
                        sharpness = ledger.Quality.Sharpness,
                        overall = ledger.Quality.OverallQuality
                    }
                },
                duration_ms = profile.AnalysisDurationMs
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                file = imagePath
            });
        }
    }

    /// <summary>
    /// Generate a concise caption for an image.
    /// </summary>
    [McpServerTool(Name = "generate_caption")]
    [Description("Generate a concise, accessible caption for an image. Optimized for alt-text, social media, or quick descriptions.")]
    public static async Task<string> GenerateCaptionAsync(
        [Description("Path to image file")] string imagePath,
        [Description("Maximum caption length in characters")] int maxLength = 150)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"File not found: {imagePath}"
                });
            }

            var services = new ServiceCollection();
            services.AddDocSummarizerImages(opt =>
            {
                opt.EnableOcr = true;
                opt.Ocr.SpellCheckLanguage = DefaultLanguage;
                opt.Ocr.PipelineName = DefaultPipeline;
            });

            var provider = services.BuildServiceProvider();
            var orchestrator = provider.GetRequiredService<WaveOrchestrator>();

            var profile = await orchestrator.AnalyzeAsync(imagePath);
            var ledger = profile.GetLedger();

            // Use ledger's alt-text context as base
            var caption = ledger.ToAltTextContext();

            // Truncate if needed
            if (caption.Length > maxLength)
            {
                caption = caption.Substring(0, maxLength - 3) + "...";
            }

            var result = new
            {
                success = true,
                file = Path.GetFileName(imagePath),
                caption,
                length = caption.Length,
                components = new
                {
                    image_type = ledger.Identity.IsAnimated ? "Animated GIF" : ledger.Identity.Format,
                    has_text = !string.IsNullOrWhiteSpace(ledger.Text.ExtractedText),
                    primary_color = ledger.Colors.DominantColors.FirstOrDefault()?.Name,
                    is_grayscale = ledger.Colors.IsGrayscale
                },
                duration_ms = profile.AnalysisDurationMs
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                file = imagePath
            });
        }
    }

    /// <summary>
    /// Generate a detailed description of an image.
    /// </summary>
    [McpServerTool(Name = "generate_detailed_description")]
    [Description("Generate a comprehensive description of an image including composition, colors, text, motion (for GIFs), and quality assessment.")]
    public static async Task<string> GenerateDetailedDescriptionAsync(
        [Description("Path to image file")] string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"File not found: {imagePath}"
                });
            }

            var services = new ServiceCollection();
            services.AddDocSummarizerImages(opt =>
            {
                opt.EnableOcr = true;
                opt.Ocr.SpellCheckLanguage = DefaultLanguage;
                opt.Ocr.PipelineName = DefaultPipeline;
            });

            var provider = services.BuildServiceProvider();
            var orchestrator = provider.GetRequiredService<WaveOrchestrator>();

            var profile = await orchestrator.AnalyzeAsync(imagePath);
            var ledger = profile.GetLedger();

            // Use ledger's LLM summary as comprehensive description
            var description = ledger.ToLlmSummary();

            var result = new
            {
                success = true,
                file = Path.GetFileName(imagePath),
                description,
                detailed_breakdown = new
                {
                    technical = new
                    {
                        format = ledger.Identity.Format,
                        dimensions = $"{ledger.Identity.Width}×{ledger.Identity.Height}",
                        aspect_ratio = ledger.Identity.AspectRatio,
                        file_size_kb = new FileInfo(imagePath).Length / 1024.0
                    },
                    visual = new
                    {
                        dominant_colors = ledger.Colors.DominantColors.Take(5).Select(c => new
                        {
                            color = c.Name ?? c.Hex,
                            hex = c.Hex,
                            percentage = c.Percentage
                        }).ToList(),
                        is_grayscale = ledger.Colors.IsGrayscale,
                        complexity = ledger.Composition.Complexity,
                        edge_density = ledger.Composition.EdgeDensity
                    },
                    content = new
                    {
                        text = ledger.Text.ExtractedText,
                        text_confidence = ledger.Text.Confidence,
                        text_quality = ledger.Text.SpellCheckScore,
                        word_count = ledger.Text.WordCount
                    },
                    motion = ledger.Motion != null ? new
                    {
                        is_animated = ledger.Identity.IsAnimated,
                        frames = ledger.Motion.FrameCount,
                        duration = ledger.Motion.Duration,
                        intensity = ledger.Motion.MotionIntensity
                    } : null,
                    quality = new
                    {
                        sharpness = ledger.Quality.Sharpness,
                        overall = ledger.Quality.OverallQuality,
                        exposure = ledger.Quality.Exposure.ToString()
                    }
                },
                duration_ms = profile.AnalysisDurationMs
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                file = imagePath
            });
        }
    }

    /// <summary>
    /// Analyze image using a customizable output template.
    /// </summary>
    [McpServerTool(Name = "analyze_with_template")]
    [Description("Analyze an image and format the output using a predefined template or custom format string. Supports variable substitution and conditional formatting.")]
    public static async Task<string> AnalyzeWithTemplateAsync(
        [Description("Path to image file")] string imagePath,
        [Description("Template name (social_media, accessibility, seo, technical_report, animated_gif_summary, markdown_blog, content_moderation, json_structured) or 'custom'")]
        string templateName = "social_media",
        [Description("Custom format string (only used when templateName='custom'). Use {variable.path} for substitution.")]
        string? customFormat = null)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"File not found: {imagePath}"
                });
            }

            var services = new ServiceCollection();
            services.AddDocSummarizerImages(opt =>
            {
                opt.EnableOcr = true;
                opt.Ocr.SpellCheckLanguage = DefaultLanguage;
                opt.Ocr.PipelineName = DefaultPipeline;
            });

            var provider = services.BuildServiceProvider();
            var orchestrator = provider.GetRequiredService<WaveOrchestrator>();

            var profile = await orchestrator.AnalyzeAsync(imagePath);
            var ledger = profile.GetLedger();

            // Load template
            var templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "output-templates.json");
            string templateFormat;
            Dictionary<string, string> variables;

            if (templateName == "custom" && !string.IsNullOrEmpty(customFormat))
            {
                templateFormat = customFormat;
                variables = new Dictionary<string, string>();
            }
            else
            {
                if (!File.Exists(templatePath))
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "Template configuration file not found. Using templates is currently unavailable."
                    });
                }

                var templatesJson = await File.ReadAllTextAsync(templatePath);
                var templatesDoc = JsonDocument.Parse(templatesJson);
                var templatesArray = templatesDoc.RootElement.GetProperty("templates");

                JsonElement? selectedTemplate = null;
                foreach (var template in templatesArray.EnumerateArray())
                {
                    if (template.GetProperty("name").GetString() == templateName)
                    {
                        selectedTemplate = template;
                        break;
                    }
                }

                if (!selectedTemplate.HasValue)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Template '{templateName}' not found. Available templates: social_media, accessibility, seo, technical_report, animated_gif_summary, markdown_blog, content_moderation, json_structured, custom"
                    });
                }

                templateFormat = selectedTemplate.Value.GetProperty("format").GetString() ?? "{llm_summary}";
                variables = new Dictionary<string, string>();

                if (selectedTemplate.Value.TryGetProperty("variables", out var varsElement))
                {
                    foreach (var varProp in varsElement.EnumerateObject())
                    {
                        variables[varProp.Name] = varProp.Value.GetString() ?? "";
                    }
                }
            }

            // Build variable context from ledger
            var context = BuildVariableContext(ledger, profile, imagePath);

            // Process template
            var output = ProcessTemplate(templateFormat, variables, context);

            var result = new
            {
                success = true,
                file = Path.GetFileName(imagePath),
                template = templateName,
                output,
                duration_ms = profile.AnalysisDurationMs,
                available_variables = context.Keys.ToList()
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                file = imagePath
            });
        }
    }

    /// <summary>
    /// List available output templates.
    /// </summary>
    [McpServerTool(Name = "list_output_templates")]
    [Description("List all available output templates with descriptions and example usage.")]
    public static async Task<string> ListOutputTemplatesAsync()
    {
        try
        {
            var templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "output-templates.json");

            if (!File.Exists(templatePath))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Template configuration file not found"
                });
            }

            var templatesJson = await File.ReadAllTextAsync(templatePath);
            var templatesDoc = JsonDocument.Parse(templatesJson);

            var templates = templatesDoc.RootElement.GetProperty("templates").EnumerateArray()
                .Select(t => new
                {
                    name = t.GetProperty("name").GetString(),
                    description = t.GetProperty("description").GetString(),
                    has_max_length = t.TryGetProperty("max_length", out var maxLen) ? maxLen.GetInt32() : (int?)null
                }).ToList();

            var result = new
            {
                success = true,
                template_count = templates.Count,
                templates,
                usage = "Use analyze_with_template tool with template name to apply formatting"
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // Helper: Build variable context from ledger
    private static Dictionary<string, object?> BuildVariableContext(
        Mostlylucid.DocSummarizer.Images.Models.Dynamic.ImageLedger ledger,
        Mostlylucid.DocSummarizer.Images.Models.Dynamic.DynamicImageProfile profile,
        string imagePath)
    {
        var fileInfo = new FileInfo(imagePath);

        return new Dictionary<string, object?>
        {
            ["file"] = Path.GetFileName(imagePath),
            ["identity.format"] = ledger.Identity.Format,
            ["identity.width"] = ledger.Identity.Width,
            ["identity.height"] = ledger.Identity.Height,
            ["identity.dimensions"] = $"{ledger.Identity.Width}×{ledger.Identity.Height}",
            ["identity.aspect_ratio"] = ledger.Identity.AspectRatio,
            ["identity.is_animated"] = ledger.Identity.IsAnimated,
            ["identity.file_size"] = fileInfo.Length,
            ["identity.file_size_kb"] = fileInfo.Length / 1024.0,
            ["colors.dominant"] = ledger.Colors.DominantColors.ToList(),
            ["colors.is_grayscale"] = ledger.Colors.IsGrayscale,
            ["colors.mean_saturation"] = ledger.Colors.MeanSaturation,
            ["text.extracted_text"] = ledger.Text.ExtractedText,
            ["text.confidence"] = ledger.Text.Confidence,
            ["text.word_count"] = ledger.Text.WordCount,
            ["text.spell_check_score"] = ledger.Text.SpellCheckScore,
            ["text.is_garbled"] = ledger.Text.IsGarbled,
            ["motion.frame_count"] = ledger.Motion?.FrameCount,
            ["motion.duration"] = ledger.Motion?.Duration,
            ["motion.frame_rate"] = ledger.Motion?.FrameRate,
            ["motion.motion_intensity"] = ledger.Motion?.MotionIntensity,
            ["motion.optical_flow_magnitude"] = ledger.Motion?.OpticalFlowMagnitude,
            ["quality.sharpness"] = ledger.Quality.Sharpness,
            ["quality.overall"] = ledger.Quality.OverallQuality,
            ["quality.exposure"] = ledger.Quality.Exposure.ToString(),
            ["composition.complexity"] = ledger.Composition.Complexity,
            ["composition.edge_density"] = ledger.Composition.EdgeDensity,
            ["composition.brightness"] = ledger.Composition.Brightness,
            ["composition.contrast"] = ledger.Composition.Contrast,
            ["llm_summary"] = ledger.ToLlmSummary(),
            ["alt_text_context"] = ledger.ToAltTextContext()
        };
    }

    // Helper: Process template with variable substitution
    private static string ProcessTemplate(string template, Dictionary<string, string> variables, Dictionary<string, object?> context)
    {
        var output = template;

        // First, expand any nested variables in the variables dictionary
        foreach (var kvp in variables.ToList())
        {
            var expandedValue = ReplaceVariables(kvp.Value, context);
            variables[kvp.Key] = expandedValue;
        }

        // Then replace variable placeholders in the template
        output = Regex.Replace(output, @"\{([^}]+)\}", match =>
        {
            var placeholder = match.Groups[1].Value;

            // Check if it's a variable reference
            if (variables.ContainsKey(placeholder))
            {
                return variables[placeholder];
            }

            // Otherwise, try direct context lookup
            return GetContextValue(placeholder, context);
        });

        // Unescape newlines
        output = output.Replace("\\n", "\n");

        return output;
    }

    // Helper: Replace variables in a string
    private static string ReplaceVariables(string input, Dictionary<string, object?> context)
    {
        return Regex.Replace(input, @"\{([^}]+)\}", match =>
        {
            var placeholder = match.Groups[1].Value;
            return GetContextValue(placeholder, context);
        });
    }

    // Helper: Get value from context with fallback and ternary support
    private static string GetContextValue(string placeholder, Dictionary<string, object?> context)
    {
        // Handle fallback operator: {var|fallback}
        if (placeholder.Contains('|'))
        {
            var parts = placeholder.Split('|', 2);
            var value = GetContextValue(parts[0].Trim(), context);
            return string.IsNullOrWhiteSpace(value) ? parts[1].Trim() : value;
        }

        // Handle ternary operator: {condition?true:false}
        if (placeholder.Contains('?') && placeholder.Contains(':'))
        {
            var conditionMatch = Regex.Match(placeholder, @"(.+?)\?(.+?):(.+)");
            if (conditionMatch.Success)
            {
                var condition = conditionMatch.Groups[1].Value.Trim();
                var trueValue = conditionMatch.Groups[2].Value.Trim();
                var falseValue = conditionMatch.Groups[3].Value.Trim();

                var conditionResult = EvaluateCondition(condition, context);
                return conditionResult ? trueValue : falseValue;
            }
        }

        // Handle array indexing: {var[0].property}
        var arrayMatch = Regex.Match(placeholder, @"^([^\[]+)\[(\d+)\]\.(.+)$");
        if (arrayMatch.Success)
        {
            var arrayKey = arrayMatch.Groups[1].Value;
            var index = int.Parse(arrayMatch.Groups[2].Value);
            var property = arrayMatch.Groups[3].Value;

            if (context.TryGetValue(arrayKey, out var arrayObj) && arrayObj is System.Collections.IList list)
            {
                if (index < list.Count)
                {
                    var item = list[index];
                    if (item != null)
                    {
                        var propInfo = item.GetType().GetProperty(char.ToUpper(property[0]) + property.Substring(1));
                        if (propInfo != null)
                        {
                            var value = propInfo.GetValue(item);
                            return value?.ToString() ?? "";
                        }
                    }
                }
            }
            return "";
        }

        // Direct lookup
        if (context.TryGetValue(placeholder, out var obj))
        {
            return obj?.ToString() ?? "";
        }

        return $"{{{placeholder}}}"; // Return unchanged if not found
    }

    // Helper: Evaluate a condition
    private static bool EvaluateCondition(string condition, Dictionary<string, object?> context)
    {
        // Handle comparisons: {var>value}, {var<value}, {var==value}
        var comparisonMatch = Regex.Match(condition, @"(.+?)(>|<|==)(.+)");
        if (comparisonMatch.Success)
        {
            var left = GetContextValue(comparisonMatch.Groups[1].Value.Trim(), context);
            var op = comparisonMatch.Groups[2].Value;
            var right = comparisonMatch.Groups[3].Value.Trim();

            if (double.TryParse(left, out var leftNum) && double.TryParse(right, out var rightNum))
            {
                return op switch
                {
                    ">" => leftNum > rightNum,
                    "<" => leftNum < rightNum,
                    "==" => Math.Abs(leftNum - rightNum) < 0.0001,
                    _ => false
                };
            }

            return op == "==" && left == right;
        }

        // Boolean lookup
        var value = GetContextValue(condition, context);
        return !string.IsNullOrWhiteSpace(value) &&
               value != "False" &&
               value != "0" &&
               value != "false";
    }
}
