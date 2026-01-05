using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Spectre.Console;

namespace Mostlylucid.ImageSummarizer.Cli.Services;

/// <summary>
/// Renders low-resolution image previews in the console using Unicode block characters.
/// Enables conversational filtering with visual feedback.
/// </summary>
public static class ConsoleImageRenderer
{
    private const int DefaultWidth = 60;
    private const int DefaultHeight = 20;

    /// <summary>
    /// Render an image preview to console using Unicode half-blocks with colors.
    /// </summary>
    public static void RenderToConsole(
        string imagePath,
        int maxWidth = DefaultWidth,
        int maxHeight = DefaultHeight,
        bool grayscale = false)
    {
        try
        {
            using var image = Image.Load<Rgb24>(imagePath);

            if (grayscale)
                RenderGrayscaleBlocks(image, maxWidth, maxHeight);
            else
                RenderColorBlocks(image, maxWidth, maxHeight);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[dim]Image preview unavailable: {Markup.Escape(ex.Message)}[/]");
        }
    }

    /// <summary>
    /// Render using colored Unicode half-blocks (best quality).
    /// Each character represents 2 vertical pixels using half-block with different fg/bg colors.
    /// </summary>
    private static void RenderColorBlocks(Image<Rgb24> image, int maxWidth, int maxHeight)
    {
        var (width, height) = CalculateTargetDimensions(
            image.Width, image.Height, maxWidth, maxHeight * 2);

        using var resized = image.Clone(ctx => ctx.Resize(width, height));

        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x++)
            {
                var topPixel = resized[x, y];
                var bottomPixel = y + 1 < height ? resized[x, y + 1] : topPixel;

                AnsiConsole.Markup($"[rgb({topPixel.R},{topPixel.G},{topPixel.B}) on rgb({bottomPixel.R},{bottomPixel.G},{bottomPixel.B})]\u2580[/]");
            }
            AnsiConsole.WriteLine();
        }
    }

    /// <summary>
    /// Render using grayscale blocks with half-block technique for better resolution.
    /// </summary>
    private static void RenderGrayscaleBlocks(Image<Rgb24> image, int maxWidth, int maxHeight)
    {
        var (width, height) = CalculateTargetDimensions(
            image.Width, image.Height, maxWidth, maxHeight * 2);

        using var resized = image.Clone(ctx => ctx.Resize(width, height).Grayscale());

        // Use half-blocks for grayscale too - just with gray RGB values
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x++)
            {
                var topPixel = resized[x, y];
                var bottomPixel = y + 1 < height ? resized[x, y + 1] : topPixel;

                // Use the grayscale value for all RGB components
                var topGray = topPixel.R;
                var bottomGray = bottomPixel.R;

                AnsiConsole.Markup($"[rgb({topGray},{topGray},{topGray}) on rgb({bottomGray},{bottomGray},{bottomGray})]\u2580[/]");
            }
            AnsiConsole.WriteLine();
        }
    }

    /// <summary>
    /// Generate a color bar representation of the image (single line preview).
    /// </summary>
    public static string GenerateColorBar(string imagePath, int segments = 40)
    {
        try
        {
            using var image = Image.Load<Rgb24>(imagePath);
            using var resized = image.Clone(ctx => ctx.Resize(segments, 1));

            var bar = new System.Text.StringBuilder();

            for (int x = 0; x < segments; x++)
            {
                var pixel = resized[x, 0];
                bar.Append($"[rgb({pixel.R},{pixel.G},{pixel.B})]\u258c[/]");
            }

            return bar.ToString();
        }
        catch
        {
            return "[dim]Preview unavailable[/]";
        }
    }

    /// <summary>
    /// Create a bordered preview panel for display.
    /// </summary>
    public static Panel CreatePreviewPanel(string imagePath, string? caption = null, int maxWidth = 60, int maxHeight = 20)
    {
        try
        {
            using var image = Image.Load<Rgb24>(imagePath);
            var (width, height) = CalculateTargetDimensions(image.Width, image.Height, maxWidth, maxHeight * 2);

            var preview = new System.Text.StringBuilder();

            using var resized = image.Clone(ctx => ctx.Resize(width, height));

            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x++)
                {
                    var topPixel = resized[x, y];
                    var bottomPixel = y + 1 < height ? resized[x, y + 1] : topPixel;

                    preview.Append($"[rgb({topPixel.R},{topPixel.G},{topPixel.B}) on rgb({bottomPixel.R},{bottomPixel.G},{bottomPixel.B})]\u2580[/]");
                }
                preview.AppendLine();
            }

            var panel = new Panel(new Markup(preview.ToString().TrimEnd()))
            {
                Header = new PanelHeader(caption ?? Path.GetFileName(imagePath)),
                Border = BoxBorder.Rounded
            };

            panel.BorderStyle(Style.Parse("cyan"));

            return panel;
        }
        catch (Exception ex)
        {
            return new Panel($"[dim]Preview unavailable: {Markup.Escape(ex.Message)}[/]")
            {
                Header = new PanelHeader(caption ?? "Image"),
                Border = BoxBorder.Rounded
            };
        }
    }

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
            targetWidth = maxWidth;
            targetHeight = (int)(maxWidth / aspectRatio);
        }
        else
        {
            targetHeight = maxHeight;
            targetWidth = (int)(maxHeight * aspectRatio);
        }

        return (Math.Max(1, targetWidth), Math.Max(1, targetHeight));
    }
}
