using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Ocr;
using Mostlylucid.DocSummarizer.Images.Services.Vision;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Complex mode wave - segments document and processes each region in parallel
/// Priority: 45 (runs after routing, before standard OCR)
/// </summary>
public class ComplexModeWave : IAnalysisWave
{
    private readonly DocumentLayoutAnalyzer _layoutAnalyzer;
    private readonly IOcrEngine? _ocrEngine;
    private readonly Florence2CaptionService? _florence2Service;
    private readonly VisionLlmService? _visionLlmService;
    private readonly ImageConfig _config;
    private readonly ILogger<ComplexModeWave>? _logger;

    public string Name => "ComplexModeWave";
    public int Priority => 45; // After routing, before OCR
    public IReadOnlyList<string> Tags => new[] { "complex", "segmentation", "parallel" };

    public ComplexModeWave(
        IOcrEngine? ocrEngine = null,
        IOptions<ImageConfig>? config = null,
        ILogger<ComplexModeWave>? logger = null,
        Florence2CaptionService? florence2Service = null,
        VisionLlmService? visionLlmService = null)
    {
        _ocrEngine = ocrEngine;
        _config = config?.Value ?? new ImageConfig();
        _logger = logger;
        _florence2Service = florence2Service;
        _visionLlmService = visionLlmService;
        _layoutAnalyzer = new DocumentLayoutAnalyzer(logger as ILogger<DocumentLayoutAnalyzer>);
    }

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Only run if complex mode is enabled
        if (!_config.ComplexMode.Enabled)
            return false;

        // Skip if routing says to skip
        if (context.IsWaveSkippedByRouting(Name))
            return false;

        return true;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        try
        {
            // 1. Analyze document layout
            var layout = await _layoutAnalyzer.AnalyzeAsync(imagePath, ct);

            if (layout.Segments.Count < _config.ComplexMode.MinSegments)
            {
                _logger?.LogDebug("Not enough segments ({Count} < {Min}), skipping complex mode",
                    layout.Segments.Count, _config.ComplexMode.MinSegments);

                signals.Add(new Signal
                {
                    Key = "complex.skipped",
                    Value = true,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "complex" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "insufficient_segments",
                        ["detected"] = layout.Segments.Count,
                        ["minimum"] = _config.ComplexMode.MinSegments
                    }
                });

                return signals;
            }

            // Emit layout signals
            signals.AddRange(EmitLayoutSignals(layout));

            // 2. Process segments in parallel
            var results = await ProcessSegmentsInParallelAsync(imagePath, layout.Segments, ct);

            // 3. Emit segment results
            signals.AddRange(EmitSegmentSignals(results));

            // 4. Reconstruct document
            var reconstructed = ReconstructDocument(results, layout);

            signals.Add(new Signal
            {
                Key = "complex.reconstructed_text",
                Value = reconstructed,
                Confidence = CalculateAverageConfidence(results),
                Source = Name,
                Tags = new List<string> { "complex", "text" }
            });

            sw.Stop();

            // Performance signal
            signals.Add(new Signal
            {
                Key = "complex.duration_ms",
                Value = sw.ElapsedMilliseconds,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "performance" },
                Metadata = new Dictionary<string, object>
                {
                    ["segments_processed"] = results.Count,
                    ["parallel"] = _config.ComplexMode.MaxParallelism
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ComplexModeWave failed");

            signals.Add(new Signal
            {
                Key = "complex.error",
                Value = ex.Message,
                Confidence = 0,
                Source = Name,
                Tags = new List<string> { "error" }
            });
        }

        return signals;
    }

    private IEnumerable<Signal> EmitLayoutSignals(DocumentLayout layout)
    {
        yield return new Signal
        {
            Key = "complex.segments_detected",
            Value = layout.Segments.Count,
            Confidence = layout.Confidence,
            Source = Name,
            Tags = new List<string> { "complex", "layout" }
        };

        yield return new Signal
        {
            Key = "complex.layout_type",
            Value = layout.LayoutType.ToString(),
            Confidence = layout.Confidence,
            Source = Name,
            Tags = new List<string> { "complex", "layout" }
        };

        yield return new Signal
        {
            Key = "complex.column_count",
            Value = layout.ColumnCount,
            Confidence = layout.Confidence,
            Source = Name,
            Tags = new List<string> { "complex", "layout" }
        };

        yield return new Signal
        {
            Key = "complex.is_color",
            Value = layout.IsColor,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "complex", "color" }
        };

        yield return new Signal
        {
            Key = "complex.average_saturation",
            Value = layout.AverageSaturation,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "complex", "color" }
        };
    }

    private async Task<List<SegmentResult>> ProcessSegmentsInParallelAsync(
        string imagePath,
        List<DocumentSegment> segments,
        CancellationToken ct)
    {
        var results = new List<SegmentResult>();

        // Process in batches respecting MaxParallelism
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _config.ComplexMode.MaxParallelism,
            CancellationToken = ct
        };

        var tasks = new List<Task<SegmentResult>>();

        foreach (var segment in segments)
        {
            tasks.Add(ProcessSegmentAsync(imagePath, segment, ct));
        }

        results.AddRange(await Task.WhenAll(tasks));

        return results;
    }

    private async Task<SegmentResult> ProcessSegmentAsync(
        string imagePath,
        DocumentSegment segment,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Crop segment from original image
            var segmentPath = await CropSegmentAsync(imagePath, segment, ct);

            // Convert to grayscale if no color (optimization)
            if (!segment.IsColor)
            {
                segmentPath = await ConvertToGrayscaleAsync(segmentPath, ct);
            }

            var result = segment.Type switch
            {
                SegmentType.TextBlock => await ProcessTextBlockAsync(segmentPath, segment, ct),
                SegmentType.Image => await ProcessImageAsync(segmentPath, segment, ct),
                SegmentType.Chart => await ProcessChartAsync(segmentPath, segment, ct),
                SegmentType.Table => await ProcessTableAsync(segmentPath, segment, ct),
                SegmentType.Caption => await ProcessCaptionAsync(segmentPath, segment, ct),
                _ => new SegmentResult { Id = segment.Id, Type = segment.Type }
            };

            result.DurationMs = sw.ElapsedMilliseconds;
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to process segment {Id} of type {Type}", segment.Id, segment.Type);

            return new SegmentResult
            {
                Id = segment.Id,
                Type = segment.Type,
                Content = $"[Error: {ex.Message}]",
                Confidence = 0,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    private async Task<string> CropSegmentAsync(
        string imagePath,
        DocumentSegment segment,
        CancellationToken ct)
    {
        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct);

        image.Mutate(ctx => ctx.Crop(segment.BoundingBox));

        var tempPath = Path.Combine(Path.GetTempPath(), $"segment_{segment.Id}.png");
        await image.SaveAsPngAsync(tempPath, ct);

        return tempPath;
    }

    private async Task<string> ConvertToGrayscaleAsync(string imagePath, CancellationToken ct)
    {
        using var image = await Image.LoadAsync<Rgb24>(imagePath, ct);

        image.Mutate(ctx => ctx.Grayscale());

        var grayPath = Path.ChangeExtension(imagePath, ".gray.png");
        await image.SaveAsPngAsync(grayPath, ct);

        // Delete original color version
        try { File.Delete(imagePath); } catch { }

        return grayPath;
    }

    private async Task<SegmentResult> ProcessTextBlockAsync(
        string segmentPath,
        DocumentSegment segment,
        CancellationToken ct)
    {
        if (_ocrEngine == null)
        {
            return new SegmentResult
            {
                Id = segment.Id,
                Type = SegmentType.TextBlock,
                Content = "[OCR engine not available]",
                Confidence = 0
            };
        }

        // Extract text using OCR (synchronous, run in task)
        var regions = await Task.Run(() => _ocrEngine.ExtractTextWithCoordinates(segmentPath), ct);
        var text = string.Join(" ", regions.Select(r => r.Text));

        return new SegmentResult
        {
            Id = segment.Id,
            Type = SegmentType.TextBlock,
            Content = text,
            Confidence = regions.Any() ? regions.Average(r => r.Confidence) : 0,
            ZOrder = segment.ZOrder,
            ImagePath = segmentPath,
            Method = "tesseract"
        };
    }

    private async Task<SegmentResult> ProcessImageAsync(
        string segmentPath,
        DocumentSegment segment,
        CancellationToken ct)
    {
        // Try Florence-2 first (fast)
        if (_florence2Service != null)
        {
            var florenceResult = await _florence2Service.GetCaptionAsync(segmentPath, detailed: true, enhanceWithColors: true, ct);

            if (florenceResult.Success && !string.IsNullOrWhiteSpace(florenceResult.Caption))
            {
                return new SegmentResult
                {
                    Id = segment.Id,
                    Type = SegmentType.Image,
                    Content = florenceResult.Caption!,
                    Confidence = 0.85, // Florence-2 base confidence
                    ZOrder = segment.ZOrder,
                    ImagePath = segmentPath,
                    Method = "florence2"
                };
            }
        }

        // Escalate to Vision LLM if available
        if (_visionLlmService != null && _config.ComplexMode.Images.PreferVisionLlm)
        {
            var result = await _visionLlmService.AnalyzeImageAsync(
                segmentPath,
                "Describe this image in detail.",
                null, // modelOverride
                ct);
            var caption = result.Caption ?? "[No caption generated]";

            return new SegmentResult
            {
                Id = segment.Id,
                Type = SegmentType.Image,
                Content = caption,
                Confidence = 0.95,
                ZOrder = segment.ZOrder,
                ImagePath = segmentPath,
                Method = "vision_llm"
            };
        }

        return new SegmentResult
        {
            Id = segment.Id,
            Type = SegmentType.Image,
            Content = "[Image description not available]",
            Confidence = 0,
            ZOrder = segment.ZOrder,
            ImagePath = segmentPath
        };
    }

    private async Task<SegmentResult> ProcessChartAsync(
        string segmentPath,
        DocumentSegment segment,
        CancellationToken ct)
    {
        // Charts always use Vision LLM for best accuracy
        if (_visionLlmService != null)
        {
            var result = await _visionLlmService.AnalyzeImageAsync(
                segmentPath,
                "Describe this chart/diagram in detail. Include all visible text, data points, axis labels, and key information.",
                null, // modelOverride
                ct);
            var caption = result.Caption ?? "[No chart description generated]";

            return new SegmentResult
            {
                Id = segment.Id,
                Type = SegmentType.Chart,
                Content = caption,
                Confidence = 0.95,
                ZOrder = segment.ZOrder,
                ImagePath = segmentPath,
                Method = "vision_llm"
            };
        }

        return new SegmentResult
        {
            Id = segment.Id,
            Type = SegmentType.Chart,
            Content = "[Chart description not available]",
            Confidence = 0,
            ZOrder = segment.ZOrder,
            ImagePath = segmentPath
        };
    }

    private async Task<SegmentResult> ProcessTableAsync(
        string segmentPath,
        DocumentSegment segment,
        CancellationToken ct)
    {
        // Use OCR for table text extraction
        if (_ocrEngine != null)
        {
            var regions = await Task.Run(() => _ocrEngine.ExtractTextWithCoordinates(segmentPath), ct);
            var text = string.Join("\n", regions.Select(r => r.Text));

            return new SegmentResult
            {
                Id = segment.Id,
                Type = SegmentType.Table,
                Content = text,
                Confidence = regions.Any() ? regions.Average(r => r.Confidence) : 0,
                ZOrder = segment.ZOrder,
                ImagePath = segmentPath,
                Method = "tesseract"
            };
        }

        return new SegmentResult
        {
            Id = segment.Id,
            Type = SegmentType.Table,
            Content = "[Table extraction not available]",
            Confidence = 0,
            ZOrder = segment.ZOrder,
            ImagePath = segmentPath
        };
    }

    private async Task<SegmentResult> ProcessCaptionAsync(
        string segmentPath,
        DocumentSegment segment,
        CancellationToken ct)
    {
        if (_ocrEngine != null)
        {
            var regions = await Task.Run(() => _ocrEngine.ExtractTextWithCoordinates(segmentPath), ct);
            var text = string.Join(" ", regions.Select(r => r.Text));

            return new SegmentResult
            {
                Id = segment.Id,
                Type = SegmentType.Caption,
                Content = text,
                Confidence = regions.Any() ? regions.Average(r => r.Confidence) : 0,
                ZOrder = segment.ZOrder,
                RelatedTo = segment.RelatedTo,
                ImagePath = segmentPath,
                Method = "tesseract"
            };
        }

        return new SegmentResult
        {
            Id = segment.Id,
            Type = SegmentType.Caption,
            Content = "[Caption extraction not available]",
            Confidence = 0,
            ZOrder = segment.ZOrder,
            RelatedTo = segment.RelatedTo,
            ImagePath = segmentPath
        };
    }

    private IEnumerable<Signal> EmitSegmentSignals(List<SegmentResult> results)
    {
        foreach (var result in results)
        {
            yield return new Signal
            {
                Key = $"complex.segment.{result.ZOrder}.type",
                Value = result.Type.ToString(),
                Confidence = result.Confidence,
                Source = Name,
                Tags = new List<string> { "complex", "segment" }
            };

            yield return new Signal
            {
                Key = $"complex.segment.{result.ZOrder}.content",
                Value = result.Content,
                Confidence = result.Confidence,
                Source = Name,
                Tags = new List<string> { "complex", "segment", "content" }
            };

            if (result.Method != null)
            {
                yield return new Signal
                {
                    Key = $"complex.segment.{result.ZOrder}.method",
                    Value = result.Method,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "complex", "segment" }
                };
            }
        }
    }

    private string ReconstructDocument(List<SegmentResult> results, DocumentLayout layout)
    {
        var document = new StringBuilder();
        var ordered = results.OrderBy(r => r.ZOrder).ToList();

        foreach (var result in ordered)
        {
            switch (result.Type)
            {
                case SegmentType.TextBlock:
                    document.AppendLine(result.Content);
                    document.AppendLine();
                    break;

                case SegmentType.Image:
                case SegmentType.Chart:
                    document.AppendLine($"![{result.Type}]({result.ImagePath})");

                    // Find associated caption
                    var caption = ordered.FirstOrDefault(
                        r => r.Type == SegmentType.Caption && r.RelatedTo == result.Id
                    );

                    if (caption != null)
                    {
                        document.AppendLine($"*{caption.Content}*");
                    }

                    document.AppendLine($"**Description**: {result.Content}");
                    document.AppendLine();
                    break;

                case SegmentType.Table:
                    document.AppendLine("**Table:**");
                    document.AppendLine(result.Content);
                    document.AppendLine();
                    break;
            }
        }

        return document.ToString();
    }

    private double CalculateAverageConfidence(List<SegmentResult> results)
    {
        return results.Any() ? results.Average(r => r.Confidence) : 0;
    }
}
