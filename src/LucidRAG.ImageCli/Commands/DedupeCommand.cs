using System.CommandLine;
using System.CommandLine.Parsing;
using LucidRAG.ImageCli.Services;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace LucidRAG.ImageCli.Commands;

/// <summary>
/// Command for finding and managing duplicate images.
/// </summary>
public static class DedupeCommand
{
    private static readonly Argument<string> DirectoryArg = new("directory") { Description = "Directory to scan for duplicates" };

    private static readonly Option<int> ThresholdOpt = new("--threshold", "-t") { Description = "Hamming distance threshold (0-64, lower = more strict)", DefaultValueFactory = _ => 5 };

    private static readonly Option<string> PatternOpt = new("--pattern", "-p") { Description = "Glob pattern for filtering files", DefaultValueFactory = _ => "**/*" };

    private static readonly Option<bool> RecursiveOpt = new("--recursive", "-r") { Description = "Scan subdirectories recursively", DefaultValueFactory = _ => true };

    private static readonly Option<DeduplicationAction> ActionOpt = new("--action", "-a") { Description = "Action to perform on duplicates", DefaultValueFactory = _ => DeduplicationAction.Report };

    private static readonly Option<string?> MoveToOpt = new("--move-to") { Description = "Directory to move duplicate files to (required for move action)" };

    private static readonly Option<bool> DryRunOpt = new("--dry-run") { Description = "Show what would be done without actually doing it", DefaultValueFactory = _ => false };

    private static readonly Option<GroupingStrategy> GroupByOpt = new("--group-by") { Description = "How to group similar images", DefaultValueFactory = _ => GroupingStrategy.Hash };

    public static Command Create()
    {
        var command = new Command("dedupe", "Find and manage duplicate images");
        command.Arguments.Add(DirectoryArg);
        command.Options.Add(ThresholdOpt);
        command.Options.Add(PatternOpt);
        command.Options.Add(RecursiveOpt);
        command.Options.Add(ActionOpt);
        command.Options.Add(MoveToOpt);
        command.Options.Add(DryRunOpt);
        command.Options.Add(GroupByOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var directory = parseResult.GetValue(DirectoryArg)!;
            var threshold = parseResult.GetValue(ThresholdOpt);
            var pattern = parseResult.GetValue(PatternOpt)!;
            var recursive = parseResult.GetValue(RecursiveOpt);
            var action = parseResult.GetValue(ActionOpt);
            var moveTo = parseResult.GetValue(MoveToOpt);
            var dryRun = parseResult.GetValue(DryRunOpt);
            var groupBy = parseResult.GetValue(GroupByOpt);

            // Validate directory
            if (!Directory.Exists(directory))
            {
                AnsiConsole.MarkupLine($"[red]✗ Error:[/] Directory not found: {Markup.Escape(directory)}");
                return 1;
            }

            // Validate action-specific requirements
            if (action == DeduplicationAction.Move && string.IsNullOrWhiteSpace(moveTo))
            {
                AnsiConsole.MarkupLine("[red]✗ Error:[/] --move-to is required when action is 'move'");
                return 1;
            }

            if (threshold < 0 || threshold > 64)
            {
                AnsiConsole.MarkupLine("[red]✗ Error:[/] Threshold must be between 0 and 64");
                return 1;
            }

            // Build service provider
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .Build();

            var services = Program.BuildServiceProvider(configuration);

            // Get services
            var dedupeService = services.GetRequiredService<DeduplicationService>();
            var batchProcessor = services.GetRequiredService<ImageBatchProcessor>();

            try
            {
                // Find all image files
                AnsiConsole.MarkupLine($"[cyan]ℹ[/] Scanning directory: {Markup.Escape(directory)}");

                var imageFiles = Directory.GetFiles(
                    directory,
                    "*.*",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(f => IsImageFile(f))
                    .ToList();

                if (imageFiles.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]⚠[/] No image files found");
                    return 0;
                }

                AnsiConsole.MarkupLine($"[dim]Found {imageFiles.Count} images to analyze[/]");
                AnsiConsole.WriteLine();

                // Find duplicates with progress
                DeduplicationResult? result = null;

                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .Columns(
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new RemainingTimeColumn(),
                        new SpinnerColumn())
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask("[cyan]Calculating perceptual hashes[/]");
                        task.MaxValue = imageFiles.Count;

                        var progress = new Progress<(int Processed, int Total)>(p =>
                        {
                            task.Value = p.Processed;
                        });

                        result = await dedupeService.FindDuplicatesAsync(
                            imageFiles,
                            threshold,
                            progress,
                            ct);
                    });

                if (result == null || result.Groups.Count == 0)
                {
                    AnsiConsole.MarkupLine("[green]✓[/] No duplicates found!");
                    return 0;
                }

                // Display results
                AnsiConsole.WriteLine();
                DisplayDuplicateGroups(result, dryRun);

                // Perform action if not just reporting
                if (action != DeduplicationAction.Report)
                {
                    if (!dryRun && !ConfirmAction(action, result))
                    {
                        AnsiConsole.MarkupLine("[yellow]⚠[/] Action cancelled by user");
                        return 0;
                    }

                    AnsiConsole.WriteLine();
                    await PerformActionOnGroups(dedupeService, result.Groups, action, moveTo, dryRun, ct);
                }

                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Error:[/] {Markup.Escape(ex.Message)}");
                AnsiConsole.WriteException(ex);
                return 1;
            }
        });

        return command;
    }

    private static void DisplayDuplicateGroups(DeduplicationResult result, bool dryRun)
    {
        var panel = new Panel(new Markup(
            $"[green]Found {result.Groups.Count} duplicate groups[/]\n" +
            $"[dim]Total duplicates: {result.TotalDuplicates}[/]\n" +
            $"[dim]Wasted space: {FormatBytes(result.WastedSpace)}[/]"))
        {
            Header = new PanelHeader("[cyan]Deduplication Summary[/]"),
            Border = BoxBorder.Rounded
        };
        panel.BorderStyle(Style.Parse("cyan"));

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        // Show top groups
        var topGroups = result.Groups.Take(20).ToList();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title($"[cyan]Duplicate Groups (showing top {topGroups.Count})[/]");

        table.AddColumn("Group");
        table.AddColumn("Files");
        table.AddColumn("Size Range");
        table.AddColumn("Similarity");
        table.AddColumn("Sample Files");

        for (int i = 0; i < topGroups.Count; i++)
        {
            var group = topGroups[i];
            var sizeRange = $"{FormatBytes(group.Images.Min(img => img.FileSize))} - " +
                          $"{FormatBytes(group.Images.Max(img => img.FileSize))}";

            var maxDistance = group.Images.Max(img => img.HammingDistance);
            var similarity = $"{(64 - maxDistance) * 100.0 / 64:F1}%";

            var sampleFiles = string.Join("\n",
                group.Images.Take(3).Select(img => Path.GetFileName(img.FilePath)));

            if (group.Images.Count > 3)
            {
                sampleFiles += $"\n[dim]... and {group.Images.Count - 3} more[/]";
            }

            table.AddRow(
                $"[yellow]{i + 1}[/]",
                group.Images.Count.ToString(),
                sizeRange,
                similarity,
                sampleFiles
            );
        }

        if (result.Groups.Count > 20)
        {
            table.Caption($"[dim]Showing first 20 of {result.Groups.Count} groups[/]");
        }

        AnsiConsole.Write(table);
    }

    private static bool ConfirmAction(DeduplicationAction action, DeduplicationResult result)
    {
        var message = action switch
        {
            DeduplicationAction.Move => $"Move {result.TotalDuplicates} duplicate files?",
            DeduplicationAction.Delete => $"[red]DELETE {result.TotalDuplicates} duplicate files? THIS CANNOT BE UNDONE![/]",
            _ => "Continue?"
        };

        return AnsiConsole.Confirm(message, defaultValue: false);
    }

    private static async Task PerformActionOnGroups(
        DeduplicationService service,
        List<DuplicateGroup> groups,
        DeduplicationAction action,
        string? moveToDirectory,
        bool dryRun,
        CancellationToken ct)
    {
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[cyan]{(dryRun ? "Simulating" : "Performing")} {action}[/]");
                task.MaxValue = groups.Count;

                foreach (var group in groups)
                {
                    var result = await service.PerformActionAsync(
                        group,
                        action,
                        moveToDirectory,
                        dryRun,
                        ct);

                    foreach (var detail in result.Details)
                    {
                        var color = dryRun ? "dim" : "green";
                        AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(detail)}[/]");
                    }

                    task.Increment(1);
                }
            });

        if (dryRun)
        {
            AnsiConsole.MarkupLine("\n[yellow]ℹ[/] This was a dry run. No files were actually modified.");
            AnsiConsole.MarkupLine("[dim]Remove --dry-run to perform the action.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[green]✓[/] Action completed successfully");
        }
    }

    private static bool IsImageFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".tiff" or ".tif";
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}

public enum GroupingStrategy
{
    Hash,
    Similarity
}

file static class ServiceProviderExtensions
{
    public static T GetRequiredService<T>(this IServiceProvider services) where T : notnull
    {
        return (T)(services.GetService(typeof(T)) ??
            throw new InvalidOperationException($"Service of type {typeof(T)} not found"));
    }
}
