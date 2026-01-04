using System.Text.Json;
using System.Text.Json.Serialization;
using Mostlylucid.DocSummarizer.Images.Models;
using LucidRAG.ImageCli.Services.VisionClients;

namespace LucidRAG.ImageCli.Services.OutputFormatters;

/// <summary>
/// Formats image analysis results as JSON.
/// </summary>
public class JsonFormatter : IOutputFormatter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FormatSingle(string filePath, ImageProfile profile, string? llmCaption = null, string? extractedText = null, GifMotionProfile? gifMotion = null, List<EvidenceClaim>? evidenceClaims = null)
    {
        var result = new
        {
            filePath,
            analysis = new
            {
                // Identity
                sha256 = profile.Sha256,
                format = profile.Format,
                dimensions = new
                {
                    width = profile.Width,
                    height = profile.Height,
                    aspectRatio = profile.AspectRatio
                },
                hasExif = profile.HasExif,

                // Type detection
                detectedType = new
                {
                    type = profile.DetectedType.ToString(),
                    confidence = profile.TypeConfidence
                },

                // Visual characteristics
                visual = new
                {
                    edgeDensity = profile.EdgeDensity,
                    luminanceEntropy = profile.LuminanceEntropy,
                    meanLuminance = profile.MeanLuminance,
                    luminanceStdDev = profile.LuminanceStdDev,
                    clippedBlacksPercent = profile.ClippedBlacksPercent,
                    clippedWhitesPercent = profile.ClippedWhitesPercent,
                    compressionArtifacts = profile.CompressionArtifacts
                },

                // Sharpness
                sharpness = new
                {
                    laplacianVariance = profile.LaplacianVariance,
                    category = CategorizeSharpness(profile.LaplacianVariance)
                },

                // Text detection
                textLikeliness = profile.TextLikeliness,
                salientRegions = profile.SalientRegions,

                // Color analysis
                colors = new
                {
                    dominant = profile.DominantColors?.Select(c => new
                    {
                        hex = c.Hex,
                        name = c.Name,
                        percentage = c.Percentage
                    }),
                    colorGrid = profile.ColorGrid != null ? new
                    {
                        rows = profile.ColorGrid.Rows,
                        cols = profile.ColorGrid.Cols,
                        cells = profile.ColorGrid.Cells?.Select(c => new
                        {
                            row = c.Row,
                            col = c.Col,
                            hex = c.Hex,
                            coverage = c.Coverage
                        })
                    } : null,
                    meanSaturation = profile.MeanSaturation,
                    isGrayscale = profile.IsMostlyGrayscale
                },

                // Hashing
                perceptualHash = profile.PerceptualHash
            },

            // GIF/WebP motion analysis
            gifMotion = gifMotion != null ? new
            {
                frameCount = gifMotion.FrameCount,
                frameDelayMs = gifMotion.FrameDelayMs,
                fps = gifMotion.Fps,
                totalDurationMs = gifMotion.TotalDurationMs,
                loops = gifMotion.Loops,
                motionDirection = gifMotion.MotionDirection,
                motionMagnitude = gifMotion.MotionMagnitude,
                maxMotionMagnitude = gifMotion.MaxMotionMagnitude,
                motionPercentage = gifMotion.MotionPercentage,
                confidence = gifMotion.Confidence,
                complexity = gifMotion.Complexity != null ? new
                {
                    animationType = gifMotion.Complexity.AnimationType,
                    overallComplexity = gifMotion.Complexity.OverallComplexity,
                    visualStability = gifMotion.Complexity.VisualStability,
                    colorVariation = gifMotion.Complexity.ColorVariation,
                    entropyVariation = gifMotion.Complexity.EntropyVariation,
                    sceneChangeCount = gifMotion.Complexity.SceneChangeCount,
                    averageFrameDifference = gifMotion.Complexity.AverageFrameDifference,
                    maxFrameDifference = gifMotion.Complexity.MaxFrameDifference
                } : null
            } : null,

            // Optional LLM/OCR results
            llmCaption,
            extractedText,

            // Evidence claims
            evidenceClaims = evidenceClaims?.Select(c => new
            {
                text = c.Text,
                sources = c.Sources,
                evidence = c.Evidence
            })
        };

        return JsonSerializer.Serialize(result, Options);
    }

    public string FormatBatch(IEnumerable<ImageAnalysisResult> results)
    {
        var resultsList = results.ToList();

        var output = new
        {
            summary = new
            {
                totalImages = resultsList.Count,
                successful = resultsList.Count(r => r.Profile != null),
                failed = resultsList.Count(r => r.Profile == null),
                escalated = resultsList.Count(r => r.WasEscalated)
            },
            results = resultsList.Select(r => new
            {
                filePath = r.FilePath,
                success = r.Profile != null,
                wasEscalated = r.WasEscalated,
                error = r.Error,
                profile = r.Profile != null ? new
                {
                    detectedType = r.Profile.DetectedType.ToString(),
                    typeConfidence = r.Profile.TypeConfidence,
                    width = r.Profile.Width,
                    height = r.Profile.Height,
                    aspectRatio = r.Profile.AspectRatio,
                    edgeDensity = r.Profile.EdgeDensity,
                    laplacianVariance = r.Profile.LaplacianVariance,
                    textLikeliness = r.Profile.TextLikeliness,
                    meanSaturation = r.Profile.MeanSaturation,
                    isGrayscale = r.Profile.IsMostlyGrayscale,
                    perceptualHash = r.Profile.PerceptualHash,
                    dominantColors = r.Profile.DominantColors?.Take(3).Select(c => new
                    {
                        hex = c.Hex,
                        name = c.Name,
                        percentage = c.Percentage
                    })
                } : null,
                gifMotion = r.GifMotion != null ? new
                {
                    frameCount = r.GifMotion.FrameCount,
                    fps = r.GifMotion.Fps,
                    motionDirection = r.GifMotion.MotionDirection,
                    motionMagnitude = r.GifMotion.MotionMagnitude,
                    motionPercentage = r.GifMotion.MotionPercentage,
                    confidence = r.GifMotion.Confidence
                } : null,
                llmCaption = r.LlmCaption,
                extractedText = r.ExtractedText
            })
        };

        return JsonSerializer.Serialize(output, Options);
    }

    public async Task WriteAsync(string content, string? outputPath = null)
    {
        if (!string.IsNullOrEmpty(outputPath))
        {
            await File.WriteAllTextAsync(outputPath, content);
            Console.WriteLine($"Output saved to: {outputPath}");
        }
        else
        {
            Console.WriteLine(content);
        }
    }

    private static string CategorizeSharpness(double laplacianVariance)
    {
        return laplacianVariance switch
        {
            < 100 => "Very Blurry",
            < 300 => "Blurry",
            < 500 => "Soft",
            < 1000 => "Sharp",
            _ => "Very Sharp"
        };
    }
}
