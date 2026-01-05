using System.CommandLine;
using System.CommandLine.Parsing;
using LucidRAG.ImageCli.Services;
using Mostlylucid.DocSummarizer.Images.Services;
using Mostlylucid.DocSummarizer.Images.Services.Vision;
using Spectre.Console;

namespace LucidRAG.ImageCli.Commands;

/// <summary>
/// Command for previewing images in the console with conversational filtering.
/// Demonstrates low-res pixel art rendering for quick visual feedback.
/// </summary>
public static class PreviewCommand
{
    private static readonly Argument<string> ImagePathArg = new("image-path")
    {
        Description = "Path to the image file to preview"
    };

    private static readonly Option<RenderMode> ModeOpt = new("--mode", "-m")
    {
        Description = "Rendering mode",
        DefaultValueFactory = _ => RenderMode.ColorBlocks
    };

    private static readonly Option<int> WidthOpt = new("--width", "-w")
    {
        Description = "Width in characters",
        DefaultValueFactory = _ => 80
    };

    private static readonly Option<int> HeightOpt = new("--height", "-h")
    {
        Description = "Height in characters",
        DefaultValueFactory = _ => 40
    };

    private static readonly Option<bool> CompactOpt = new("--compact", "-c")
    {
        Description = "Show compact single-line preview",
        DefaultValueFactory = _ => false
    };

    private static readonly Option<bool> ColorBarOpt = new("--color-bar")
    {
        Description = "Show dominant color bar",
        DefaultValueFactory = _ => false
    };

    private static readonly Option<bool> PanelOpt = new("--panel", "-p")
    {
        Description = "Show preview in bordered panel",
        DefaultValueFactory = _ => false
    };

    public static Command Create()
    {
        var command = new Command("preview", "Preview image in console with pixel art rendering");
        command.Arguments.Add(ImagePathArg);
        command.Options.Add(ModeOpt);
        command.Options.Add(WidthOpt);
        command.Options.Add(HeightOpt);
        command.Options.Add(CompactOpt);
        command.Options.Add(ColorBarOpt);
        command.Options.Add(PanelOpt);

        command.SetAction((parseResult, ct) =>
        {
            var imagePath = parseResult.GetValue(ImagePathArg)!;
            var mode = parseResult.GetValue(ModeOpt);
            var width = parseResult.GetValue(WidthOpt);
            var height = parseResult.GetValue(HeightOpt);
            var compact = parseResult.GetValue(CompactOpt);
            var colorBar = parseResult.GetValue(ColorBarOpt);
            var panel = parseResult.GetValue(PanelOpt);

            // Validate image path
            if (!File.Exists(imagePath))
            {
                AnsiConsole.MarkupLine($"[red]✗ Error:[/] Image file not found: {Markup.Escape(imagePath)}");
                return Task.FromResult(1);
            }

            try
            {
                if (compact)
                {
                    // Compact single-line preview
                    var compactPreview = ConsoleImageRenderer.GenerateCompactPreview(imagePath, width);
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(Path.GetFileName(imagePath))}[/]");
                    AnsiConsole.MarkupLine(compactPreview);
                }
                else if (colorBar)
                {
                    // Dominant color bar
                    var bar = ConsoleImageRenderer.GenerateColorBar(imagePath, width);
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(Path.GetFileName(imagePath))}[/]");
                    AnsiConsole.MarkupLine(bar);
                }
                else if (panel)
                {
                    // Bordered panel preview
                    var previewPanel = ConsoleImageRenderer.CreatePreviewPanel(imagePath);
                    AnsiConsole.Write(previewPanel);
                }
                else
                {
                    // Full console rendering
                    AnsiConsole.MarkupLine($"[cyan]Preview:[/] {Markup.Escape(Path.GetFileName(imagePath))}");
                    AnsiConsole.MarkupLine($"[dim]Mode: {mode}, Size: {width}x{height}[/]");
                    AnsiConsole.WriteLine();

                    ConsoleImageRenderer.RenderToConsole(imagePath, width, height, mode);

                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[dim]Rendering modes: ColorBlocks (best), GrayscaleBlocks, Ascii, Braille (highest resolution)[/]");
                }

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Error:[/] {Markup.Escape(ex.Message)}");
                return Task.FromResult(1);
            }
        });

        return command;
    }
}
