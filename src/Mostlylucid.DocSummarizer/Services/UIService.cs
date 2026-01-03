using Mostlylucid.DocSummarizer.Models;
using Spectre.Console;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Unified UI service interface that combines progress reporting and console output.
/// Provides a single consistent API for all user-facing output.
/// </summary>
public interface IUIService
{
    /// <summary>Whether to show verbose output</summary>
    bool Verbose { get; set; }
    
    /// <summary>Whether we're currently in a batch/nested context</summary>
    bool IsInBatchContext { get; }
    
    // === Header/Structure ===
    
    /// <summary>Display application header with title</summary>
    void WriteHeader(string title, string? subtitle = null);
    
    /// <summary>Display document info panel</summary>
    void WriteDocumentInfo(string document, string mode, string model, string? focus = null);
    
    /// <summary>Display a section divider</summary>
    void WriteDivider(string? title = null);
    
    // === Messages ===
    
    /// <summary>Display an info message</summary>
    void Info(string message);
    
    /// <summary>Display a success message</summary>
    void Success(string message);
    
    /// <summary>Display a warning message</summary>
    void Warning(string message);
    
    /// <summary>Display an error message</summary>
    void Error(string message, Exception? ex = null);
    
    // === Results ===
    
    /// <summary>Display summary result in a panel</summary>
    void WriteSummary(string summary, string title = "Summary");
    
    /// <summary>Display extracted entities</summary>
    void WriteEntities(ExtractedEntities? entities);
    
    /// <summary>Display topics tree</summary>
    void WriteTopics(IEnumerable<(string Topic, string Summary)> topics);
    
    /// <summary>Display completion status</summary>
    void WriteCompletion(TimeSpan elapsed, bool success = true);
    
    // === Progress ===
    
    /// <summary>Execute an operation with spinner</summary>
    Task<T> WithSpinnerAsync<T>(string message, Func<Task<T>> operation);
    
    /// <summary>Execute an operation with status updates</summary>
    Task<T> WithStatusAsync<T>(string status, Func<Task<T>> operation);
    
    /// <summary>Enter batch context (prevents nested progress bars)</summary>
    IDisposable EnterBatchContext();
    
    /// <summary>Display batch item progress</summary>
    void WriteBatchProgress(int current, int total, string fileName, bool success);
    
    // === Conversion Progress ===
    
    /// <summary>Run document conversion with real-time progress</summary>
    Task<string> RunConversionAsync(DoclingClient docling, string filePath, string description);
}

/// <summary>
/// Unified UI service using Spectre.Console for rich terminal output.
/// Automatically falls back to simple console output when in batch context.
/// </summary>
public class UIService : IUIService
{
    private bool _verbose;
    [ThreadStatic] private static bool _isInBatchContext;
    
    public UIService(bool verbose = true)
    {
        _verbose = verbose;
    }
    
    public bool Verbose
    {
        get => _verbose;
        set => _verbose = value;
    }
    
    public bool IsInBatchContext => _isInBatchContext;
    
    #region Header/Structure
    
    public void WriteHeader(string title, string? subtitle = null)
    {
        if (!_verbose && subtitle == null) return;
        
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 60));
            Console.WriteLine($"  {title}");
            if (subtitle != null)
                Console.WriteLine($"  {subtitle}");
            Console.WriteLine(new string('=', 60));
            return;
        }
        
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
            panel.Header = new PanelHeader($" {subtitle} ", Justify.Center);
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }
    
    public void WriteDocumentInfo(string document, string mode, string model, string? focus = null)
    {
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            Console.WriteLine($"  Document: {document}");
            Console.WriteLine($"  Mode: {mode}");
            Console.WriteLine($"  Model: {model}");
            if (!string.IsNullOrEmpty(focus))
                Console.WriteLine($"  Focus: {focus}");
            Console.WriteLine();
            return;
        }
        
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .AddColumn(new TableColumn("[blue]Property[/]").Centered())
            .AddColumn(new TableColumn("[blue]Value[/]"));
        
        table.AddRow("[cyan]Document[/]", Markup.Escape(document));
        table.AddRow("[cyan]Mode[/]", $"[yellow]{mode}[/]");
        table.AddRow("[cyan]Model[/]", $"[green]{Markup.Escape(model)}[/]");
        
        if (!string.IsNullOrEmpty(focus))
            table.AddRow("[cyan]Focus[/]", Markup.Escape(focus));
        
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }
    
    public void WriteDivider(string? title = null)
    {
        if (!_verbose) return;
        
        Console.WriteLine();
        if (title != null)
            Console.WriteLine($"--- {title} ---");
        else
            Console.WriteLine(new string('-', 40));
    }
    
    #endregion
    
    #region Messages
    
    public void Info(string message)
    {
        if (!_verbose) return;
        
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            Console.WriteLine($"[INFO] {message}");
            return;
        }
        
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(message)}[/]");
    }
    
    public void Success(string message)
    {
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            Console.WriteLine($"[OK] {message}");
            return;
        }
        
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");
    }
    
    public void Warning(string message)
    {
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            Console.WriteLine($"[WARN] {message}");
            return;
        }
        
        AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(message)}");
    }
    
    public void Error(string message, Exception? ex = null)
    {
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            Console.WriteLine($"[ERROR] {message}");
            if (ex != null && _verbose)
                Console.WriteLine(ex.ToString());
            return;
        }
        
        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
        if (ex != null)
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
    }
    
    #endregion
    
    #region Results
    
    public void WriteSummary(string summary, string title = "Summary")
    {
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 60));
            Console.WriteLine($"  {title}");
            Console.WriteLine(new string('=', 60));
            Console.WriteLine(summary);
            Console.WriteLine(new string('=', 60));
            Console.WriteLine();
            return;
        }
        
        var panel = new Panel(Markup.Escape(summary))
        {
            Header = new PanelHeader($" {title} ", Justify.Center),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Green),
            Padding = new Padding(2, 1)
        };
        
        AnsiConsole.Write(panel);
    }
    
    public void WriteEntities(ExtractedEntities? entities)
    {
        if (entities == null || !entities.HasAny)
            return;
        
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            Console.WriteLine();
            Console.WriteLine("--- Extracted Entities ---");
            if (entities.Characters.Count > 0)
                Console.WriteLine($"  Characters: {string.Join(", ", entities.Characters.Take(10))}");
            if (entities.Locations.Count > 0)
                Console.WriteLine($"  Locations: {string.Join(", ", entities.Locations.Take(8))}");
            if (entities.Events.Count > 0)
                Console.WriteLine($"  Events: {string.Join(", ", entities.Events.Take(6))}");
            if (entities.Organizations.Count > 0)
                Console.WriteLine($"  Organizations: {string.Join(", ", entities.Organizations.Take(6))}");
            if (entities.Dates.Count > 0)
                Console.WriteLine($"  Dates: {string.Join(", ", entities.Dates.Take(6))}");
            return;
        }
        
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
    
    public void WriteTopics(IEnumerable<(string Topic, string Summary)> topics)
    {
        var topicsList = topics.ToList();
        if (topicsList.Count == 0) return;
        
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            Console.WriteLine();
            Console.WriteLine("--- Topics ---");
            foreach (var (topic, summary) in topicsList)
            {
                Console.WriteLine($"  * {topic}");
                var truncated = summary.Length > 100 ? summary[..100] + "..." : summary;
                Console.WriteLine($"    {truncated}");
            }
            return;
        }
        
        var tree = new Tree("[blue]Topics[/]")
            .Style(Style.Parse("cyan"));
        
        foreach (var (topic, summary) in topicsList)
        {
            var node = tree.AddNode($"[yellow]{Markup.Escape(topic)}[/]");
            var truncated = summary.Length > 200 ? summary[..200] + "..." : summary;
            node.AddNode($"[dim]{Markup.Escape(truncated)}[/]");
        }
        
        AnsiConsole.Write(tree);
        AnsiConsole.WriteLine();
    }
    
    public void WriteCompletion(TimeSpan elapsed, bool success = true)
    {
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            var status = success ? "Completed" : "Failed";
            Console.WriteLine($"[{status}] {elapsed.TotalSeconds:F1}s");
            return;
        }
        
        var rule = new Rule(success 
            ? $"[green]Completed in {elapsed.TotalSeconds:F1}s[/]" 
            : $"[red]Failed after {elapsed.TotalSeconds:F1}s[/]")
        {
            Style = Style.Parse(success ? "green" : "red")
        };
        
        AnsiConsole.Write(rule);
    }
    
    #endregion
    
    #region Progress
    
    public async Task<T> WithSpinnerAsync<T>(string message, Func<Task<T>> operation)
    {
        // Skip spinner in batch context to avoid nested interactive displays
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            if (_verbose)
                Console.WriteLine($"  {message}");
            return await operation();
        }
        
        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(message, async _ => await operation());
    }
    
    public async Task<T> WithStatusAsync<T>(string status, Func<Task<T>> operation)
    {
        if (_verbose)
            Console.WriteLine(status);
        return await operation();
    }
    
    public IDisposable EnterBatchContext()
    {
        _isInBatchContext = true;
        return new BatchContextGuard();
    }
    
    public void WriteBatchProgress(int current, int total, string fileName, bool success)
    {
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            var status = success ? "OK" : "FAIL";
            var percent = total > 0 ? (current * 100) / total : 0;
            Console.WriteLine($"  [{status}] ({current}/{total}) {fileName} [{percent}%]");
            return;
        }
        
        var statusMark = success ? "[green]OK[/]" : "[red]FAIL[/]";
        var percent2 = (double)current / total * 100;
        
        AnsiConsole.MarkupLine(
            $"{statusMark} [dim]({current}/{total})[/] [{(success ? "white" : "red")}]{Markup.Escape(fileName)}[/] " +
            $"[dim]{percent2:F0}%[/]");
    }
    
    #endregion
    
    #region Conversion
    
    public async Task<string> RunConversionAsync(DoclingClient docling, string filePath, string description)
    {
        // If we're in batch context, use simple output
        if (IsInBatchContext || !Environment.UserInteractive)
        {
            return await RunConversionSimpleAsync(docling, filePath, description);
        }
        
        string result = "";
        
        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(true)
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
                
                docling.OnProgress = progress =>
                {
                    task.Value = progress.Percent;
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
                    docling.OnProgress = null;
                    task.StopTask();
                }
            });
        
        return result;
    }
    
    private async Task<string> RunConversionSimpleAsync(DoclingClient docling, string filePath, string description)
    {
        var lastPercent = -1;
        
        docling.OnProgress = progress =>
        {
            var currentPercent = (int)(progress.Percent / 20) * 20;
            if (currentPercent > lastPercent && _verbose)
            {
                lastPercent = currentPercent;
                Console.WriteLine($"  Converting: {currentPercent}%");
            }
        };
        
        try
        {
            return await docling.ConvertAsync(filePath);
        }
        finally
        {
            docling.OnProgress = null;
        }
    }
    
    #endregion
    
    private class BatchContextGuard : IDisposable
    {
        public void Dispose() => _isInBatchContext = false;
    }
}

/// <summary>
/// Adapter to make UIService work with legacy IProgressReporter interface
/// </summary>
public class UIServiceProgressAdapter : IProgressReporter
{
    private readonly IUIService _ui;
    private string _currentStage = "";
    
    public UIServiceProgressAdapter(IUIService ui)
    {
        _ui = ui;
    }
    
    public void ReportStage(string stage, float progress = 0)
    {
        _currentStage = stage;
        _ui.Info($"{stage} ({progress:P0})");
    }
    
    public void ReportLlmActivity(string activity)
    {
        _ui.Info($"LLM: {activity}");
    }
    
    public void ReportLog(string message, LogLevel level = LogLevel.Info)
    {
        switch (level)
        {
            case LogLevel.Error:
                _ui.Error(message);
                break;
            case LogLevel.Warning:
                _ui.Warning(message);
                break;
            case LogLevel.Success:
                _ui.Success(message);
                break;
            default:
                _ui.Info(message);
                break;
        }
    }
    
    public void ReportChunkProgress(int completed, int total)
    {
        _ui.Info($"Chunks: {completed}/{total}");
    }
}
