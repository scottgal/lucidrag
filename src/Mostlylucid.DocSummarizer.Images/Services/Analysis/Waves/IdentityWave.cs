using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using SixLabors.ImageSharp;
using System.Security.Cryptography;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Basic image identity wave - extracts format, dimensions, file size, etc.
/// Highest priority wave that provides fundamental image properties.
/// Uses streaming to avoid loading full image into memory where possible.
/// </summary>
public class IdentityWave : IAnalysisWave
{
    public string Name => "IdentityWave";
    public int Priority => 110; // Highest priority - others may depend on this
    public IReadOnlyList<string> Tags => new[] { SignalTags.Identity };

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        // File info (no I/O yet)
        var fileInfo = new FileInfo(imagePath);

        signals.Add(new Signal
        {
            Key = "identity.file_size",
            Value = fileInfo.Length,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity }
        });

        // Use Image.Identify for fast metadata extraction without decoding pixels
        await using var stream = File.OpenRead(imagePath);
        var imageInfo = await Image.IdentifyAsync(stream, ct);

        if (imageInfo == null)
        {
            throw new InvalidOperationException($"Could not identify image: {imagePath}");
        }

        // Format
        var format = imageInfo.Metadata.DecodedImageFormat?.Name ?? "Unknown";
        signals.Add(new Signal
        {
            Key = "identity.format",
            Value = format,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity }
        });

        // Dimensions (from metadata, no pixel access needed)
        signals.Add(new Signal
        {
            Key = "identity.width",
            Value = imageInfo.Width,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity }
        });

        signals.Add(new Signal
        {
            Key = "identity.height",
            Value = imageInfo.Height,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity }
        });

        // Aspect ratio
        var aspectRatio = imageInfo.Width / (double)imageInfo.Height;
        signals.Add(new Signal
        {
            Key = "identity.aspect_ratio",
            Value = aspectRatio,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity }
        });

        // Frame count (metadata access, no frame decoding)
        var frameCount = imageInfo.FrameMetadataCollection?.Count ?? 1;
        var isAnimated = frameCount > 1;

        signals.Add(new Signal
        {
            Key = "identity.frame_count",
            Value = frameCount,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity },
            Metadata = new Dictionary<string, object>
            {
                ["is_animated"] = isAnimated
            }
        });

        signals.Add(new Signal
        {
            Key = "identity.is_animated",
            Value = isAnimated,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity }
        });

        // Pixel count (calculated from metadata)
        var pixelCount = imageInfo.Width * imageInfo.Height;
        signals.Add(new Signal
        {
            Key = "identity.pixel_count",
            Value = pixelCount,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity },
            Metadata = new Dictionary<string, object>
            {
                ["megapixels"] = pixelCount / 1_000_000.0
            }
        });

        // SHA256 hash (stream-based, memory efficient)
        stream.Position = 0;
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream, ct);
        var hash = Convert.ToHexString(hashBytes);

        signals.Add(new Signal
        {
            Key = "identity.sha256",
            Value = hash,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Identity, SignalTags.Forensic }
        });

        // Store image info in context (metadata only, no pixels)
        context.SetCached("image_info", imageInfo);
        context.SetCached("image_path", imagePath);

        return signals;
    }
}
