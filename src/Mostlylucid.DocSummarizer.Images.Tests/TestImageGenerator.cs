using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Images.Tests;

/// <summary>
/// Helper class to generate test images with known characteristics
/// </summary>
public static class TestImageGenerator
{
    /// <summary>
    /// Create a solid color image
    /// </summary>
    public static Image<Rgba32> CreateSolidColor(int width, int height, Color color)
    {
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx => ctx.Fill(color));
        return image;
    }

    /// <summary>
    /// Create an image with horizontal stripes (good for testing edge detection)
    /// </summary>
    public static Image<Rgba32> CreateHorizontalStripes(int width, int height, int stripeHeight = 10)
    {
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx =>
        {
            var isWhite = true;
            for (var y = 0; y < height; y += stripeHeight)
            {
                var color = isWhite ? Color.White : Color.Black;
                ctx.Fill(color, new Rectangle(0, y, width, Math.Min(stripeHeight, height - y)));
                isWhite = !isWhite;
            }
        });
        return image;
    }

    /// <summary>
    /// Create an image with vertical stripes
    /// </summary>
    public static Image<Rgba32> CreateVerticalStripes(int width, int height, int stripeWidth = 10)
    {
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx =>
        {
            var isWhite = true;
            for (var x = 0; x < width; x += stripeWidth)
            {
                var color = isWhite ? Color.White : Color.Black;
                ctx.Fill(color, new Rectangle(x, 0, Math.Min(stripeWidth, width - x), height));
                isWhite = !isWhite;
            }
        });
        return image;
    }

    /// <summary>
    /// Create a checkerboard pattern (high edge density)
    /// </summary>
    public static Image<Rgba32> CreateCheckerboard(int width, int height, int squareSize = 10)
    {
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx =>
        {
            for (var y = 0; y < height; y += squareSize)
            for (var x = 0; x < width; x += squareSize)
            {
                var isWhite = ((x / squareSize) + (y / squareSize)) % 2 == 0;
                var color = isWhite ? Color.White : Color.Black;
                ctx.Fill(color, new Rectangle(x, y,
                    Math.Min(squareSize, width - x),
                    Math.Min(squareSize, height - y)));
            }
        });
        return image;
    }

    /// <summary>
    /// Create a gradient image (smooth transitions, low edge density)
    /// </summary>
    public static Image<Rgba32> CreateGradient(int width, int height, Color startColor, Color endColor)
    {
        var image = new Image<Rgba32>(width, height);

        var start = (Rgba32)startColor;
        var end = (Rgba32)endColor;

        for (var y = 0; y < height; y++)
        {
            var t = y / (float)(height - 1);
            var r = (byte)(start.R + (end.R - start.R) * t);
            var g = (byte)(start.G + (end.G - start.G) * t);
            var b = (byte)(start.B + (end.B - start.B) * t);
            var color = new Rgba32(r, g, b);

            for (var x = 0; x < width; x++)
            {
                image[x, y] = color;
            }
        }

        return image;
    }

    /// <summary>
    /// Create an image with random noise (blurry appearance)
    /// </summary>
    public static Image<Rgba32> CreateNoiseImage(int width, int height, int seed = 42)
    {
        var image = new Image<Rgba32>(width, height);
        var random = new Random(seed);

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)random.Next(256);
            image[x, y] = new Rgba32(value, value, value);
        }

        return image;
    }

    /// <summary>
    /// Create a blurred image (low Laplacian variance)
    /// </summary>
    public static Image<Rgba32> CreateBlurredImage(int width, int height)
    {
        var image = CreateCheckerboard(width, height, 5);
        image.Mutate(ctx => ctx.GaussianBlur(10));
        return image;
    }

    /// <summary>
    /// Create a sharp image with clear edges
    /// </summary>
    public static Image<Rgba32> CreateSharpImage(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.White);
            // Draw some sharp rectangles
            ctx.Fill(Color.Black, new Rectangle(50, 50, 100, 100));
            ctx.Fill(Color.Red, new Rectangle(200, 50, 100, 100));
            ctx.Fill(Color.Blue, new Rectangle(50, 200, 100, 100));
            ctx.Fill(Color.Green, new Rectangle(200, 200, 100, 100));
        });
        return image;
    }

    /// <summary>
    /// Create a text-like image (high contrast, bimodal, horizontal patterns)
    /// </summary>
    public static Image<Rgba32> CreateTextLikeImage(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx =>
        {
            // White background
            ctx.Fill(Color.White);

            // Draw "text-like" horizontal lines with gaps (simulating text)
            var lineHeight = 20;
            var lineGap = 10;
            var y = 30;

            while (y < height - 30)
            {
                // Draw a line of "text" as small rectangles
                var x = 30;
                while (x < width - 50)
                {
                    var wordWidth = 20 + (y * x) % 50; // Variable "word" lengths
                    ctx.Fill(Color.Black, new Rectangle(x, y, Math.Min(wordWidth, width - x - 30), lineHeight / 2));
                    x += wordWidth + 10; // Word spacing
                }
                y += lineHeight + lineGap;
            }
        });
        return image;
    }

    /// <summary>
    /// Create a colorful image with dominant colors
    /// </summary>
    public static Image<Rgba32> CreateColorBlocks(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        var blockWidth = width / 3;
        var blockHeight = height / 2;

        image.Mutate(ctx =>
        {
            // Top row
            ctx.Fill(Color.Red, new Rectangle(0, 0, blockWidth, blockHeight));
            ctx.Fill(Color.Green, new Rectangle(blockWidth, 0, blockWidth, blockHeight));
            ctx.Fill(Color.Blue, new Rectangle(blockWidth * 2, 0, width - blockWidth * 2, blockHeight));

            // Bottom row
            ctx.Fill(Color.Yellow, new Rectangle(0, blockHeight, blockWidth, height - blockHeight));
            ctx.Fill(Color.Cyan, new Rectangle(blockWidth, blockHeight, blockWidth, height - blockHeight));
            ctx.Fill(Color.Magenta, new Rectangle(blockWidth * 2, blockHeight, width - blockWidth * 2, height - blockHeight));
        });

        return image;
    }

    /// <summary>
    /// Create a grayscale image
    /// </summary>
    public static Image<Rgba32> CreateGrayscaleImage(int width, int height)
    {
        var image = CreateColorBlocks(width, height);
        image.Mutate(ctx => ctx.Grayscale());
        return image;
    }

    /// <summary>
    /// Create a screenshot-like image (UI elements, straight edges)
    /// </summary>
    public static Image<Rgba32> CreateScreenshotLike(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx =>
        {
            // Light gray background
            ctx.Fill(new Color(new Rgba32(240, 240, 240)));

            // Title bar
            ctx.Fill(new Color(new Rgba32(60, 60, 60)), new Rectangle(0, 0, width, 40));

            // Sidebar
            ctx.Fill(new Color(new Rgba32(50, 50, 50)), new Rectangle(0, 40, 200, height - 40));

            // Content area buttons
            ctx.Fill(Color.Blue, new Rectangle(220, 60, 100, 30));
            ctx.Fill(Color.Green, new Rectangle(340, 60, 100, 30));

            // "Text" lines
            for (var y = 120; y < height - 50; y += 30)
            {
                ctx.Fill(new Color(new Rgba32(100, 100, 100)), new Rectangle(220, y, 300, 15));
            }
        });
        return image;
    }

    /// <summary>
    /// Create a diagram-like image (shapes, limited colors)
    /// </summary>
    public static Image<Rgba32> CreateDiagramLike(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.White);

            // Draw boxes connected by lines
            ctx.Fill(Color.LightBlue, new Rectangle(50, 50, 100, 60));
            ctx.Fill(Color.LightBlue, new Rectangle(50, 200, 100, 60));
            ctx.Fill(Color.LightGreen, new Rectangle(250, 125, 100, 60));

            // Connecting "arrows" (simplified as rectangles)
            ctx.Fill(Color.Gray, new Rectangle(150, 75, 100, 5));
            ctx.Fill(Color.Gray, new Rectangle(150, 225, 100, 5));
            ctx.Fill(Color.Gray, new Rectangle(245, 150, 10, 30));
        });
        return image;
    }

    /// <summary>
    /// Create a small icon-like image
    /// </summary>
    public static Image<Rgba32> CreateIconLike(int size = 64)
    {
        var image = new Image<Rgba32>(size, size);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.Transparent);

            // Draw a simple icon shape
            var padding = size / 8;
            ctx.Fill(Color.DodgerBlue, new Rectangle(padding, padding, size - padding * 2, size - padding * 2));

            // Add some detail
            var innerPadding = size / 4;
            ctx.Fill(Color.White, new Rectangle(innerPadding, innerPadding, size / 2, size / 2));
        });
        return image;
    }

    /// <summary>
    /// Save a test image to a file
    /// </summary>
    public static string SaveTestImage(Image<Rgba32> image, string directory, string filename)
    {
        var path = Path.Combine(directory, filename);
        image.SaveAsPng(path);
        return path;
    }
}
