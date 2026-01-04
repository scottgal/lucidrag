using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Spectre.Console;

namespace LucidRAG.ImageCli.Services;

/// <summary>
/// Renders low-resolution image previews in the console using Unicode block characters.
/// Enables conversational filtering with visual feedback.
///
/// Techniques:
/// - Unicode half-blocks (▀▄) for 2 pixels per character (double vertical resolution)
/// - ANSI 256-color palette for color approximation
/// - Aspect ratio preservation
/// - Multiple rendering modes (blocks, ASCII, Braille)
/// </summary>
public class ConsoleImageRenderer
{
    private const int DefaultWidth = 80;  // Terminal width in characters
    private const int DefaultHeight = 40; // Terminal height in characters

    /// <summary>
    /// Render an image to console using Unicode half-blocks with colors.
    /// Uses ▀ (upper half block) with foreground and background colors to represent 2 vertical pixels per character.
    /// </summary>
    public static void RenderToConsole(
        string imagePath,
        int maxWidth = DefaultWidth,
        int maxHeight = DefaultHeight,
        RenderMode mode = RenderMode.ColorBlocks)
    {
        using var image = Image.Load<Rgb24>(imagePath);

        switch (mode)
        {
            case RenderMode.ColorBlocks:
                RenderColorBlocks(image, maxWidth, maxHeight);
                break;
            case RenderMode.GrayscaleBlocks:
                RenderGrayscaleBlocks(image, maxWidth, maxHeight);
                break;
            case RenderMode.Ascii:
                RenderAscii(image, maxWidth, maxHeight);
                break;
            case RenderMode.Braille:
                RenderBraille(image, maxWidth, maxHeight);
                break;
        }
    }

    /// <summary>
    /// Render using colored Unicode half-blocks (best quality).
    /// Each character represents 2 vertical pixels using ▀ with different fg/bg colors.
    /// </summary>
    private static void RenderColorBlocks(Image<Rgb24> image, int maxWidth, int maxHeight)
    {
        // Calculate target dimensions preserving aspect ratio
        // Note: Each character is 2 pixels tall (using half-blocks)
        var (width, height) = CalculateTargetDimensions(
            image.Width, image.Height, maxWidth, maxHeight * 2);

        // Resize image
        using var resized = image.Clone(ctx => ctx.Resize(width, height));

        // Render using half-blocks (▀)
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x++)
            {
                // Get top and bottom pixels
                var topPixel = resized[x, y];
                var bottomPixel = y + 1 < height ? resized[x, y + 1] : topPixel;

                // Convert to ANSI colors
                var topColor = RgbToAnsiColor(topPixel);
                var bottomColor = RgbToAnsiColor(bottomPixel);

                // Use upper half block (▀) with top color as foreground, bottom color as background
                AnsiConsole.Markup($"[rgb({topPixel.R},{topPixel.G},{topPixel.B}) on rgb({bottomPixel.R},{bottomPixel.G},{bottomPixel.B})]▀[/]");
            }
            AnsiConsole.WriteLine();
        }
    }

    /// <summary>
    /// Render using grayscale blocks with varying intensity.
    /// </summary>
    private static void RenderGrayscaleBlocks(Image<Rgb24> image, int maxWidth, int maxHeight)
    {
        var (width, height) = CalculateTargetDimensions(
            image.Width, image.Height, maxWidth, maxHeight);

        using var resized = image.Clone(ctx => ctx.Resize(width, height).Grayscale());

        // Grayscale characters from light to dark
        var chars = new[] { ' ', '░', '▒', '▓', '█' };

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var pixel = resized[x, y];
                var brightness = (pixel.R + pixel.G + pixel.B) / 3;
                var charIndex = (chars.Length - 1) - (brightness * chars.Length / 256);
                charIndex = Math.Clamp(charIndex, 0, chars.Length - 1);

                AnsiConsole.Markup($"[grey{brightness * 100 / 255}]{chars[charIndex]}[/]");
            }
            AnsiConsole.WriteLine();
        }
    }

    /// <summary>
    /// Render using ASCII characters based on brightness.
    /// Classic ASCII art style.
    /// </summary>
    private static void RenderAscii(Image<Rgb24> image, int maxWidth, int maxHeight)
    {
        var (width, height) = CalculateTargetDimensions(
            image.Width, image.Height, maxWidth, maxHeight);

        using var resized = image.Clone(ctx => ctx.Resize(width, height).Grayscale());

        // ASCII characters from light to dark (by visual density)
        var chars = " .:-=+*#%@";

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var pixel = resized[x, y];
                var brightness = (pixel.R + pixel.G + pixel.B) / 3;
                var charIndex = (chars.Length - 1) - (brightness * chars.Length / 256);
                charIndex = Math.Clamp(charIndex, 0, chars.Length - 1);

                Console.Write(chars[charIndex]);
            }
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Render using Braille characters (Unicode U+2800-U+28FF).
    /// Highest resolution: 2x4 pixels per character = 8x resolution boost.
    /// </summary>
    private static void RenderBraille(Image<Rgb24> image, int maxWidth, int maxHeight)
    {
        // Each Braille character is 2x4 pixels
        var (width, height) = CalculateTargetDimensions(
            image.Width, image.Height, maxWidth * 2, maxHeight * 4);

        using var resized = image.Clone(ctx => ctx.Resize(width, height).Grayscale());

        // Braille dot positions (Unicode U+2800 base)
        // Dots are numbered 1,2,3,4,5,6,7,8 in this pattern:
        // 1 4
        // 2 5
        // 3 6
        // 7 8

        var dotValues = new[] { 0x01, 0x02, 0x04, 0x40, 0x08, 0x10, 0x20, 0x80 };

        for (int y = 0; y < height; y += 4)
        {
            for (int x = 0; x < width; x += 2)
            {
                int brailleChar = 0x2800; // Braille pattern blank

                // Check each of 8 dot positions
                for (int dy = 0; dy < 4 && y + dy < height; dy++)
                {
                    for (int dx = 0; dx < 2 && x + dx < width; dx++)
                    {
                        var pixel = resized[x + dx, y + dy];
                        var brightness = (pixel.R + pixel.G + pixel.B) / 3;

                        // Threshold: if bright, set dot
                        if (brightness > 128)
                        {
                            var dotIndex = dy * 2 + dx;
                            brailleChar |= dotValues[dotIndex];
                        }
                    }
                }

                Console.Write((char)brailleChar);
            }
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Generate a compact preview string for inline display (single line).
    /// </summary>
    public static string GenerateCompactPreview(string imagePath, int width = 32)
    {
        using var image = Image.Load<Rgb24>(imagePath);
        using var resized = image.Clone(ctx => ctx.Resize(width, 1));

        var preview = new System.Text.StringBuilder();

        for (int x = 0; x < width; x++)
        {
            var pixel = resized[x, 0];
            preview.Append($"[rgb({pixel.R},{pixel.G},{pixel.B})]█[/]");
        }

        return preview.ToString();
    }

    /// <summary>
    /// Get a color bar representation of the image's dominant colors.
    /// </summary>
    public static string GenerateColorBar(string imagePath, int segments = 16)
    {
        using var image = Image.Load<Rgb24>(imagePath);
        using var resized = image.Clone(ctx => ctx.Resize(segments, 1));

        var bar = new System.Text.StringBuilder();

        for (int x = 0; x < segments; x++)
        {
            var pixel = resized[x, 0];
            bar.Append($"[rgb({pixel.R},{pixel.G},{pixel.B})]▌[/]");
        }

        return bar.ToString();
    }

    /// <summary>
    /// Calculate target dimensions preserving aspect ratio.
    /// </summary>
    private static (int width, int height) CalculateTargetDimensions(
        int sourceWidth,
        int sourceHeight,
        int maxWidth,
        int maxHeight)
    {
        var aspectRatio = (double)sourceWidth / sourceHeight;

        int targetWidth, targetHeight;

        if (aspectRatio > (double)maxWidth / maxHeight)
        {
            // Width is limiting factor
            targetWidth = maxWidth;
            targetHeight = (int)(maxWidth / aspectRatio);
        }
        else
        {
            // Height is limiting factor
            targetHeight = maxHeight;
            targetWidth = (int)(maxHeight * aspectRatio);
        }

        return (targetWidth, targetHeight);
    }

    /// <summary>
    /// Convert RGB to nearest ANSI 256-color code.
    /// </summary>
    private static int RgbToAnsiColor(Rgb24 rgb)
    {
        // ANSI 256 color palette:
        // 0-15: System colors
        // 16-231: 6x6x6 RGB cube
        // 232-255: Grayscale

        // Use 6x6x6 RGB cube (colors 16-231)
        var r = (int)Math.Round(rgb.R / 255.0 * 5);
        var g = (int)Math.Round(rgb.G / 255.0 * 5);
        var b = (int)Math.Round(rgb.B / 255.0 * 5);

        return 16 + (36 * r) + (6 * g) + b;
    }

    /// <summary>
    /// Create a bordered preview panel for conversational display.
    /// </summary>
    public static Panel CreatePreviewPanel(string imagePath, string? caption = null)
    {
        using var image = Image.Load<Rgb24>(imagePath);
        var (width, height) = CalculateTargetDimensions(image.Width, image.Height, 60, 30);

        var preview = new System.Text.StringBuilder();

        using var resized = image.Clone(ctx => ctx.Resize(width, height * 2));

        for (int y = 0; y < height * 2; y += 2)
        {
            for (int x = 0; x < width; x++)
            {
                var topPixel = resized[x, y];
                var bottomPixel = y + 1 < height * 2 ? resized[x, y + 1] : topPixel;

                preview.Append($"[rgb({topPixel.R},{topPixel.G},{topPixel.B}) on rgb({bottomPixel.R},{bottomPixel.G},{bottomPixel.B})]▀[/]");
            }
            preview.AppendLine();
        }

        var panel = new Panel(new Markup(preview.ToString()))
        {
            Header = new PanelHeader(caption ?? Path.GetFileName(imagePath)),
            Border = BoxBorder.Rounded
        };

        panel.BorderStyle(Style.Parse("cyan"));

        return panel;
    }
}

/// <summary>
/// Rendering modes for console image display.
/// </summary>
public enum RenderMode
{
    /// <summary>
    /// Colored Unicode blocks (best quality, requires true color support).
    /// </summary>
    ColorBlocks,

    /// <summary>
    /// Grayscale Unicode blocks.
    /// </summary>
    GrayscaleBlocks,

    /// <summary>
    /// ASCII art characters.
    /// </summary>
    Ascii,

    /// <summary>
    /// Braille characters (highest resolution, 2x4 pixels per character).
    /// </summary>
    Braille
}
