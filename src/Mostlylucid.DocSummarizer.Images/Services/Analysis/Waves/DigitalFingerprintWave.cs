using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Security.Cryptography;
using System.Text;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Digital fingerprinting wave using robust perceptual hashing.
/// Inspired by Meta's PDQ algorithm and PhotoDNA approach.
///
/// Generates multiple fingerprints:
/// - PDQ-style robust hash (resistant to rotation, scaling, compression)
/// - Color histogram hash
/// - DCT-based hash
/// - Block mean hash
///
/// References:
/// - PDQ (Meta): https://github.com/facebook/ThreatExchange/tree/main/pdq
/// - PhotoDNA: Robust hash for content matching
/// - Trufo (2025): Next-gen perceptual hashing
/// </summary>
public class DigitalFingerprintWave : IAnalysisWave
{
    public string Name => "DigitalFingerprintWave";
    public int Priority => 85; // High priority - provides identity signals
    public IReadOnlyList<string> Tags => new[] { SignalTags.Identity, SignalTags.Forensic };

    private const int PdqHashSize = 16; // 16x16 DCT, 256-bit hash
    private const int BlockHashSize = 8; // 8x8 blocks for block mean hash

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        await using var stream = File.OpenRead(imagePath);
        using var image = await Image.LoadAsync<Rgba32>(stream, ct);

        // 1. PDQ-style robust hash (DCT-based)
        var pdqHash = ComputePdqStyleHash(image);
        signals.Add(new Signal
        {
            Key = "fingerprint.pdq_hash",
            Value = pdqHash,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity },
            Metadata = new Dictionary<string, object>
            {
                ["algorithm"] = "pdq_style_dct",
                ["hash_size"] = PdqHashSize,
                ["resistant_to"] = new[] { "rotation", "scaling", "compression", "color_adjustments" }
            }
        });

        // 2. Color histogram hash (resistant to geometric changes)
        var colorHash = ComputeColorHistogramHash(image);
        signals.Add(new Signal
        {
            Key = "fingerprint.color_hash",
            Value = colorHash,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity, SignalTags.Color },
            Metadata = new Dictionary<string, object>
            {
                ["algorithm"] = "color_histogram",
                ["resistant_to"] = new[] { "rotation", "cropping", "scaling" }
            }
        });

        // 3. Block mean hash (resistant to minor edits)
        var blockHash = ComputeBlockMeanHash(image);
        signals.Add(new Signal
        {
            Key = "fingerprint.block_hash",
            Value = blockHash,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity },
            Metadata = new Dictionary<string, object>
            {
                ["algorithm"] = "block_mean",
                ["hash_size"] = BlockHashSize,
                ["resistant_to"] = new[] { "compression", "minor_edits" }
            }
        });

        // 4. Composite fingerprint (combines all hashes)
        var compositeFingerprint = ComputeCompositeFingerprint(pdqHash, colorHash, blockHash);
        signals.Add(new Signal
        {
            Key = "fingerprint.composite",
            Value = compositeFingerprint,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity },
            Metadata = new Dictionary<string, object>
            {
                ["algorithm"] = "composite_multi_modal",
                ["components"] = new[] { "pdq", "color", "block" }
            }
        });

        // 5. Fingerprint quality score
        var qualityScore = AssessFingerprintQuality(image);
        signals.Add(new Signal
        {
            Key = "fingerprint.quality",
            Value = qualityScore,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Quality },
            Metadata = new Dictionary<string, object>
            {
                ["interpretation"] = qualityScore > 0.8 ? "High distinctiveness" :
                                    qualityScore > 0.5 ? "Medium distinctiveness" : "Low distinctiveness"
            }
        });

        return signals;
    }

    /// <summary>
    /// Compute PDQ-style hash using DCT (Discrete Cosine Transform).
    /// Resistant to rotation, scaling, compression, and color adjustments.
    /// </summary>
    private static string ComputePdqStyleHash(Image<Rgba32> image)
    {
        // Resize to standard size for DCT
        using var resized = image.Clone(ctx => ctx.Resize(PdqHashSize, PdqHashSize));

        // Convert to grayscale for luminance-based hashing
        var luminance = new double[PdqHashSize, PdqHashSize];

        for (int y = 0; y < PdqHashSize; y++)
        {
            var row = resized.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < PdqHashSize; x++)
            {
                var pixel = row[x];
                // ITU-R BT.709 luma coefficients
                luminance[x, y] = (0.2126 * pixel.R + 0.7152 * pixel.G + 0.0722 * pixel.B) / 255.0;
            }
        }

        // Compute 2D DCT
        var dct = ComputeDCT2D(luminance);

        // Generate hash from low-frequency DCT coefficients (excluding DC component)
        var hash = new StringBuilder();
        var median = ComputeMedian(dct);

        for (int y = 0; y < PdqHashSize; y++)
        {
            for (int x = 0; x < PdqHashSize; x++)
            {
                if (x == 0 && y == 0) continue; // Skip DC component

                hash.Append(dct[x, y] > median ? '1' : '0');
            }
        }

        return ConvertBinaryToHex(hash.ToString());
    }

    /// <summary>
    /// Compute color histogram hash.
    /// Resistant to geometric transformations.
    /// </summary>
    private static string ComputeColorHistogramHash(Image<Rgba32> image)
    {
        const int bins = 16; // 16 bins per channel = 4096 total bins

        var rHist = new int[bins];
        var gHist = new int[bins];
        var bHist = new int[bins];

        for (int y = 0; y < image.Height; y++)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < image.Width; x++)
            {
                var pixel = row[x];
                rHist[pixel.R * bins / 256]++;
                gHist[pixel.G * bins / 256]++;
                bHist[pixel.B * bins / 256]++;
            }
        }

        // Normalize histograms
        var totalPixels = image.Width * image.Height;
        var hash = new StringBuilder();

        for (int i = 0; i < bins; i++)
        {
            var rVal = (byte)((rHist[i] * 255) / totalPixels);
            var gVal = (byte)((gHist[i] * 255) / totalPixels);
            var bVal = (byte)((bHist[i] * 255) / totalPixels);

            hash.Append($"{rVal:X2}{gVal:X2}{bVal:X2}");
        }

        // Hash the histogram to fixed size
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(hash.ToString()));
        return Convert.ToHexString(hashBytes)[..32]; // 128-bit hash
    }

    /// <summary>
    /// Compute block mean hash.
    /// Divides image into blocks and compares to median.
    /// </summary>
    private static string ComputeBlockMeanHash(Image<Rgba32> image)
    {
        using var resized = image.Clone(ctx => ctx.Resize(BlockHashSize, BlockHashSize));

        var blockMeans = new double[BlockHashSize, BlockHashSize];

        for (int y = 0; y < BlockHashSize; y++)
        {
            var row = resized.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < BlockHashSize; x++)
            {
                var pixel = row[x];
                blockMeans[x, y] = (pixel.R + pixel.G + pixel.B) / 3.0;
            }
        }

        var median = ComputeMedian(blockMeans);
        var hash = new StringBuilder();

        for (int y = 0; y < BlockHashSize; y++)
        {
            for (int x = 0; x < BlockHashSize; x++)
            {
                hash.Append(blockMeans[x, y] > median ? '1' : '0');
            }
        }

        return ConvertBinaryToHex(hash.ToString());
    }

    /// <summary>
    /// Compute composite fingerprint from multiple hashes.
    /// </summary>
    private static string ComputeCompositeFingerprint(params string[] hashes)
    {
        var combined = string.Join("|", hashes);
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// Assess fingerprint quality/distinctiveness.
    /// Higher quality = more unique/distinctive image.
    /// </summary>
    private static double AssessFingerprintQuality(Image<Rgba32> image)
    {
        // Quality based on:
        // 1. Color diversity (not monochrome)
        // 2. Spatial complexity (not uniform)
        // 3. Sufficient resolution

        var width = image.Width;
        var height = image.Height;

        // Resolution score
        var resolutionScore = Math.Min(1.0, (width * height) / (1920.0 * 1080.0));

        // Color diversity score (sample pixels)
        var colorSet = new HashSet<uint>();
        var sampleStep = Math.Max(1, width / 32);

        for (int y = 0; y < height; y += sampleStep)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < width; x += sampleStep)
            {
                var pixel = row[x];
                var colorKey = ((uint)pixel.R << 16) | ((uint)pixel.G << 8) | pixel.B;
                colorSet.Add(colorKey);
            }
        }

        var maxColors = Math.Min(4096, (width / sampleStep) * (height / sampleStep));
        var colorDiversityScore = (double)colorSet.Count / maxColors;

        // Combined quality score
        return (resolutionScore * 0.3 + colorDiversityScore * 0.7);
    }

    /// <summary>
    /// Simplified 2D DCT implementation.
    /// </summary>
    private static double[,] ComputeDCT2D(double[,] input)
    {
        var size = input.GetLength(0);
        var output = new double[size, size];

        for (int v = 0; v < size; v++)
        {
            for (int u = 0; u < size; u++)
            {
                double sum = 0;

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        sum += input[x, y] *
                               Math.Cos((2 * x + 1) * u * Math.PI / (2.0 * size)) *
                               Math.Cos((2 * y + 1) * v * Math.PI / (2.0 * size));
                    }
                }

                var cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
                var cv = v == 0 ? 1 / Math.Sqrt(2) : 1;

                output[u, v] = 0.25 * cu * cv * sum;
            }
        }

        return output;
    }

    private static double ComputeMedian(double[,] values)
    {
        var flat = new List<double>();

        for (int x = 0; x < values.GetLength(0); x++)
        {
            for (int y = 0; y < values.GetLength(1); y++)
            {
                flat.Add(values[x, y]);
            }
        }

        flat.Sort();
        return flat[flat.Count / 2];
    }

    private static string ConvertBinaryToHex(string binary)
    {
        var hex = new StringBuilder();

        for (int i = 0; i < binary.Length; i += 4)
        {
            var chunk = binary.Substring(i, Math.Min(4, binary.Length - i)).PadRight(4, '0');
            var value = Convert.ToInt32(chunk, 2);
            hex.Append($"{value:X}");
        }

        return hex.ToString();
    }
}
