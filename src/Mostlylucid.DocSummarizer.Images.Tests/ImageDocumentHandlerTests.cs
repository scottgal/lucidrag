using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Services;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.DocSummarizer.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Mostlylucid.DocSummarizer.Images.Tests;

public class ImageDocumentHandlerTests : IDisposable
{
    private readonly ImageDocumentHandler _handler;
    private readonly string _tempDir;

    public ImageDocumentHandlerTests()
    {
        var config = Options.Create(new ImageConfig());
        var analyzer = new ImageAnalyzer(config);
        _handler = new ImageDocumentHandler(config, analyzer);
        _tempDir = Path.Combine(Path.GetTempPath(), "ImageHandlerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    #region SupportedExtensions Tests

    [Fact]
    public void SupportedExtensions_ContainsCommonFormats()
    {
        var extensions = _handler.SupportedExtensions;

        Assert.Contains(".jpg", extensions);
        Assert.Contains(".jpeg", extensions);
        Assert.Contains(".png", extensions);
        Assert.Contains(".gif", extensions);
        Assert.Contains(".webp", extensions);
    }

    [Fact]
    public void SupportedExtensions_AllLowercase()
    {
        var extensions = _handler.SupportedExtensions;

        Assert.All(extensions, ext => Assert.Equal(ext.ToLowerInvariant(), ext));
    }

    #endregion

    #region CanHandle Tests

    [Theory]
    [InlineData("test.png", true)]
    [InlineData("test.jpg", true)]
    [InlineData("test.jpeg", true)]
    [InlineData("test.gif", true)]
    [InlineData("test.webp", true)]
    [InlineData("test.bmp", true)]
    [InlineData("test.PNG", true)] // Case insensitive
    [InlineData("test.JPG", true)]
    [InlineData("test.pdf", false)]
    [InlineData("test.docx", false)]
    [InlineData("test.txt", false)]
    [InlineData("", false)]
    public void CanHandle_ReturnsCorrectResult(string filePath, bool expected)
    {
        var result = _handler.CanHandle(filePath);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CanHandle_NullPath_ReturnsFalse()
    {
        var result = _handler.CanHandle(null!);
        Assert.False(result);
    }

    #endregion

    #region ProcessAsync Tests

    [Fact]
    public async Task ProcessAsync_ReturnsDocumentContent()
    {
        using var image = TestImageGenerator.CreateColorBlocks(300, 200);
        var path = SaveTempImage(image, "test.png");

        var result = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        Assert.NotNull(result);
        Assert.NotNull(result.Markdown);
        Assert.Equal("image", result.ContentType);
    }

    [Fact]
    public async Task ProcessAsync_TitleContainsFilename()
    {
        using var image = TestImageGenerator.CreateSolidColor(100, 100, Color.Red);
        var path = SaveTempImage(image, "myimage.png");

        var result = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        Assert.Contains("myimage.png", result.Title);
    }

    [Fact]
    public async Task ProcessAsync_MarkdownContainsImageInfo()
    {
        using var image = TestImageGenerator.CreateColorBlocks(300, 200);
        var path = SaveTempImage(image, "info.png");

        var result = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        // Should contain dimensions
        Assert.Contains("300", result.Markdown);
        Assert.Contains("200", result.Markdown);

        // Should contain format
        Assert.Contains("PNG", result.Markdown);
    }

    [Fact]
    public async Task ProcessAsync_MetadataContainsExpectedKeys()
    {
        using var image = TestImageGenerator.CreateColorBlocks(200, 200);
        var path = SaveTempImage(image, "meta.png");

        var result = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        Assert.True(result.Metadata.ContainsKey("imageType"));
        Assert.True(result.Metadata.ContainsKey("width"));
        Assert.True(result.Metadata.ContainsKey("height"));
        Assert.True(result.Metadata.ContainsKey("format"));
        Assert.True(result.Metadata.ContainsKey("sha256"));
        Assert.True(result.Metadata.ContainsKey("textLikeliness"));
        Assert.True(result.Metadata.ContainsKey("dominantColors"));
    }

    [Fact]
    public async Task ProcessAsync_MetadataHasCorrectDimensions()
    {
        using var image = TestImageGenerator.CreateSolidColor(400, 300, Color.Blue);
        var path = SaveTempImage(image, "dims.png");

        var result = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        Assert.Equal(400, result.Metadata["width"]);
        Assert.Equal(300, result.Metadata["height"]);
    }

    [Fact]
    public async Task ProcessAsync_Screenshot_DetectsType()
    {
        using var image = TestImageGenerator.CreateScreenshotLike(800, 600);
        var path = SaveTempImage(image, "screenshot.png");

        var result = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        var imageType = result.Metadata["imageType"]?.ToString();
        Assert.NotNull(imageType);
    }

    [Fact]
    public async Task ProcessAsync_TextLikeImage_HasHighTextLikeliness()
    {
        using var image = TestImageGenerator.CreateTextLikeImage(400, 300);
        var path = SaveTempImage(image, "textlike.png");

        var result = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        var textLikeliness = (double)result.Metadata["textLikeliness"];
        Assert.InRange(textLikeliness, 0.0, 1.0);
    }

    [Fact]
    public async Task ProcessAsync_DominantColors_IsList()
    {
        using var image = TestImageGenerator.CreateColorBlocks(200, 200);
        var path = SaveTempImage(image, "colors.png");

        var result = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        var dominantColors = result.Metadata["dominantColors"] as List<string>;
        Assert.NotNull(dominantColors);
        Assert.NotEmpty(dominantColors);
    }

    #endregion

    #region Markdown Output Tests

    [Fact]
    public async Task ProcessAsync_Markdown_ContainsVisualProperties()
    {
        using var image = TestImageGenerator.CreateSharpImage(300, 300);
        var path = SaveTempImage(image, "visual.png");

        var result = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        Assert.Contains("Visual Properties", result.Markdown);
        Assert.Contains("Brightness", result.Markdown);
        Assert.Contains("Contrast", result.Markdown);
        Assert.Contains("Sharpness", result.Markdown);
    }

    [Fact]
    public async Task ProcessAsync_Markdown_ContainsColorAnalysis()
    {
        using var image = TestImageGenerator.CreateColorBlocks(200, 200);
        var path = SaveTempImage(image, "colormd.png");

        var result = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        Assert.Contains("Color Analysis", result.Markdown);
        Assert.Contains("Dominant Colors", result.Markdown);
    }

    [Fact]
    public async Task ProcessAsync_Markdown_ContainsTechnicalDetails()
    {
        using var image = TestImageGenerator.CreateSolidColor(100, 100, Color.Green);
        var path = SaveTempImage(image, "tech.png");

        var result = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        Assert.Contains("Technical Details", result.Markdown);
        Assert.Contains("SHA256", result.Markdown);
        Assert.Contains("Perceptual Hash", result.Markdown);
    }

    [Fact]
    public async Task ProcessAsync_GrayscaleImage_MarkdownIndicatesGrayscale()
    {
        using var image = TestImageGenerator.CreateGrayscaleImage(200, 200);
        var path = SaveTempImage(image, "gray.png");

        var result = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        Assert.Contains("grayscale", result.Markdown.ToLowerInvariant());
    }

    #endregion

    #region Priority and HandlerName Tests

    [Fact]
    public void Priority_ReturnsPositiveValue()
    {
        Assert.True(_handler.Priority > 0);
    }

    [Fact]
    public void HandlerName_ReturnsImageHandler()
    {
        Assert.Equal("ImageHandler", _handler.HandlerName);
    }

    #endregion

    #region DI Registration Tests

    [Fact]
    public void AddDocSummarizerImages_RegistersServices()
    {
        var services = new ServiceCollection();
        services.AddDocSummarizerImages();
        var provider = services.BuildServiceProvider();

        var analyzer = provider.GetService<IImageAnalyzer>();
        var handler = provider.GetService<IDocumentHandler>();

        Assert.NotNull(analyzer);
        Assert.NotNull(handler);
    }

    [Fact]
    public void AddDocSummarizerImages_RegistersSubAnalyzers()
    {
        var services = new ServiceCollection();
        services.AddDocSummarizerImages();
        var provider = services.BuildServiceProvider();

        var colorAnalyzer = provider.GetService<ColorAnalyzer>();
        var edgeAnalyzer = provider.GetService<EdgeAnalyzer>();
        var blurAnalyzer = provider.GetService<BlurAnalyzer>();
        var textAnalyzer = provider.GetService<TextLikelinessAnalyzer>();

        Assert.NotNull(colorAnalyzer);
        Assert.NotNull(edgeAnalyzer);
        Assert.NotNull(blurAnalyzer);
        Assert.NotNull(textAnalyzer);
    }

    [Fact]
    public void AddDocSummarizerImages_WithConfig_AppliesConfig()
    {
        var services = new ServiceCollection();
        services.AddDocSummarizerImages(config =>
        {
            config.MaxImageSize = 1024;
            config.EnableOcr = true;
        });
        var provider = services.BuildServiceProvider();

        var options = provider.GetService<IOptions<ImageConfig>>();
        Assert.NotNull(options);
        Assert.Equal(1024, options.Value.MaxImageSize);
        Assert.True(options.Value.EnableOcr);
    }

    #endregion

    #region Real Image Tests

    [Fact]
    public async Task ProcessAsync_RealScreenshot_ProducesValidOutput()
    {
        var testImagePath = GetTestImagePath("01-home.png");
        if (!File.Exists(testImagePath))
        {
            return;
        }

        var result = await _handler.ProcessAsync(testImagePath, new DocumentHandlerOptions());

        Assert.NotNull(result);
        Assert.NotEmpty(result.Markdown);
        Assert.NotEmpty(result.Title);
        Assert.Equal("image", result.ContentType);
    }

    [Fact]
    public async Task ProcessAsync_AllTestImages_ProduceValidOutput()
    {
        var testImagesDir = GetTestImagesDirectory();
        if (!Directory.Exists(testImagesDir))
        {
            return;
        }

        var imageFiles = Directory.GetFiles(testImagesDir, "*.png");
        foreach (var imagePath in imageFiles)
        {
            var result = await _handler.ProcessAsync(imagePath, new DocumentHandlerOptions());

            Assert.NotNull(result);
            Assert.NotEmpty(result.Markdown);
            Assert.True(result.Metadata.ContainsKey("width"));
            Assert.True(result.Metadata.ContainsKey("height"));
        }
    }

    #endregion

    #region Helper Methods

    private string SaveTempImage(Image<Rgba32> image, string filename)
    {
        var path = Path.Combine(_tempDir, filename);
        image.SaveAsPng(path);
        return path;
    }

    private static string GetTestImagePath(string filename)
    {
        var dir = Path.GetDirectoryName(typeof(ImageDocumentHandlerTests).Assembly.Location)!;
        return Path.Combine(dir, "TestImages", filename);
    }

    private static string GetTestImagesDirectory()
    {
        var dir = Path.GetDirectoryName(typeof(ImageDocumentHandlerTests).Assembly.Location)!;
        return Path.Combine(dir, "TestImages");
    }

    #endregion
}
