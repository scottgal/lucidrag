using Spectre.Console;

namespace LucidRAG.Cli.Services;

/// <summary>
/// Rich progress rendering using Spectre.Console
/// </summary>
public class CliProgressRenderer
{
    public async Task<T> WithSpinnerAsync<T>(string message, Func<Task<T>> action)
    {
        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(message, async ctx => await action());
    }

    public async Task WithSpinnerAsync(string message, Func<Task> action)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(message, async ctx => await action());
    }

    public async Task<T> WithProgressAsync<T>(string description, Func<ProgressContext, Task<T>> action)
    {
        return await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(action);
    }

    public void WriteSuccess(string message)
    {
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");
    }

    public void WriteError(string message)
    {
        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(message)}");
    }

    public void WriteWarning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(message)}");
    }

    public void WriteInfo(string message)
    {
        AnsiConsole.MarkupLine($"[cyan]>[/] {Markup.Escape(message)}");
    }

    public void WriteTable<T>(string title, IEnumerable<T> items, params (string Header, Func<T, string> Selector)[] columns)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Title($"[cyan]{Markup.Escape(title)}[/]");

        foreach (var (header, _) in columns)
        {
            table.AddColumn(new TableColumn($"[cyan]{Markup.Escape(header)}[/]"));
        }

        foreach (var item in items)
        {
            var row = columns.Select(c => Markup.Escape(c.Selector(item))).ToArray();
            table.AddRow(row);
        }

        AnsiConsole.Write(table);
    }
}
