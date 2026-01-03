using System.Threading.Channels;
using Spectre.Console;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Consumes progress updates from Core's ProgressChannel and displays them using Spectre.Console.
/// This bridges the library's channel-based progress API to CLI-specific rich terminal output.
/// </summary>
public class ProgressChannelConsumer
{
    private readonly bool _verbose;
    private readonly bool _useSpectre;

    public ProgressChannelConsumer(bool verbose = true, bool useSpectre = true)
    {
        _verbose = verbose;
        _useSpectre = useSpectre && Environment.UserInteractive && !Console.IsInputRedirected;
    }

    /// <summary>
    /// Run an async operation while consuming and displaying progress updates.
    /// </summary>
    public async Task<T> RunWithProgressAsync<T>(
        string title,
        Func<ChannelWriter<ProgressUpdate>, Task<T>> operation,
        CancellationToken ct = default)
    {
        var channel = ProgressChannel.CreateUnbounded();
        
        // Start the operation
        var operationTask = operation(channel.Writer);
        
        // Consume progress updates
        if (_useSpectre)
        {
            await ConsumeWithSpectreAsync(title, channel.Reader, operationTask, ct);
        }
        else
        {
            await ConsumeSimpleAsync(channel.Reader, operationTask, ct);
        }
        
        return await operationTask;
    }

    /// <summary>
    /// Consume progress using Spectre.Console Status display.
    /// </summary>
    private async Task ConsumeWithSpectreAsync<T>(
        string title,
        ChannelReader<ProgressUpdate> reader,
        Task<T> operationTask,
        CancellationToken ct)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(title, async ctx =>
            {
                try
                {
                    while (!operationTask.IsCompleted)
                    {
                        // Check for progress updates (non-blocking)
                        if (reader.TryRead(out var update))
                        {
                            DisplayUpdate(update, ctx);
                        }
                        else
                        {
                            // Small delay to avoid busy-waiting
                            await Task.Delay(50, ct);
                        }
                    }
                    
                    // Drain any remaining updates
                    while (reader.TryRead(out var update))
                    {
                        DisplayUpdate(update, ctx);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelled - just return
                }
            });
    }

    private void DisplayUpdate(ProgressUpdate update, StatusContext ctx)
    {
        var message = update.Type switch
        {
            ProgressType.Stage => $"[cyan]{Markup.Escape(update.Stage)}:[/] {Markup.Escape(update.Message)}",
            ProgressType.ItemProgress => $"[dim]{Markup.Escape(update.Stage)}:[/] {update.Current}/{update.Total} ({update.PercentComplete:F0}%)",
            ProgressType.LlmActivity => $"[yellow]LLM:[/] {Markup.Escape(update.Message)}",
            ProgressType.Info when _verbose => $"[dim]{Markup.Escape(update.Message)}[/]",
            ProgressType.Warning => $"[yellow]Warning:[/] {Markup.Escape(update.Message)}",
            ProgressType.Error => $"[red]Error:[/] {Markup.Escape(update.Message)}",
            ProgressType.Completed => $"[green]✓[/] {Markup.Escape(update.Message)} ({update.ElapsedMs}ms)",
            ProgressType.Download => $"[blue]Downloading:[/] {update.PercentComplete:F0}%",
            _ => null
        };

        if (message != null)
        {
            ctx.Status(message);
            
            // For important updates, also write to console
            if (update.Type is ProgressType.Warning or ProgressType.Error or ProgressType.Completed)
            {
                AnsiConsole.MarkupLine(message);
            }
        }
    }

    /// <summary>
    /// Consume progress with simple console output (non-interactive mode).
    /// </summary>
    private async Task ConsumeSimpleAsync<T>(
        ChannelReader<ProgressUpdate> reader,
        Task<T> operationTask,
        CancellationToken ct)
    {
        try
        {
            while (!operationTask.IsCompleted)
            {
                if (reader.TryRead(out var update))
                {
                    DisplayUpdateSimple(update);
                }
                else
                {
                    await Task.Delay(50, ct);
                }
            }
            
            // Drain remaining
            while (reader.TryRead(out var update))
            {
                DisplayUpdateSimple(update);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
    }

    private void DisplayUpdateSimple(ProgressUpdate update)
    {
        var message = update.Type switch
        {
            ProgressType.Stage => $"[{update.Stage}] {update.Message}",
            ProgressType.ItemProgress when _verbose => $"  {update.Current}/{update.Total} ({update.PercentComplete:F0}%)",
            ProgressType.LlmActivity when _verbose => $"  LLM: {update.Message}",
            ProgressType.Info when _verbose => $"  {update.Message}",
            ProgressType.Warning => $"[WARN] {update.Message}",
            ProgressType.Error => $"[ERROR] {update.Message}",
            ProgressType.Completed => $"[OK] {update.Message} ({update.ElapsedMs}ms)",
            ProgressType.Download when _verbose => $"  Download: {update.PercentComplete:F0}%",
            _ => null
        };

        if (message != null)
        {
            Console.WriteLine(message);
        }
    }

    /// <summary>
    /// Consume progress with a Spectre.Console progress bar (for longer operations).
    /// </summary>
    public async Task<T> RunWithProgressBarAsync<T>(
        string title,
        Func<ChannelWriter<ProgressUpdate>, Task<T>> operation,
        CancellationToken ct = default)
    {
        var channel = ProgressChannel.CreateUnbounded();
        
        if (!_useSpectre)
        {
            // Fall back to simple mode
            var opTask = operation(channel.Writer);
            await ConsumeSimpleAsync(channel.Reader, opTask, ct);
            return await opTask;
        }

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
                var mainTask = ctx.AddTask($"[cyan]{Markup.Escape(title)}[/]");
                mainTask.MaxValue = 100;
                
                var operationTask = operation(channel.Writer);
                
                try
                {
                    while (!operationTask.IsCompleted)
                    {
                        if (channel.Reader.TryRead(out var update))
                        {
                            UpdateProgressBar(mainTask, update);
                        }
                        else
                        {
                            await Task.Delay(50, ct);
                        }
                    }
                    
                    // Drain remaining
                    while (channel.Reader.TryRead(out var update))
                    {
                        UpdateProgressBar(mainTask, update);
                    }
                    
                    mainTask.Value = 100;
                    mainTask.Description = $"[green]✓ {Markup.Escape(title)} complete[/]";
                }
                catch (OperationCanceledException)
                {
                    mainTask.Description = $"[yellow]Cancelled: {Markup.Escape(title)}[/]";
                }
                catch (Exception ex)
                {
                    mainTask.Description = $"[red]Failed: {Markup.Escape(ex.Message)}[/]";
                    throw;
                }
                
                result = await operationTask;
            });
        
        return result;
    }

    private static void UpdateProgressBar(ProgressTask task, ProgressUpdate update)
    {
        task.Value = update.PercentComplete;
        
        var desc = update.Type switch
        {
            ProgressType.Stage => $"[cyan]{Markup.Escape(update.Stage)}:[/] {Markup.Escape(update.Message)}",
            ProgressType.ItemProgress => $"[dim]{update.Current}/{update.Total}[/] {Markup.Escape(update.Message)}",
            ProgressType.LlmActivity => $"[yellow]LLM:[/] {Markup.Escape(update.Message)}",
            ProgressType.Completed => $"[green]✓[/] {Markup.Escape(update.Message)}",
            _ => null
        };

        if (desc != null)
        {
            task.Description = desc;
        }
    }
}
