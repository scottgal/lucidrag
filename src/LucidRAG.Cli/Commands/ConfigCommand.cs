using System.CommandLine;
using System.Text.Json;
using Spectre.Console;

namespace LucidRAG.Cli.Commands;

/// <summary>
/// Configuration management
/// </summary>
public static class ConfigCommand
{
    private const string ConfigFileName = "lucidrag.json";

    public static Command Create()
    {
        var command = new Command("config", "Configuration management");

        command.Subcommands.Add(CreateShowCommand());
        command.Subcommands.Add(CreateInitCommand());
        command.Subcommands.Add(CreateSetCommand());
        command.Subcommands.Add(CreatePathCommand());

        return command;
    }

    private static Command CreateShowCommand()
    {
        var dataDirOpt = new Option<string?>("--data-dir") { Description = "Data directory" };

        var command = new Command("show", "Show current configuration") { dataDirOpt };

        command.SetAction(async (parseResult, ct) =>
        {
            var dataDir = Program.EnsureDataDirectory(parseResult.GetValue(dataDirOpt));
            var configPath = Path.Combine(dataDir, ConfigFileName);

            AnsiConsole.MarkupLine($"[cyan]Data directory:[/] {dataDir}");
            AnsiConsole.WriteLine();

            if (File.Exists(configPath))
            {
                var json = await File.ReadAllTextAsync(configPath, ct);
                var config = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Cyan1)
                    .Title("[cyan]Configuration[/]");

                table.AddColumn(new TableColumn("[cyan]Key[/]").LeftAligned());
                table.AddColumn(new TableColumn("[cyan]Value[/]").LeftAligned());

                if (config != null)
                {
                    foreach (var (key, value) in config)
                    {
                        table.AddRow(Markup.Escape(key), Markup.Escape(value?.ToString() ?? "null"));
                    }
                }

                AnsiConsole.Write(table);
            }
            else
            {
                AnsiConsole.MarkupLine("[dim]No configuration file found. Using defaults.[/]");
                AnsiConsole.MarkupLine("[dim]Use 'lucidrag config init' to create one.[/]");
            }

            return 0;
        });

        return command;
    }

    private static Command CreateInitCommand()
    {
        var dataDirOpt = new Option<string?>("--data-dir") { Description = "Data directory" };
        var forceOpt = new Option<bool>("--force", "-f") { Description = "Overwrite existing config" };

        var command = new Command("init", "Create default configuration file")
        {
            dataDirOpt,
            forceOpt
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var dataDir = Program.EnsureDataDirectory(parseResult.GetValue(dataDirOpt));
            var force = parseResult.GetValue(forceOpt);
            var configPath = Path.Combine(dataDir, ConfigFileName);

            if (File.Exists(configPath) && !force)
            {
                AnsiConsole.MarkupLine($"[yellow]Config file already exists: {configPath}[/]");
                AnsiConsole.MarkupLine("[dim]Use --force to overwrite[/]");
                return 1;
            }

            var defaultConfig = new Dictionary<string, object>
            {
                ["ollama_url"] = "http://localhost:11434",
                ["ollama_model"] = "llama3.2:3b",
                ["embedding_model"] = "AllMiniLmL6V2",
                ["vector_store"] = "DuckDB",
                ["serve_port"] = 5080
            };

            var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(configPath, json, ct);

            AnsiConsole.MarkupLine($"[green]✓[/] Created configuration file: {configPath}");
            return 0;
        });

        return command;
    }

    private static Command CreateSetCommand()
    {
        var keyArg = new Argument<string>("key") { Description = "Configuration key" };
        var valueArg = new Argument<string>("value") { Description = "Configuration value" };
        var dataDirOpt = new Option<string?>("--data-dir") { Description = "Data directory" };

        var command = new Command("set", "Set a configuration value");
        command.Arguments.Add(keyArg);
        command.Arguments.Add(valueArg);
        command.Options.Add(dataDirOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var key = parseResult.GetValue(keyArg)!;
            var value = parseResult.GetValue(valueArg)!;
            var dataDir = Program.EnsureDataDirectory(parseResult.GetValue(dataDirOpt));
            var configPath = Path.Combine(dataDir, ConfigFileName);

            Dictionary<string, object> config;

            if (File.Exists(configPath))
            {
                var json = await File.ReadAllTextAsync(configPath, ct);
                config = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
            }
            else
            {
                config = new Dictionary<string, object>();
            }

            // Try to parse as number or bool
            if (int.TryParse(value, out var intVal))
                config[key] = intVal;
            else if (bool.TryParse(value, out var boolVal))
                config[key] = boolVal;
            else
                config[key] = value;

            var newJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(configPath, newJson, ct);

            AnsiConsole.MarkupLine($"[green]✓[/] Set {Markup.Escape(key)} = {Markup.Escape(value)}");
            return 0;
        });

        return command;
    }

    private static Command CreatePathCommand()
    {
        var dataDirOpt = new Option<string?>("--data-dir") { Description = "Data directory" };

        var command = new Command("path", "Show data directory path") { dataDirOpt };

        command.SetAction((parseResult, ct) =>
        {
            var dataDir = Program.EnsureDataDirectory(parseResult.GetValue(dataDirOpt));
            Console.WriteLine(dataDir);
            return Task.FromResult(0);
        });

        return command;
    }
}
