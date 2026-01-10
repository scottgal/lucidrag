using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Error Level Analysis (ELA) wave for detecting image manipulation.
/// ELA works by resaving the image at a known quality level and computing
/// the difference. Manipulated regions show different error levels.
/// </summary>
public class ErrorLevelAnalysisWave : IAnalysisWave
{
    public string Name => "ErrorLevelAnalysisWave";
    public int Priority => 70; // Medium-high priority
    public IReadOnlyList<string> Tags => new[] { SignalTags.Forensic, SignalTags.Quality };

    private const int ElaQuality = 95; // JPEG quality for ELA resave
    private const int GridSize = 16; // Divide image into grid for regional analysis

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // Only perform ELA on JPEG images
        var format = context.GetValue<string>("identity.format");
        if (format?.ToUpperInvariant() != "JPEG" && format?.ToUpperInvariant() != "JPG")
        {
            signals.Add(new Signal
            {
                Key = "forensics.ela_applicable",
                Value = false,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { SignalTags.Forensic },
                Metadata = new Dictionary<string, object>
                {
                    ["reason"] = "ELA only works on JPEG images"
                }
            });
            return signals;
        }

        try
        {
            await using var stream = File.OpenRead(imagePath);
            using var originalImage = await Image.LoadAsync<Rgba32>(stream, ct);

            // Resave image at known quality
            using var resavedImage = await ResaveAtQualityAsync(originalImage, ElaQuality);

            // Compute error level (pixel-wise difference)
            var errorLevels = ComputeErrorLevels(originalImage, resavedImage);

            // Analyze error level distribution
            var stats = AnalyzeErrorLevelDistribution(errorLevels);

            signals.Add(new Signal
            {
                Key = "forensics.ela_mean_error",
                Value = stats.Mean,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { SignalTags.Forensic },
                Metadata = new Dictionary<string, object>
                {
                    ["stddev"] = stats.StdDev,
                    ["max"] = stats.Max
                }
            });

            // High standard deviation suggests tampering
            var tamperingConfidence = CalculateTamperingConfidence(stats);

            if (tamperingConfidence > 0.5)
            {
                signals.Add(new Signal
                {
                    Key = "forensics.ela_tampering_detected",
                    Value = true,
                    Confidence = tamperingConfidence,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Forensic },
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "High variance in error levels across image",
                        ["mean_error"] = stats.Mean,
                        ["stddev"] = stats.StdDev
                    }
                });
            }

            // Regional analysis - detect localized tampering
            var regionalAnalysis = AnalyzeRegionalErrorLevels(errorLevels, originalImage.Width, originalImage.Height);

            if (regionalAnalysis.SuspiciousRegions.Any())
            {
                signals.Add(new Signal
                {
                    Key = "forensics.ela_suspicious_regions",
                    Value = regionalAnalysis.SuspiciousRegions,
                    Confidence = regionalAnalysis.Confidence,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Forensic },
                    Metadata = new Dictionary<string, object>
                    {
                        ["region_count"] = regionalAnalysis.SuspiciousRegions.Count,
                        ["detection_method"] = "regional_error_level_analysis"
                    }
                });
            }

            signals.Add(new Signal
            {
                Key = "forensics.ela_uniformity",
                Value = stats.Uniformity,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { SignalTags.Forensic },
                Metadata = new Dictionary<string, object>
                {
                    ["interpretation"] = stats.Uniformity > 0.8 ? "Consistent compression" : "Inconsistent compression (possible tampering)"
                }
            });
        }
        catch (Exception ex)
        {
            signals.Add(new Signal
            {
                Key = "forensics.ela_error",
                Value = ex.Message,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "error" }
            });
        }

        return signals;
    }

    private static async Task<Image<Rgba32>> ResaveAtQualityAsync(Image<Rgba32> image, int quality)
    {
        using var ms = new MemoryStream();

        var encoder = new JpegEncoder { Quality = quality };
        await image.SaveAsync(ms, encoder);

        ms.Position = 0;
        return await Image.LoadAsync<Rgba32>(ms);
    }

    private static double[,] ComputeErrorLevels(Image<Rgba32> original, Image<Rgba32> resaved)
    {
        var width = Math.Min(original.Width, resaved.Width);
        var height = Math.Min(original.Height, resaved.Height);

        var errorLevels = new double[width, height];

        for (int y = 0; y < height; y++)
        {
            var origRow = original.DangerousGetPixelRowMemory(y).Span;
            var resavedRow = resaved.DangerousGetPixelRowMemory(y).Span;

            for (int x = 0; x < width; x++)
            {
                var origPixel = origRow[x];
                var resavedPixel = resavedRow[x];

                // Calculate absolute difference
                var rDiff = Math.Abs(origPixel.R - resavedPixel.R);
                var gDiff = Math.Abs(origPixel.G - resavedPixel.G);
                var bDiff = Math.Abs(origPixel.B - resavedPixel.B);

                // Average difference across channels, scaled to 0-1
                errorLevels[x, y] = (rDiff + gDiff + bDiff) / (3.0 * 255.0);
            }
        }

        return errorLevels;
    }

    private static ErrorLevelStats AnalyzeErrorLevelDistribution(double[,] errorLevels)
    {
        var width = errorLevels.GetLength(0);
        var height = errorLevels.GetLength(1);
        var totalPixels = width * height;

        var values = new List<double>(totalPixels);

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                values.Add(errorLevels[x, y]);
            }
        }

        var mean = values.Average();
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / totalPixels;
        var stdDev = Math.Sqrt(variance);
        var max = values.Max();

        // Uniformity: 1.0 = perfectly uniform, 0.0 = highly variable
        var uniformity = 1.0 - Math.Min(1.0, stdDev * 10); // Scale stddev

        return new ErrorLevelStats
        {
            Mean = mean,
            StdDev = stdDev,
            Max = max,
            Uniformity = Math.Max(0, uniformity)
        };
    }

    private static double CalculateTamperingConfidence(ErrorLevelStats stats)
    {
        // High stddev relative to mean suggests tampering
        var coefficientOfVariation = stats.Mean > 0 ? stats.StdDev / stats.Mean : 0;

        // Normalize to 0-1 confidence scale
        var confidence = Math.Min(1.0, coefficientOfVariation / 2.0);

        return confidence;
    }

    private static RegionalAnalysisResult AnalyzeRegionalErrorLevels(
        double[,] errorLevels,
        int imageWidth,
        int imageHeight)
    {
        var cellWidth = imageWidth / GridSize;
        var cellHeight = imageHeight / GridSize;

        var cellAverages = new List<(int X, int Y, double AvgError)>();

        // Calculate average error level for each cell
        for (int gridY = 0; gridY < GridSize; gridY++)
        {
            for (int gridX = 0; gridX < GridSize; gridX++)
            {
                var startX = gridX * cellWidth;
                var startY = gridY * cellHeight;
                var endX = Math.Min(startX + cellWidth, imageWidth);
                var endY = Math.Min(startY + cellHeight, imageHeight);

                double sum = 0;
                int count = 0;

                for (int x = startX; x < endX; x++)
                {
                    for (int y = startY; y < endY; y++)
                    {
                        if (x < errorLevels.GetLength(0) && y < errorLevels.GetLength(1))
                        {
                            sum += errorLevels[x, y];
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    cellAverages.Add((gridX, gridY, sum / count));
                }
            }
        }

        // Find cells with significantly higher error levels
        var globalAvg = cellAverages.Average(c => c.AvgError);
        var globalStdDev = Math.Sqrt(cellAverages.Sum(c => Math.Pow(c.AvgError - globalAvg, 2)) / cellAverages.Count);

        var threshold = globalAvg + (2 * globalStdDev); // 2 standard deviations above mean

        var suspiciousRegions = cellAverages
            .Where(c => c.AvgError > threshold)
            .Select(c => new SuspiciousRegion
            {
                X = c.X * cellWidth,
                Y = c.Y * cellHeight,
                Width = cellWidth,
                Height = cellHeight,
                ErrorLevel = c.AvgError,
                DeviationFromMean = (c.AvgError - globalAvg) / globalStdDev
            })
            .ToList();

        var confidence = suspiciousRegions.Any()
            ? Math.Min(1.0, suspiciousRegions.Average(r => r.DeviationFromMean) / 3.0)
            : 0.0;

        return new RegionalAnalysisResult
        {
            SuspiciousRegions = suspiciousRegions,
            Confidence = confidence
        };
    }

    private record ErrorLevelStats
    {
        public double Mean { get; init; }
        public double StdDev { get; init; }
        public double Max { get; init; }
        public double Uniformity { get; init; }
    }

    private record RegionalAnalysisResult
    {
        public List<SuspiciousRegion> SuspiciousRegions { get; init; } = new();
        public double Confidence { get; init; }
    }

    private record SuspiciousRegion
    {
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double ErrorLevel { get; init; }
        public double DeviationFromMean { get; init; }
    }
}
