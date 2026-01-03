using Spectre.Console;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Progress service using Spectre.Console for beautiful terminal output
/// </summary>
public class SpectreProgressService : IProgressReporter
{
    private readonly bool _verbose;
    private ProgressTask? _currentTask;
    private string _currentStage = "";
    
    public SpectreProgressService(bool verbose = true)
    {
        _verbose = verbose;
    }

    public void ReportStage(string stage, float progress = 0)
    {
        _currentStage = stage;
        if (_currentTask != null)
        {
            _currentTask.Description = $"[cyan]{stage}[/]";
            _currentTask.Value = progress * 100;
        }
    }

    public void ReportLlmActivity(string activity)
    {
        if (_verbose)
        {
            AnsiConsole.MarkupLine($"  [dim]LLM:[/] [grey]{Markup.Escape(activity)}[/]");
        }
    }

    public void ReportLog(string message, LogLevel level = LogLevel.Info)
    {
        var color = level switch
        {
            LogLevel.Error => "red",
            LogLevel.Warning => "yellow",
            LogLevel.Success => "green",
            _ => "white"
        };
        
        if (_verbose)
        {
            AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(message)}[/]");
        }
    }

    public void ReportChunkProgress(int completed, int total)
    {
        if (_currentTask != null && total > 0)
        {
            _currentTask.Value = (double)completed / total * 100;
            _currentTask.Description = $"[cyan]{_currentStage}[/] [dim]({completed}/{total})[/]";
        }
    }

    /// <summary>
    /// Run a task with live progress display
    /// </summary>
    public async Task<T> RunWithProgressAsync<T>(string title, Func<SpectreProgressService, Task<T>> task)
    {
        T result = default!;
        
        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                _currentTask = ctx.AddTask($"[cyan]{Markup.Escape(title)}[/]");
                result = await task(this);
                _currentTask.Value = 100;
                _currentTask.StopTask();
            });
        
        return result;
    }

    /// <summary>
    /// Display a styled header panel
    /// </summary>
    public static void WriteHeader(string title, string? subtitle = null)
    {
        var panel = new Panel(
            new FigletText(title)
                .Centered()
                .Color(Color.Cyan1))
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Blue),
            Padding = new Padding(1, 0, 1, 0)
        };
        
        if (subtitle != null)
        {
            panel.Header = new PanelHeader($" {subtitle} ", Justify.Center);
        }
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Display document info in a panel
    /// </summary>
    public static void WriteDocumentInfo(string fileName, string mode, string model, string? focus = null)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .AddColumn(new TableColumn("[blue]Property[/]").Centered())
            .AddColumn(new TableColumn("[blue]Value[/]"));

        table.AddRow("[cyan]Document[/]", Markup.Escape(fileName));
        table.AddRow("[cyan]Mode[/]", $"[yellow]{mode}[/]");
        table.AddRow("[cyan]Model[/]", $"[green]{Markup.Escape(model)}[/]");
        
        if (!string.IsNullOrEmpty(focus))
        {
            table.AddRow("[cyan]Focus[/]", Markup.Escape(focus));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Display a status spinner while doing work.
    /// Falls back to simple execution if already in an interactive context.
    /// </summary>
    public static async Task<T> WithSpinnerAsync<T>(string message, Func<Task<T>> task)
    {
        // Skip spinner if already in a batch context to avoid nested interactive displays
        if (BatchContextTracker.IsInContext)
        {
            return await task();
        }
        
        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(message, async _ => await task());
    }

    /// <summary>
    /// Display the summary result in a nice panel
    /// </summary>
    public static void WriteSummaryPanel(string summary, string title = "Summary")
    {
        var panel = new Panel(Markup.Escape(summary))
        {
            Header = new PanelHeader($" {title} ", Justify.Center),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Green),
            Padding = new Padding(2, 1)
        };
        
        AnsiConsole.Write(panel);
    }

    /// <summary>
    /// Display topics in a tree structure
    /// </summary>
    public static void WriteTopicsTree(IEnumerable<(string Topic, string Summary)> topics)
    {
        var tree = new Tree("[blue]Topics[/]")
            .Style(Style.Parse("cyan"));

        foreach (var (topic, summary) in topics)
        {
            var node = tree.AddNode($"[yellow]{Markup.Escape(topic)}[/]");
            // Truncate long summaries
            var truncated = summary.Length > 200 ? summary[..200] + "..." : summary;
            node.AddNode($"[dim]{Markup.Escape(truncated)}[/]");
        }

        AnsiConsole.Write(tree);
        AnsiConsole.WriteLine();
    }
    
    /// <summary>
    /// Display extracted entities (characters, locations, events, etc.)
    /// </summary>
    public static void WriteEntities(Models.ExtractedEntities? entities)
    {
        if (entities == null || !entities.HasAny)
            return;
        
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Title("[cyan]Extracted Entities[/]");
        
        table.AddColumn(new TableColumn("[blue]Type[/]").Centered());
        table.AddColumn(new TableColumn("[blue]Values[/]").LeftAligned());
        
        if (entities.Characters.Count > 0)
        {
            var chars = string.Join(", ", entities.Characters.Take(12));
            if (entities.Characters.Count > 12)
                chars += $", [dim]+{entities.Characters.Count - 12} more[/]";
            table.AddRow("[yellow]Characters[/]", Markup.Escape(chars));
        }
        
        if (entities.Locations.Count > 0)
        {
            var locs = string.Join(", ", entities.Locations.Take(8));
            if (entities.Locations.Count > 8)
                locs += $", [dim]+{entities.Locations.Count - 8} more[/]";
            table.AddRow("[green]Locations[/]", Markup.Escape(locs));
        }
        
        if (entities.Events.Count > 0)
        {
            var evts = string.Join(", ", entities.Events.Take(6));
            if (entities.Events.Count > 6)
                evts += $", [dim]+{entities.Events.Count - 6} more[/]";
            table.AddRow("[magenta]Key Events[/]", Markup.Escape(evts));
        }
        
        if (entities.Organizations.Count > 0)
        {
            var orgs = string.Join(", ", entities.Organizations.Take(6));
            if (entities.Organizations.Count > 6)
                orgs += $", [dim]+{entities.Organizations.Count - 6} more[/]";
            table.AddRow("[orange1]Organizations[/]", Markup.Escape(orgs));
        }
        
        if (entities.Dates.Count > 0)
        {
            var dates = string.Join(", ", entities.Dates.Take(6));
            if (entities.Dates.Count > 6)
                dates += $", [dim]+{entities.Dates.Count - 6} more[/]";
            table.AddRow("[aqua]Dates[/]", Markup.Escape(dates));
        }
        
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Display batch progress
    /// </summary>
    public static void WriteBatchProgress(int current, int total, string fileName, bool success)
    {
        var status = success ? "[green]✓[/]" : "[red]✗[/]";
        var percent = (double)current / total * 100;
        
        AnsiConsole.MarkupLine(
            $"{status} [dim]({current}/{total})[/] [{(success ? "white" : "red")}]{Markup.Escape(fileName)}[/] " +
            $"[dim]{percent:F0}%[/]");
    }

    /// <summary>
    /// Display completion message
    /// </summary>
    public static void WriteCompletion(TimeSpan elapsed, bool success = true)
    {
        var rule = new Rule(success 
            ? $"[green]Completed in {elapsed.TotalSeconds:F1}s[/]" 
            : $"[red]Failed after {elapsed.TotalSeconds:F1}s[/]")
        {
            Style = Style.Parse(success ? "green" : "red")
        };
        
        AnsiConsole.Write(rule);
    }

    /// <summary>
    /// Display an error
    /// </summary>
    public static void WriteError(string message, Exception? ex = null)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
        
        if (ex != null)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
        }
    }

    /// <summary>
    /// Display a warning
    /// </summary>
    public static void WriteWarning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(message)}");
    }

    /// <summary>
    /// Display processing stages
    /// </summary>
    public static void WriteStage(string stage)
    {
        AnsiConsole.MarkupLine($"[blue]>>>[/] [cyan]{Markup.Escape(stage)}[/]");
    }

    /// <summary>
    /// Run document conversion with real-time progress from DoclingClient.
    /// If already inside an interactive context (e.g., batch processing), falls back to
    /// simple console output to avoid Spectre.Console nested progress bar conflicts.
    /// </summary>
    public static async Task<string> RunConversionWithProgressAsync(
        DoclingClient docling,
        string filePath,
        string description)
    {
        // If we're already inside a Spectre progress context (e.g., batch mode),
        // use a simple non-interactive fallback to avoid "concurrent interactive functions" error
        if (BatchContextTracker.IsInContext)
        {
            return await RunConversionWithoutProgressAsync(docling, filePath, description);
        }
        
        string result = "";
        
        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(true)  // Clear when done to avoid stale bars
            .HideCompleted(true)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[cyan]{Markup.Escape(description)}[/]");
                task.MaxValue = 100;
                
                // Wire up the progress callback
                docling.OnProgress = progress =>
                {
                    // Update the progress bar percentage
                    task.Value = progress.Percent;
                    
                    // Update description with wave info
                    var desc = progress.TotalWaves > 1
                        ? $"[cyan]Wave {progress.CurrentWave}/{progress.TotalWaves}:[/] {Markup.Escape(progress.Status)}"
                        : $"[cyan]{Markup.Escape(progress.Status)}[/]";
                    
                    task.Description = desc;
                };
                
                try
                {
                    result = await docling.ConvertAsync(filePath);
                    task.Value = 100;
                    task.Description = "[green]Conversion complete[/]";
                }
                finally
                {
                    // Clear the callback
                    docling.OnProgress = null;
                    task.StopTask();
                }
            });
        
        return result;
    }
    
    /// <summary>
    /// Run document conversion without interactive progress display.
    /// Used when already inside a batch processing context.
    /// </summary>
    private static async Task<string> RunConversionWithoutProgressAsync(
        DoclingClient docling,
        string filePath,
        string description)
    {
        // Simple console output - no Spectre progress bar
        var lastPercent = -1;
        
        docling.OnProgress = progress =>
        {
            // Only output on significant progress changes to avoid flooding
            var currentPercent = (int)(progress.Percent / 10) * 10;
            if (currentPercent > lastPercent)
            {
                lastPercent = currentPercent;
                // Don't write anything - batch mode handles its own progress
            }
        };
        
        try
        {
            var result = await docling.ConvertAsync(filePath);
            return result;
        }
        finally
        {
            docling.OnProgress = null;
        }
    }
    
    /// <summary>
    /// Run document conversion inside an existing progress context (avoids nested progress bars)
    /// </summary>
    public static async Task<string> RunConversionAsync(
        DoclingClient docling,
        string filePath,
        Action<double, string> progressUpdate)
    {
        // Wire up the progress callback to the provided updater
        docling.OnProgress = progress =>
        {
            var status = progress.TotalWaves > 1
                ? $"Wave {progress.CurrentWave}/{progress.TotalWaves}: {progress.Status}"
                : progress.Status;
            
            progressUpdate(progress.Percent, status);
        };
        
        try
        {
            var result = await docling.ConvertAsync(filePath);
            progressUpdate(100, "Conversion complete");
            return result;
        }
        finally
        {
            docling.OnProgress = null;
        }
    }

    /// <summary>
    /// Run a multi-stage operation with live progress updates.
    /// The callback receives a progress reporter that can update description and percentage.
    /// </summary>
    public static async Task<T> RunWithLiveProgressAsync<T>(
        string initialDescription,
        Func<Action<double, string>, Task<T>> operation)
    {
        T result = default!;
        
        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[cyan]{Markup.Escape(initialDescription)}[/]");
                task.MaxValue = 100;
                
                // Progress update callback
                void UpdateProgress(double percent, string status)
                {
                    task.Value = percent;
                    task.Description = $"[cyan]{Markup.Escape(status)}[/]";
                }
                
                result = await operation(UpdateProgress);
                task.Value = 100;
                task.StopTask();
            });
        
        return result;
    }
}
