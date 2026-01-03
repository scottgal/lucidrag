using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace Mostlylucid.DocSummarizer.Images.Tests;

public class ColorAnalyzerTests
{
    private readonly ColorAnalyzer _analyzer;

    public ColorAnalyzerTests()
    {
        _analyzer = new ColorAnalyzer(new ColorGridConfig());
    }

    #region ExtractDominantColors Tests

    [Fact]
    public void ExtractDominantColors_SolidRed_ReturnsDominantRed()
    {
        using var image = TestImageGenerator.CreateSolidColor(200, 200, Color.Red);

        var colors = _analyzer.ExtractDominantColors(image);

        Assert.NotEmpty(colors);
        var dominant = colors[0];
        // Red should be dominant (close to #FF0000)
        Assert.Contains("Red", dominant.Name);
        Assert.True(dominant.Percentage > 90, $"Expected >90% red, got {dominant.Percentage}%");
    }

    [Fact]
    public void ExtractDominantColors_SolidBlue_ReturnsDominantBlue()
    {
        using var image = TestImageGenerator.CreateSolidColor(200, 200, Color.Blue);

        var colors = _analyzer.ExtractDominantColors(image);

        Assert.NotEmpty(colors);
        var dominant = colors[0];
        Assert.Contains("Blue", dominant.Name);
        Assert.True(dominant.Percentage > 90);
    }

    [Fact]
    public void ExtractDominantColors_ColorBlocks_ReturnsMultipleColors()
    {
        using var image = TestImageGenerator.CreateColorBlocks(300, 200);

        var colors = _analyzer.ExtractDominantColors(image, maxColors: 6);

        Assert.True(colors.Count >= 3, $"Expected at least 3 colors, got {colors.Count}");
        // Each block is ~16.6% of the image
        Assert.All(colors, c => Assert.True(c.Percentage > 10 && c.Percentage < 25,
            $"Expected each color ~16%, got {c.Percentage}%"));
    }

    [Fact]
    public void ExtractDominantColors_BlackAndWhite_ReturnsBlackAndWhite()
    {
        using var image = TestImageGenerator.CreateCheckerboard(200, 200, 20);

        var colors = _analyzer.ExtractDominantColors(image, maxColors: 2);

        Assert.Equal(2, colors.Count);
        var colorNames = colors.Select(c => c.Name).ToList();
        Assert.Contains("Black", colorNames);
        Assert.Contains("White", colorNames);
    }

    [Fact]
    public void ExtractDominantColors_ReturnsHexColors()
    {
        using var image = TestImageGenerator.CreateSolidColor(100, 100, Color.Green);

        var colors = _analyzer.ExtractDominantColors(image);

        Assert.All(colors, c =>
        {
            Assert.StartsWith("#", c.Hex);
            Assert.Equal(7, c.Hex.Length); // #RRGGBB format
        });
    }

    #endregion

    #region ComputeColorGrid Tests

    [Fact]
    public void ComputeColorGrid_ReturnsCorrectGridSize()
    {
        var config = new ColorGridConfig { Rows = 3, Cols = 3 };
        var analyzer = new ColorAnalyzer(config);
        using var image = TestImageGenerator.CreateColorBlocks(300, 300);

        var grid = analyzer.ComputeColorGrid(image);

        Assert.Equal(3, grid.Rows);
        Assert.Equal(3, grid.Cols);
        Assert.Equal(9, grid.Cells.Count);
    }

    [Fact]
    public void ComputeColorGrid_CellsHaveCorrectPositions()
    {
        var config = new ColorGridConfig { Rows = 2, Cols = 2 };
        var analyzer = new ColorAnalyzer(config);
        using var image = TestImageGenerator.CreateColorBlocks(200, 200);

        var grid = analyzer.ComputeColorGrid(image);

        // Verify all positions exist
        var positions = grid.Cells.Select(c => (c.Row, c.Col)).ToHashSet();
        Assert.Contains((0, 0), positions);
        Assert.Contains((0, 1), positions);
        Assert.Contains((1, 0), positions);
        Assert.Contains((1, 1), positions);
    }

    [Fact]
    public void ComputeColorGrid_CellsHaveValidCoverage()
    {
        var config = new ColorGridConfig { Rows = 3, Cols = 3 };
        var analyzer = new ColorAnalyzer(config);
        using var image = TestImageGenerator.CreateSolidColor(300, 300, Color.Red);

        var grid = analyzer.ComputeColorGrid(image);

        Assert.All(grid.Cells, cell =>
        {
            Assert.InRange(cell.Coverage, 0.0, 1.0);
            // For solid color, coverage should be very high
            Assert.True(cell.Coverage > 0.9, $"Expected coverage > 0.9, got {cell.Coverage}");
        });
    }

    [Fact]
    public void ComputeColorGrid_TopRowDifferentFromBottomRow()
    {
        // Create image with red top half, blue bottom half
        using var image = new Image<Rgba32>(300, 200);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.Red, new Rectangle(0, 0, 300, 100));
            ctx.Fill(Color.Blue, new Rectangle(0, 100, 300, 100));
        });

        var config = new ColorGridConfig { Rows = 2, Cols = 1 };
        var analyzer = new ColorAnalyzer(config);
        var grid = analyzer.ComputeColorGrid(image);

        var topCell = grid.Cells.First(c => c.Row == 0);
        var bottomCell = grid.Cells.First(c => c.Row == 1);

        Assert.NotEqual(topCell.Hex, bottomCell.Hex);
    }

    #endregion

    #region Saturation Tests

    [Fact]
    public void CalculateMeanSaturation_Grayscale_ReturnsLowSaturation()
    {
        using var image = TestImageGenerator.CreateGrayscaleImage(200, 200);

        var saturation = _analyzer.CalculateMeanSaturation(image);

        Assert.True(saturation < 0.1, $"Expected saturation < 0.1 for grayscale, got {saturation}");
    }

    [Fact]
    public void CalculateMeanSaturation_ColorfulImage_ReturnsHighSaturation()
    {
        using var image = TestImageGenerator.CreateColorBlocks(200, 200);

        var saturation = _analyzer.CalculateMeanSaturation(image);

        Assert.True(saturation > 0.5, $"Expected saturation > 0.5 for colorful image, got {saturation}");
    }

    [Fact]
    public void IsMostlyGrayscale_GrayscaleImage_ReturnsTrue()
    {
        using var image = TestImageGenerator.CreateGrayscaleImage(200, 200);

        var isGrayscale = _analyzer.IsMostlyGrayscale(image);

        Assert.True(isGrayscale);
    }

    [Fact]
    public void IsMostlyGrayscale_ColorfulImage_ReturnsFalse()
    {
        using var image = TestImageGenerator.CreateColorBlocks(200, 200);

        var isGrayscale = _analyzer.IsMostlyGrayscale(image);

        Assert.False(isGrayscale);
    }

    #endregion

    #region Real Image Tests

    [Fact]
    public void ExtractDominantColors_Screenshot_DetectsUIColors()
    {
        var testImagePath = GetTestImagePath("01-home.png");
        if (!File.Exists(testImagePath))
        {
            // Skip if test image not available
            return;
        }

        using var image = Image.Load<Rgba32>(testImagePath);
        var colors = _analyzer.ExtractDominantColors(image);

        Assert.NotEmpty(colors);
        // Screenshots typically have dominant background colors
        Assert.True(colors[0].Percentage > 10);
    }

    [Fact]
    public void ComputeColorGrid_Screenshot_ReturnsValidGrid()
    {
        var testImagePath = GetTestImagePath("02-chat-input.png");
        if (!File.Exists(testImagePath))
        {
            return;
        }

        using var image = Image.Load<Rgba32>(testImagePath);
        var grid = _analyzer.ComputeColorGrid(image);

        Assert.NotEmpty(grid.Cells);
        Assert.All(grid.Cells, cell =>
        {
            Assert.NotNull(cell.Hex);
            Assert.StartsWith("#", cell.Hex);
        });
    }

    #endregion

    private static string GetTestImagePath(string filename)
    {
        var dir = Path.GetDirectoryName(typeof(ColorAnalyzerTests).Assembly.Location)!;
        return Path.Combine(dir, "TestImages", filename);
    }
}
