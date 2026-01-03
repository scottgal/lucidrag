using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Mostlylucid.DocSummarizer.Images.Tests;

public class ImageAnalyzerTests : IDisposable
{
    private readonly ImageAnalyzer _analyzer;
    private readonly string _tempDir;

    public ImageAnalyzerTests()
    {
        var config = Options.Create(new ImageConfig());
        _analyzer = new ImageAnalyzer(config);
        _tempDir = Path.Combine(Path.GetTempPath(), "ImageAnalyzerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    #region AnalyzeAsync Tests

    [Fact]
    public async Task AnalyzeAsync_ReturnsValidProfile()
    {
        using var image = TestImageGenerator.CreateColorBlocks(300, 200);
        var path = SaveTempImage(image, "test.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.NotNull(profile);
        Assert.Equal(300, profile.Width);
        Assert.Equal(200, profile.Height);
        Assert.Equal("PNG", profile.Format);
    }

    [Fact]
    public async Task AnalyzeAsync_CalculatesAspectRatio()
    {
        using var image = TestImageGenerator.CreateSolidColor(400, 200, Color.Blue);
        var path = SaveTempImage(image, "wide.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.Equal(2.0, profile.AspectRatio, 2);
    }

    [Fact]
    public async Task AnalyzeAsync_CalculatesSha256()
    {
        using var image = TestImageGenerator.CreateSolidColor(100, 100, Color.Red);
        var path = SaveTempImage(image, "hash.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.NotNull(profile.Sha256);
        Assert.Equal(64, profile.Sha256.Length); // SHA256 = 64 hex chars
        Assert.True(profile.Sha256.All(c => char.IsAsciiHexDigitLower(c) || char.IsDigit(c)));
    }

    [Fact]
    public async Task AnalyzeAsync_SameImage_SameHash()
    {
        using var image = TestImageGenerator.CreateSolidColor(100, 100, Color.Green);
        var path1 = SaveTempImage(image, "hash1.png");
        var path2 = SaveTempImage(image, "hash2.png");

        var profile1 = await _analyzer.AnalyzeAsync(path1);
        var profile2 = await _analyzer.AnalyzeAsync(path2);

        Assert.Equal(profile1.Sha256, profile2.Sha256);
    }

    [Fact]
    public async Task AnalyzeAsync_ReturnsDominantColors()
    {
        using var image = TestImageGenerator.CreateSolidColor(200, 200, Color.Purple);
        var path = SaveTempImage(image, "purple.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.NotEmpty(profile.DominantColors);
        Assert.True(profile.DominantColors[0].Percentage > 90);
    }

    [Fact]
    public async Task AnalyzeAsync_ReturnsColorGrid()
    {
        using var image = TestImageGenerator.CreateColorBlocks(300, 300);
        var path = SaveTempImage(image, "blocks.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.NotNull(profile.ColorGrid);
        Assert.True(profile.ColorGrid.Cells.Count > 0);
    }

    [Fact]
    public async Task AnalyzeAsync_CalculatesEdgeDensity()
    {
        using var image = TestImageGenerator.CreateCheckerboard(200, 200, 10);
        var path = SaveTempImage(image, "checker.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.True(profile.EdgeDensity > 0.2, $"Expected high edge density, got {profile.EdgeDensity}");
    }

    [Fact]
    public async Task AnalyzeAsync_CalculatesLaplacianVariance()
    {
        using var image = TestImageGenerator.CreateSharpImage(300, 300);
        var path = SaveTempImage(image, "sharp.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.True(profile.LaplacianVariance > 100);
    }

    [Fact]
    public async Task AnalyzeAsync_CalculatesTextLikeliness()
    {
        using var image = TestImageGenerator.CreateTextLikeImage(400, 300);
        var path = SaveTempImage(image, "text.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.InRange(profile.TextLikeliness, 0.0, 1.0);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsGrayscale()
    {
        using var image = TestImageGenerator.CreateGrayscaleImage(200, 200);
        var path = SaveTempImage(image, "gray.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.True(profile.IsMostlyGrayscale);
        Assert.True(profile.MeanSaturation < 0.15);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsColorfulImage()
    {
        using var image = TestImageGenerator.CreateColorBlocks(200, 200);
        var path = SaveTempImage(image, "colorful.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.False(profile.IsMostlyGrayscale);
        Assert.True(profile.MeanSaturation > 0.3);
    }

    #endregion

    #region Image Type Detection Tests

    [Fact]
    public async Task AnalyzeAsync_Screenshot_DetectsScreenshotType()
    {
        using var image = TestImageGenerator.CreateScreenshotLike(800, 600);
        var path = SaveTempImage(image, "screenshot.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        // Screenshots have high straight edge ratio and may contain text
        Assert.True(profile.DetectedType == ImageType.Screenshot ||
                    profile.DetectedType == ImageType.Diagram ||
                    profile.DetectedType == ImageType.ScannedDocument,
            $"Expected Screenshot/Diagram/ScannedDocument, got {profile.DetectedType}");
    }

    [Fact]
    public async Task AnalyzeAsync_Diagram_DetectsDiagramType()
    {
        using var image = TestImageGenerator.CreateDiagramLike(400, 300);
        var path = SaveTempImage(image, "diagram.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.True(profile.DetectedType == ImageType.Diagram ||
                    profile.DetectedType == ImageType.Screenshot ||
                    profile.DetectedType == ImageType.Chart,
            $"Expected diagram-like type, got {profile.DetectedType}");
    }

    [Fact]
    public async Task AnalyzeAsync_Icon_DetectsIconType()
    {
        using var image = TestImageGenerator.CreateIconLike(64);
        var path = SaveTempImage(image, "icon.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        // Small images with clear edges are icons
        Assert.True(profile.DetectedType == ImageType.Icon ||
                    profile.DetectedType == ImageType.Diagram,
            $"Expected Icon or Diagram, got {profile.DetectedType}");
    }

    [Fact]
    public async Task AnalyzeAsync_TypeConfidence_IsValid()
    {
        using var image = TestImageGenerator.CreateScreenshotLike(400, 300);
        var path = SaveTempImage(image, "conf.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.InRange(profile.TypeConfidence, 0.0, 1.0);
    }

    #endregion

    #region Perceptual Hash Tests

    [Fact]
    public async Task AnalyzeAsync_CalculatesPerceptualHash()
    {
        using var image = TestImageGenerator.CreateColorBlocks(200, 200);
        var path = SaveTempImage(image, "phash.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.NotNull(profile.PerceptualHash);
        Assert.Equal(16, profile.PerceptualHash.Length); // 64-bit hash as hex
    }

    [Fact]
    public async Task AnalyzeAsync_SimilarImages_SimilarHashes()
    {
        using var image1 = TestImageGenerator.CreateSolidColor(200, 200, Color.Blue);
        using var image2 = TestImageGenerator.CreateSolidColor(200, 200, Color.DarkBlue);

        var path1 = SaveTempImage(image1, "blue1.png");
        var path2 = SaveTempImage(image2, "blue2.png");

        var profile1 = await _analyzer.AnalyzeAsync(path1);
        var profile2 = await _analyzer.AnalyzeAsync(path2);

        // Similar solid colors should have similar perceptual hashes
        var hash1 = Convert.ToUInt64(profile1.PerceptualHash, 16);
        var hash2 = Convert.ToUInt64(profile2.PerceptualHash, 16);

        // Hamming distance should be low for similar images
        var hammingDistance = HammingDistance(hash1, hash2);
        Assert.True(hammingDistance < 20, $"Expected similar hashes, got Hamming distance {hammingDistance}");
    }

    [Fact]
    public async Task AnalyzeAsync_DifferentImages_DifferentHashes()
    {
        using var image1 = TestImageGenerator.CreateSolidColor(200, 200, Color.Red);
        using var image2 = TestImageGenerator.CreateCheckerboard(200, 200, 10);

        var path1 = SaveTempImage(image1, "red.png");
        var path2 = SaveTempImage(image2, "checker.png");

        var profile1 = await _analyzer.AnalyzeAsync(path1);
        var profile2 = await _analyzer.AnalyzeAsync(path2);

        Assert.NotEqual(profile1.PerceptualHash, profile2.PerceptualHash);
    }

    #endregion

    #region GeneratePerceptualHashAsync Tests

    [Fact]
    public async Task GeneratePerceptualHashAsync_ReturnsHash()
    {
        using var image = TestImageGenerator.CreateColorBlocks(200, 200);
        var path = SaveTempImage(image, "hash.png");

        var hash = await _analyzer.GeneratePerceptualHashAsync(path);

        Assert.NotNull(hash);
        Assert.Equal(16, hash.Length);
    }

    #endregion

    #region GenerateThumbnailAsync Tests

    [Fact]
    public async Task GenerateThumbnailAsync_CreatesThumbnail()
    {
        using var image = TestImageGenerator.CreateColorBlocks(800, 600);
        var path = SaveTempImage(image, "large.png");

        var thumbnailBytes = await _analyzer.GenerateThumbnailAsync(path, maxSize: 100);

        Assert.NotEmpty(thumbnailBytes);

        // Verify it's a valid image
        using var thumbnail = Image.Load(thumbnailBytes);
        Assert.True(thumbnail.Width <= 100);
        Assert.True(thumbnail.Height <= 100);
    }

    [Fact]
    public async Task GenerateThumbnailAsync_MaintainsAspectRatio()
    {
        using var image = TestImageGenerator.CreateSolidColor(800, 400, Color.Blue); // 2:1 aspect ratio
        var path = SaveTempImage(image, "wide.png");

        var thumbnailBytes = await _analyzer.GenerateThumbnailAsync(path, maxSize: 200);

        using var thumbnail = Image.Load(thumbnailBytes);
        var aspectRatio = thumbnail.Width / (double)thumbnail.Height;
        Assert.Equal(2.0, aspectRatio, 1);
    }

    #endregion

    #region Brightness/Contrast Tests

    [Fact]
    public async Task AnalyzeAsync_DarkImage_LowMeanLuminance()
    {
        using var image = TestImageGenerator.CreateSolidColor(200, 200, Color.Black);
        var path = SaveTempImage(image, "dark.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.True(profile.MeanLuminance < 30, $"Expected low luminance for black, got {profile.MeanLuminance}");
    }

    [Fact]
    public async Task AnalyzeAsync_BrightImage_HighMeanLuminance()
    {
        using var image = TestImageGenerator.CreateSolidColor(200, 200, Color.White);
        var path = SaveTempImage(image, "bright.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.True(profile.MeanLuminance > 240, $"Expected high luminance for white, got {profile.MeanLuminance}");
    }

    [Fact]
    public async Task AnalyzeAsync_HighContrastImage_HighStdDev()
    {
        using var image = TestImageGenerator.CreateCheckerboard(200, 200, 20);
        var path = SaveTempImage(image, "contrast.png");

        var profile = await _analyzer.AnalyzeAsync(path);

        Assert.True(profile.LuminanceStdDev > 50, $"Expected high std dev, got {profile.LuminanceStdDev}");
    }

    #endregion

    #region Real Image Integration Tests

    [Fact]
    public async Task AnalyzeAsync_RealScreenshot_ComprehensiveProfile()
    {
        var testImagePath = GetTestImagePath("01-home.png");
        if (!File.Exists(testImagePath))
        {
            return;
        }

        var profile = await _analyzer.AnalyzeAsync(testImagePath);

        // Verify all properties are populated
        Assert.NotNull(profile.Sha256);
        Assert.True(profile.Width > 0);
        Assert.True(profile.Height > 0);
        Assert.NotEmpty(profile.DominantColors);
        Assert.NotNull(profile.ColorGrid);
        Assert.InRange(profile.EdgeDensity, 0.0, 1.0);
        Assert.InRange(profile.TextLikeliness, 0.0, 1.0);
        Assert.NotNull(profile.PerceptualHash);
        Assert.True(profile.TypeConfidence > 0);
    }

    [Fact]
    public async Task AnalyzeAsync_AllTestImages_NoExceptions()
    {
        var testImagesDir = GetTestImagesDirectory();
        if (!Directory.Exists(testImagesDir))
        {
            return;
        }

        var imageFiles = Directory.GetFiles(testImagesDir, "*.png");
        foreach (var imagePath in imageFiles)
        {
            var profile = await _analyzer.AnalyzeAsync(imagePath);

            Assert.NotNull(profile);
            Assert.True(profile.Width > 0, $"Width should be > 0 for {Path.GetFileName(imagePath)}");
            Assert.True(profile.Height > 0, $"Height should be > 0 for {Path.GetFileName(imagePath)}");
        }
    }

    [Fact]
    public async Task AnalyzeAsync_ChatResponse_HasTextElements()
    {
        var testImagePath = GetTestImagePath("03-chat-response.png");
        if (!File.Exists(testImagePath))
        {
            return;
        }

        var profile = await _analyzer.AnalyzeAsync(testImagePath);

        // Chat responses should have text
        Assert.True(profile.TextLikeliness > 0.1 || profile.DetectedType == ImageType.Screenshot,
            $"Expected chat response to have text elements, got TextLikeliness={profile.TextLikeliness}");
    }

    #endregion

    #region Helper Methods

    private string SaveTempImage(Image<Rgba32> image, string filename)
    {
        var path = Path.Combine(_tempDir, filename);
        image.SaveAsPng(path);
        return path;
    }

    private static int HammingDistance(ulong a, ulong b)
    {
        var xor = a ^ b;
        var count = 0;
        while (xor != 0)
        {
            count += (int)(xor & 1);
            xor >>= 1;
        }
        return count;
    }

    private static string GetTestImagePath(string filename)
    {
        var dir = Path.GetDirectoryName(typeof(ImageAnalyzerTests).Assembly.Location)!;
        return Path.Combine(dir, "TestImages", filename);
    }

    private static string GetTestImagesDirectory()
    {
        var dir = Path.GetDirectoryName(typeof(ImageAnalyzerTests).Assembly.Location)!;
        return Path.Combine(dir, "TestImages");
    }

    #endregion
}
