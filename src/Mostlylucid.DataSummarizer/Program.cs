using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using Mostlylucid.DataSummarizer.Configuration;
using Mostlylucid.DataSummarizer.Models;
using Mostlylucid.DataSummarizer.Services;
using Spectre.Console;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();

var settings = new DataSummarizerSettings();
configuration.GetSection("DataSummarizer").Bind(settings);

// Drag-and-drop support: if first arg is a file path (not a flag), inject -f
if (args.Length > 0 && !args[0].StartsWith("-") && File.Exists(args[0]))
{
    args = new[] { "-f", args[0] }.Concat(args.Skip(1)).ToArray();
}

// Options - using new System.CommandLine 2.0.1 API
var fileOption = new Option<string?>("--file", "-f") { Description = "Path to data file (CSV, Excel, Parquet, JSON, SQLite)" };
var sheetOption = new Option<string?>("--sheet", "-s") { Description = "Sheet name for Excel files" };
var tableOption = new Option<string?>("--table", "-t") { Description = "Table name for SQLite databases (required for .sqlite/.db files)" };
var modelOption = new Option<string?>("--model", "-m") { Description = "Ollama model for LLM insights", DefaultValueFactory = _ => "qwen2.5-coder:7b" };
var noLlmOption = new Option<bool>("--no-llm") { Description = "Skip LLM insights (stats only)" };
var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Verbose output" };
var outputOption = new Option<string?>("--output", "-o") { Description = "Output file path (default: console)" };
var queryOption = new Option<string?>("--query", "-q") { Description = "Ask a specific question about the data" };
var onnxOption = new Option<string?>("--onnx", "--onnx-sentinel") { Description = "Optional ONNX sentinel model path for column scoring" };
var onnxEnabledOption = new Option<bool?>("--onnx-enabled") { Description = "Enable/disable ONNX classifier for PII detection (default: from appsettings.json)" };
var onnxModelOption = new Option<string?>("--onnx-model") { Description = "ONNX embedding model: AllMiniLmL6V2, BgeSmallEnV15, GteSmall, MultiQaMiniLm, ParaphraseMiniLmL3" };
var onnxGpuOption = new Option<bool>("--onnx-gpu") { Description = "Force GPU acceleration for ONNX (DirectML/CUDA)" };
var onnxCpuOption = new Option<bool>("--onnx-cpu") { Description = "Force CPU-only execution for ONNX" };
var onnxModelDirOption = new Option<string?>("--onnx-model-dir") { Description = "Directory for ONNX models (default: ./models)" };

// PII display options (privacy-safe by default)
var showPiiOption = new Option<bool>("--show-pii") { Description = "Show actual PII values in output (WARNING: disables privacy protection)" };
var showPiiTypeOption = new Option<string[]?>("--show-pii-type") { Description = "Show specific PII types: email, phone, ssn, name, address, etc.", AllowMultipleArgumentsPerToken = true };
var hidePiiLabelsOption = new Option<bool>("--hide-pii-labels") { Description = "Hide PII type labels like [EMAIL] when redacting" };

var ingestDirOption = new Option<string?>("--ingest-dir") { Description = "Ingest all supported files in a directory into the registry" };
var ingestFilesOption = new Option<string[]?>("--ingest-files") { Description = "Ingest a comma-separated list of files into the registry", AllowMultipleArgumentsPerToken = true };
var registryQueryOption = new Option<string?>("--registry-query") { Description = "Ask a question across all ingested data (vector search)" };
var vectorDbOption = new Option<string?>("--vector-db") { Description = "Path to persistent DuckDB vector store", DefaultValueFactory = _ => ".datasummarizer.vss.duckdb" };
var sessionIdOption = new Option<string?>("--session-id") { Description = "Conversation/session id for context memory (auto-generates if omitted)" };
var synthPathOption = new Option<string?>("--synthesize-to") { Description = "Write a synthetic CSV that matches the profiled shape" };
var synthRowsOption = new Option<int>("--synthesize-rows") { Description = "Rows to generate when synthesizing", DefaultValueFactory = _ => 1000 };
var columnsOption = new Option<string[]?>("--columns") { Description = "Specific columns to analyze (comma-separated)", AllowMultipleArgumentsPerToken = true };
var excludeColumnsOption = new Option<string[]?>("--exclude-columns") { Description = "Columns to exclude from analysis", AllowMultipleArgumentsPerToken = true };
var maxColumnsOption = new Option<int?>("--max-columns") { Description = "Maximum columns to analyze (0=unlimited). Selects most interesting for wide tables." };
var fastModeOption = new Option<bool>("--fast") { Description = "Quick stats only - no LLM, no correlations, no expensive analysis" };
var skipCorrelationsOption = new Option<bool>("--skip-correlations") { Description = "Skip correlation analysis (faster for wide tables)" };
var ignoreErrorsOption = new Option<bool>("--ignore-errors") { Description = "Ignore CSV parsing errors (malformed rows)" };
var targetOption = new Option<string?>("--target") { Description = "Target column for supervised analysis (e.g. churn flag)" };
var markdownOutputOption = new Option<string?>("--markdown-output") { Description = "Write markdown report to this path (overrides defaults)" };
var noReportOption = new Option<bool>("--no-report") { Description = "Skip markdown report generation" };
var focusQuestionOption = new Option<string[]?>("--focus-question") { Description = "Focus question(s) for the LLM-grounded report", AllowMultipleArgumentsPerToken = true };
var interactiveOption = new Option<bool>("--interactive", "-i") { Description = "Interactive conversation mode - ask multiple questions about your data" };
var outputProfileOption = new Option<string?>("--output-profile", "-p") { Description = "Output profile: Default, Tool, Brief, Detailed, Markdown" };

// Profile store options
var storeProfileOption = new Option<bool>("--store") { Description = "Store profile for drift tracking (auto-detect similar profiles)" };
var compareToOption = new Option<string?>("--compare-to") { Description = "Profile ID to compare against (for drift detection)" };
var autoDriftOption = new Option<bool>("--auto-drift") { Description = "Auto-detect baseline and show drift (default: true for tool mode)" };
var noStoreOption = new Option<bool>("--no-store") { Description = "Don't store profile or check for drift" };
var storePathOption = new Option<string?>("--store-path") { Description = "Custom profile store directory" };

// Synth command options
var synthProfileOption = new Option<string>("--profile") { Description = "Profile JSON produced by 'profile' command", Required = true };
var synthSourceOption = new Option<string>("--source") { Description = "Source file or glob", Required = true };
var synthTargetOption = new Option<string>("--target") { Description = "Target file or glob", Required = true };

// Export format options (for tool command)
var formatOption = new Option<string?>("--format") { Description = "Output format: json (default), markdown, html" };

// Constraint validation options
var constraintFileOption = new Option<string?>("--constraints") { Description = "Path to constraint suite JSON file" };
var generateConstraintsOption = new Option<bool>("--generate-constraints") { Description = "Auto-generate constraints from the profile" };
var strictValidationOption = new Option<bool>("--strict") { Description = "Fail if any constraint violations found" };

// Segment comparison options
var segmentAOption = new Option<string?>("--segment-a") { Description = "First profile ID or file path for segment comparison" };
var segmentBOption = new Option<string?>("--segment-b") { Description = "Second profile ID or file path for segment comparison" };
var segmentNameAOption = new Option<string?>("--name-a") { Description = "Display name for segment A" };
var segmentNameBOption = new Option<string?>("--name-b") { Description = "Display name for segment B" };

// Subcommands
var profileCmd = new Command("profile", "Profile one or more files and write profile JSON");
profileCmd.Options.Add(fileOption);
profileCmd.Options.Add(ingestFilesOption);
profileCmd.Options.Add(ingestDirOption);
profileCmd.Options.Add(outputOption);
profileCmd.Options.Add(verboseOption);
profileCmd.Options.Add(noLlmOption);
profileCmd.Options.Add(modelOption);
profileCmd.Options.Add(onnxOption);
profileCmd.Options.Add(vectorDbOption);
profileCmd.Options.Add(sessionIdOption);

var synthCmd = new Command("synth", "Synthesize data from a saved profile (JSON)");
synthCmd.Options.Add(synthProfileOption);
synthCmd.Options.Add(synthPathOption);
synthCmd.Options.Add(synthRowsOption);
synthCmd.Options.Add(verboseOption);

var validateCmd = new Command("validate", "Compare two datasets (or dataset vs synth) and report deltas, or validate against constraints");
validateCmd.Options.Add(synthSourceOption);
validateCmd.Options.Add(synthTargetOption);
validateCmd.Options.Add(outputOption);
validateCmd.Options.Add(verboseOption);
validateCmd.Options.Add(modelOption);
validateCmd.Options.Add(noLlmOption);
validateCmd.Options.Add(vectorDbOption);
validateCmd.Options.Add(sessionIdOption);
validateCmd.Options.Add(constraintFileOption);
validateCmd.Options.Add(generateConstraintsOption);
validateCmd.Options.Add(strictValidationOption);
validateCmd.Options.Add(formatOption);

// Segment comparison command
var segmentCmd = new Command("segment", "Compare two data segments or stored profiles");
segmentCmd.Options.Add(segmentAOption);
segmentCmd.Options.Add(segmentBOption);
segmentCmd.Options.Add(segmentNameAOption);
segmentCmd.Options.Add(segmentNameBOption);
segmentCmd.Options.Add(outputOption);
segmentCmd.Options.Add(formatOption);
segmentCmd.Options.Add(storePathOption);

// SQLite export command
var toSqliteCmd = new Command("to-sqlite", "Export data to SQLite database with smart schema and indexes");
var sqliteOutputOption = new Option<string>("--output", "-o") { Description = "Output SQLite file path", Required = true };
var sqliteTableOption = new Option<string?>("--table", "-t") { Description = "Table name (default: derived from filename)" };
var sqliteNoIndexOption = new Option<bool>("--no-indexes") { Description = "Skip index creation" };
var sqliteOverwriteOption = new Option<bool>("--overwrite") { Description = "Overwrite existing SQLite file" };
toSqliteCmd.Options.Add(fileOption);
toSqliteCmd.Options.Add(sqliteOutputOption);
toSqliteCmd.Options.Add(sqliteTableOption);
toSqliteCmd.Options.Add(sqliteNoIndexOption);
toSqliteCmd.Options.Add(sqliteOverwriteOption);
toSqliteCmd.Options.Add(verboseOption);

// Markdown table conversion command
var convertMdCmd = new Command("convert-markdown", "Convert markdown tables to CSV for profiling");
var mdInputOption = new Option<string>("--input", "-i") { Description = "Markdown file path", Required = true };
var mdOutputDirOption = new Option<string?>("--output-dir", "-d") { Description = "Output directory for CSV files (default: ./converted_tables)" };
var mdListOnlyOption = new Option<bool>("--list-only") { Description = "List detected tables without converting" };
convertMdCmd.Options.Add(mdInputOption);
convertMdCmd.Options.Add(mdOutputDirOption);
convertMdCmd.Options.Add(mdListOnlyOption);
convertMdCmd.Options.Add(verboseOption);

// Intelligent search command
var searchCmd = new Command("search", "Search data with intelligent strategy detection or natural language queries");
var searchTermArg = new Argument<string>("query") { Description = "Search term or natural language query (e.g., 'dave' or 'show me ages of people named dave')" };
var searchColumnOption = new Option<string?>("--column", "-c") { Description = "Specific column to search (optional)" };
var searchLimitOption = new Option<int>("--limit", "-n") { Description = "Maximum results to return", DefaultValueFactory = _ => 100 };
var searchJsonOption = new Option<bool>("--json") { Description = "Output results as JSON" };
searchCmd.Arguments.Add(searchTermArg);
searchCmd.Options.Add(fileOption);
searchCmd.Options.Add(tableOption);
searchCmd.Options.Add(searchColumnOption);
searchCmd.Options.Add(searchLimitOption);
searchCmd.Options.Add(searchJsonOption);
searchCmd.Options.Add(verboseOption);

var toolCmd = new Command("tool", "Profile data and output JSON for LLM tool integration");
toolCmd.Options.Add(fileOption);
toolCmd.Options.Add(sheetOption);
toolCmd.Options.Add(targetOption);
toolCmd.Options.Add(columnsOption);
toolCmd.Options.Add(excludeColumnsOption);
toolCmd.Options.Add(maxColumnsOption);
toolCmd.Options.Add(fastModeOption);
toolCmd.Options.Add(skipCorrelationsOption);
toolCmd.Options.Add(ignoreErrorsOption);

// Tool-specific options for fast/cached operation
var cacheOption = new Option<bool>("--cache") { Description = "Use cached profile if file unchanged (xxHash64 check)" };
var quickOption = new Option<bool>("--quick", "-q") { Description = "Quick mode: basic stats only, no patterns/correlations (fastest)" };
var compactOption = new Option<bool>("--compact") { Description = "Compact output: omit null fields and empty arrays" };
toolCmd.Options.Add(cacheOption);
toolCmd.Options.Add(storeProfileOption); // --store
toolCmd.Options.Add(compareToOption);    // --compare-to (defined above)
toolCmd.Options.Add(autoDriftOption);    // --auto-drift (defined above)
toolCmd.Options.Add(quickOption);
toolCmd.Options.Add(compactOption);
toolCmd.Options.Add(storePathOption);
toolCmd.Options.Add(formatOption);

// Store management commands
var storeCmd = new Command("store", "Manage the profile store (list, clear, prune stored profiles)");
var storeListCmd = new Command("list", "List all stored profiles");
var storeClearCmd = new Command("clear", "Clear all stored profiles");
var storePruneCmd = new Command("prune", "Remove old profiles, keeping N most recent per schema");
var storeStatsCmd = new Command("stats", "Show store statistics");
var storeDeleteOption = new Option<string?>("--id") { Description = "Profile ID to delete" };
var pruneKeepOption = new Option<int>("--keep", "-k") { Description = "Number of profiles to keep per schema", DefaultValueFactory = _ => 5 };

storePruneCmd.Options.Add(pruneKeepOption);
storeListCmd.Options.Add(storePathOption);
storeClearCmd.Options.Add(storePathOption);
storePruneCmd.Options.Add(storePathOption);
storeStatsCmd.Options.Add(storePathOption);
storeCmd.Subcommands.Add(storeListCmd);
storeCmd.Subcommands.Add(storeClearCmd);
storeCmd.Subcommands.Add(storePruneCmd);
storeCmd.Subcommands.Add(storeStatsCmd);
storeCmd.Options.Add(storePathOption);
storeCmd.Options.Add(storeDeleteOption);

var rootCommand = new RootCommand("Data summarization tool - profile CSV, Excel, Parquet, SQLite files");
rootCommand.Options.Add(fileOption);
rootCommand.Options.Add(sheetOption);
rootCommand.Options.Add(tableOption);
rootCommand.Options.Add(modelOption);
rootCommand.Options.Add(noLlmOption);
rootCommand.Options.Add(verboseOption);
rootCommand.Options.Add(outputOption);
rootCommand.Options.Add(queryOption);
rootCommand.Options.Add(onnxOption);
rootCommand.Options.Add(onnxEnabledOption);
rootCommand.Options.Add(onnxModelOption);
rootCommand.Options.Add(onnxGpuOption);
rootCommand.Options.Add(onnxCpuOption);
rootCommand.Options.Add(onnxModelDirOption);
rootCommand.Options.Add(showPiiOption);
rootCommand.Options.Add(showPiiTypeOption);
rootCommand.Options.Add(hidePiiLabelsOption);
rootCommand.Options.Add(ingestDirOption);
rootCommand.Options.Add(ingestFilesOption);
rootCommand.Options.Add(registryQueryOption);
rootCommand.Options.Add(vectorDbOption);
rootCommand.Options.Add(sessionIdOption);
rootCommand.Options.Add(synthPathOption);
rootCommand.Options.Add(synthRowsOption);
rootCommand.Options.Add(columnsOption);
rootCommand.Options.Add(excludeColumnsOption);
rootCommand.Options.Add(maxColumnsOption);
rootCommand.Options.Add(fastModeOption);
rootCommand.Options.Add(skipCorrelationsOption);
rootCommand.Options.Add(ignoreErrorsOption);
rootCommand.Options.Add(targetOption);
rootCommand.Options.Add(markdownOutputOption);
rootCommand.Options.Add(noReportOption);
rootCommand.Options.Add(focusQuestionOption);
rootCommand.Options.Add(interactiveOption);
rootCommand.Options.Add(outputProfileOption);
rootCommand.Subcommands.Add(profileCmd);
rootCommand.Subcommands.Add(synthCmd);
rootCommand.Subcommands.Add(validateCmd);
rootCommand.Subcommands.Add(toolCmd);
rootCommand.Subcommands.Add(storeCmd);
rootCommand.Subcommands.Add(segmentCmd);
rootCommand.Subcommands.Add(toSqliteCmd);
rootCommand.Subcommands.Add(convertMdCmd);
rootCommand.Subcommands.Add(searchCmd);

profileCmd.SetAction(async (parseResult, cancellationToken) =>
{
    var file = parseResult.GetValue(fileOption);
    var ingestFiles = parseResult.GetValue(ingestFilesOption) ?? Array.Empty<string>();
    var ingestDir = parseResult.GetValue(ingestDirOption);
    var output = parseResult.GetValue(outputOption);
    var verbose = parseResult.GetValue(verboseOption);
    var noLlm = parseResult.GetValue(noLlmOption);
    var model = parseResult.GetValue(modelOption);
    var onnx = parseResult.GetValue(onnxOption);
    var vectorDb = parseResult.GetValue(vectorDbOption);
    var sessionId = parseResult.GetValue(sessionIdOption);

    var sources = CliHelpers.ExpandPatternsHelper(new[] { file }.Concat(ingestFiles), ingestDir);
    if (!sources.Any()) { Console.WriteLine("No sources found."); return; }
    var sid = sessionId ?? Guid.NewGuid().ToString("N");
    var profiles = new List<DataProfile>();
    using var svc = new DataSummarizerService(
        verbose: verbose, 
        ollamaModel: noLlm ? null : model, 
        ollamaUrl: "http://localhost:11434", 
        onnxSentinelPath: onnx, 
        onnxConfig: settings.Onnx,
        vectorStorePath: vectorDb, 
        sessionId: sid);
    foreach (var src in sources)
    {
        var report = await svc.SummarizeAsync(src, useLlm: false);
        profiles.Add(report.Profile);
    }
    var outPath = output ?? "profile.json";
    ProfileIo.SaveProfiles(profiles, outPath);
    Console.WriteLine($"Profile saved to {outPath}");
});

synthCmd.SetAction(async (parseResult, cancellationToken) =>
{
    var profilePath = parseResult.GetValue(synthProfileOption);
    var synthOut = parseResult.GetValue(synthPathOption);
    var rows = parseResult.GetValue(synthRowsOption);
    var verbose = parseResult.GetValue(verboseOption);
    
    var profiles = ProfileIo.LoadProfiles(profilePath ?? "profile.json");
    if (profiles.Count == 0) { Console.WriteLine("No profiles found in JSON"); return; }
    var outPath = synthOut ?? "synthetic.csv";
    DataSynthesizer.GenerateCsv(profiles[0], rows, outPath);
    Console.WriteLine($"Synthetic data written to {outPath}");
    await Task.CompletedTask;
});

validateCmd.SetAction(async (parseResult, cancellationToken) =>
{
    var source = parseResult.GetValue(synthSourceOption)!;
    var target = parseResult.GetValue(synthTargetOption)!;
    var output = parseResult.GetValue(outputOption);
    var verbose = parseResult.GetValue(verboseOption);
    var model = parseResult.GetValue(modelOption);
    var noLlm = parseResult.GetValue(noLlmOption);
    var vectorDb = parseResult.GetValue(vectorDbOption);
    var sessionId = parseResult.GetValue(sessionIdOption);
    var constraintFile = parseResult.GetValue(constraintFileOption);
    var generateConstraints = parseResult.GetValue(generateConstraintsOption);
    var strict = parseResult.GetValue(strictValidationOption);
    var format = parseResult.GetValue(formatOption)?.ToLowerInvariant() ?? "json";

    var sid = sessionId ?? Guid.NewGuid().ToString("N");
    var srcFiles = CliHelpers.ExpandPatternsHelper(new[] { source }, null).ToList();
    var tgtFiles = CliHelpers.ExpandPatternsHelper(new[] { target }, null).ToList();
    if (srcFiles.Count == 0 || tgtFiles.Count == 0) { Console.WriteLine("Missing source/target files"); return; }

    using var svc = new DataSummarizerService(
        verbose: verbose, 
        ollamaModel: noLlm ? null : model, 
        ollamaUrl: "http://localhost:11434", 
        onnxSentinelPath: null,
        onnxConfig: settings.Onnx,
        vectorStorePath: vectorDb, 
        sessionId: sid);
    var srcReport = await svc.SummarizeAsync(srcFiles[0], useLlm: false);
    var tgtReport = await svc.SummarizeAsync(tgtFiles[0], useLlm: false);

    // Constraint validation mode
    if (!string.IsNullOrEmpty(constraintFile) || generateConstraints)
    {
        var validator = new ConstraintValidator(verbose);
        ConstraintSuite suite;
        
        if (!string.IsNullOrEmpty(constraintFile) && File.Exists(constraintFile))
        {
            var suiteJson = await File.ReadAllTextAsync(constraintFile);
            suite = System.Text.Json.JsonSerializer.Deserialize<ConstraintSuite>(suiteJson) 
                ?? throw new InvalidOperationException("Failed to parse constraint file");
        }
        else
        {
            // Auto-generate constraints from source profile
            suite = validator.GenerateFromProfile(srcReport.Profile);
            if (generateConstraints && string.IsNullOrEmpty(constraintFile))
            {
                // Output the generated constraints
                var generatedJson = System.Text.Json.JsonSerializer.Serialize(suite, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                var constraintOutPath = output ?? Path.ChangeExtension(srcFiles[0], ".constraints.json");
                await File.WriteAllTextAsync(constraintOutPath, generatedJson);
                AnsiConsole.MarkupLine($"[green]Generated {suite.Constraints.Count} constraints to:[/] {constraintOutPath}");
            }
        }

        // Validate target against constraints
        var validationResult = validator.Validate(tgtReport.Profile, suite);
        
        // Output based on format
        var outputContent = format switch
        {
            "markdown" => FormatConstraintValidationMarkdown(validationResult),
            "html" => FormatConstraintValidationHtml(validationResult),
            _ => System.Text.Json.JsonSerializer.Serialize(validationResult, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
        };
        
        if (!string.IsNullOrEmpty(output))
        {
            await File.WriteAllTextAsync(output, outputContent);
            AnsiConsole.MarkupLine($"[green]Validation report saved to:[/] {output}");
        }
        
        // Console output
        if (format == "json")
        {
            Console.WriteLine(outputContent);
        }
        else
        {
            AnsiConsole.Write(new Rule($"[cyan]Constraint Validation: {validationResult.SuiteName}[/]").LeftJustified());
            AnsiConsole.MarkupLine($"[bold]Pass Rate:[/] {validationResult.PassRate:P1} ({validationResult.PassedConstraints}/{validationResult.TotalConstraints})");
            
            if (validationResult.FailedConstraints > 0)
            {
                AnsiConsole.MarkupLine("\n[yellow]Failed Constraints:[/]");
                foreach (var failure in validationResult.GetFailures().Take(10))
                {
                    AnsiConsole.MarkupLine($"  [red]X[/] {Markup.Escape(failure.Constraint.Description)}");
                    if (failure.ActualValue != null)
                        AnsiConsole.MarkupLine($"    [dim]Actual: {failure.ActualValue}[/]");
                    if (!string.IsNullOrEmpty(failure.Details))
                        AnsiConsole.MarkupLine($"    [dim]{Markup.Escape(failure.Details)}[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[green]All constraints passed![/]");
            }
        }
        
        // Exit with error code if strict mode and failures exist
        if (strict && validationResult.FailedConstraints > 0)
        {
            Environment.Exit(1);
        }
        return;
    }

    // Standard drift comparison mode
    var validation = ValidationService.Compare(srcReport.Profile, tgtReport.Profile);
    
    // Also compute detailed drift with ProfileComparator
    var comparator = new ProfileComparator();
    var detailedDrift = comparator.Compare(srcReport.Profile, tgtReport.Profile);
    
    // Compute anomaly score
    var anomalyScore = AnomalyScorer.ComputeAnomalyScore(tgtReport.Profile);
    
    // If significant drift detected, suggest updated constraints
    if (detailedDrift.HasSignificantDrift && detailedDrift.OverallDriftScore > 0.3)
    {
        var validator = new ConstraintValidator(verbose);
        var suggestedConstraints = validator.GenerateFromProfile(tgtReport.Profile);
        
        var suggestedPath = output != null
            ? Path.Combine(Path.GetDirectoryName(output) ?? ".", "constraints.suggested.json")
            : "constraints.suggested.json";
            
        var suggestedJson = System.Text.Json.JsonSerializer.Serialize(suggestedConstraints, 
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(suggestedPath, suggestedJson);
        
        AnsiConsole.MarkupLine($"[yellow]⚠ Significant drift detected (score: {detailedDrift.OverallDriftScore:F2})[/]");
        AnsiConsole.MarkupLine($"[dim]Suggested constraints saved to: {suggestedPath}[/]");
        AnsiConsole.MarkupLine($"[dim]Review before applying in CI/CD pipeline.[/]");
    }
    
    var combinedResult = new
    {
        validation.Source,
        validation.Target,
        validation.DriftScore,
        AnomalyScore = anomalyScore,
        DetailedDrift = new
        {
            detailedDrift.Summary,
            detailedDrift.HasSignificantDrift,
            detailedDrift.OverallDriftScore,
            detailedDrift.RowCountChange,
            detailedDrift.SchemaChanges,
            detailedDrift.Recommendations
        },
        ColumnDeltas = validation.Columns
    };
    
    var outputContent2 = format switch
    {
        "markdown" => FormatValidationMarkdown(combinedResult, detailedDrift),
        "html" => FormatValidationHtml(combinedResult, detailedDrift),
        _ => System.Text.Json.JsonSerializer.Serialize(combinedResult, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
    };
    
    if (!string.IsNullOrEmpty(output))
    {
        await File.WriteAllTextAsync(output, outputContent2);
        AnsiConsole.MarkupLine($"[green]Validation report saved to:[/] {output}");
    }
    
    if (format == "json")
    {
        Console.WriteLine(outputContent2);
    }
    else
    {
        // Pretty console output
        AnsiConsole.Write(new Rule("[cyan]Data Drift Validation[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[bold]Source:[/] {Path.GetFileName(source)}");
        AnsiConsole.MarkupLine($"[bold]Target:[/] {Path.GetFileName(target)}");
        AnsiConsole.MarkupLine($"[bold]Drift Score:[/] {validation.DriftScore:F3}");
        AnsiConsole.MarkupLine($"[bold]Anomaly Score:[/] {anomalyScore.OverallScore:F3} ({anomalyScore.Interpretation})");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{detailedDrift.Summary}[/]");
        
        if (detailedDrift.Recommendations.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Recommendations:[/]");
            foreach (var rec in detailedDrift.Recommendations.Take(5))
            {
                AnsiConsole.MarkupLine($"  - {Markup.Escape(rec)}");
            }
        }
    }
});

toolCmd.SetAction(async (parseResult, cancellationToken) =>
{
    var file = parseResult.GetValue(fileOption);
    var sheet = parseResult.GetValue(sheetOption);
    var targetColumn = parseResult.GetValue(targetOption);
    var columns = parseResult.GetValue(columnsOption);
    var excludeColumns = parseResult.GetValue(excludeColumnsOption);
    var maxColumns = parseResult.GetValue(maxColumnsOption);
    var fastMode = parseResult.GetValue(fastModeOption);
    var skipCorrelations = parseResult.GetValue(skipCorrelationsOption);
    var ignoreErrors = parseResult.GetValue(ignoreErrorsOption);
    
    // Tool-specific options
    var useCache = parseResult.GetValue(cacheOption);
    var storeResult = parseResult.GetValue(storeProfileOption);
    var compareToId = parseResult.GetValue(compareToOption);
    var autoDrift = parseResult.GetValue(autoDriftOption);
    var quickMode = parseResult.GetValue(quickOption);
    var compact = parseResult.GetValue(compactOption);
    var storePath = parseResult.GetValue(storePathOption);

    var startTime = DateTime.UtcNow;
    var jsonOptions = new System.Text.Json.JsonSerializerOptions 
    { 
        WriteIndented = !compact,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    
    try
    {
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            var errorOutput = new ToolOutput
            {
                Success = false,
                Source = file ?? "none",
                Error = file == null ? "File path is required" : $"File not found: {file}"
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(errorOutput, jsonOptions));
            return;
        }

        var store = new ProfileStore(storePath);
        DataProfile? profile = null;
        StoredProfileInfo? cachedInfo = null;
        bool usedCache = false;
        string? contentHash = null;
        
        // Fast path: check cache first (uses xxHash64 - very fast even for large files)
        if (useCache)
        {
            cachedInfo = store.QuickFindExisting(file);
            if (cachedInfo != null)
            {
                profile = store.LoadProfile(cachedInfo.Id);
                if (profile != null)
                {
                    usedCache = true;
                    contentHash = cachedInfo.ContentHash;
                }
            }
        }
        
        // Profile if not cached
        if (profile == null)
        {
            // Quick mode: minimal stats, no patterns/correlations
            var profileOptions = new ProfileOptions
            {
                Columns = columns?.Length > 0 ? columns.ToList() : null,
                ExcludeColumns = excludeColumns?.Length > 0 ? excludeColumns.ToList() : null,
                MaxColumns = quickMode ? 100 : (maxColumns ?? 50),
                FastMode = quickMode || fastMode,
                SkipCorrelations = quickMode || skipCorrelations,
                IgnoreErrors = ignoreErrors,
                TargetColumn = targetColumn
            };

            using var svc = new DataSummarizerService(
                verbose: false,
                ollamaModel: null,
                ollamaUrl: "http://localhost:11434",
                onnxSentinelPath: null,
                onnxConfig: quickMode ? null : settings.Onnx,
                vectorStorePath: null,
                sessionId: null,
                profileOptions: profileOptions
            );

            var report = await svc.SummarizeAsync(file, sheet, useLlm: false);
            profile = report.Profile;
        }
        
        var processingTime = DateTime.UtcNow - startTime;
        
        // Store if requested
        StoredProfileInfo? storedInfo = null;
        if (storeResult && !usedCache)
        {
            storedInfo = store.Store(profile, contentHash);
        }
        
        // Drift detection
        ToolDriftSummary? drift = null;
        if (!string.IsNullOrEmpty(compareToId))
        {
            // Manual comparison to specific profile
            var baselineProfile = store.LoadProfile(compareToId);
            var baselineInfo = store.ListAll().FirstOrDefault(p => p.Id == compareToId);
            if (baselineProfile != null && baselineInfo != null)
            {
                drift = ComputeDrift(profile, baselineProfile, baselineInfo);
            }
        }
        else if (autoDrift)
        {
            // Auto-detect baseline (oldest profile with same schema)
            var baseline = store.LoadBaseline(profile);
            if (baseline != null && baseline.SourcePath != profile.SourcePath)
            {
                var schemaHash = ProfileStore.ComputeSchemaHash(profile);
                var baselineInfo = store.GetHistory(schemaHash).LastOrDefault(); // Oldest
                if (baselineInfo != null)
                {
                    drift = ComputeDrift(profile, baseline, baselineInfo);
                }
            }
        }
        
        // Build output
        var toolProfile = BuildToolProfile(profile, quickMode);
        
        var output = new ToolOutput
        {
            Success = true,
            Source = file,
            Profile = toolProfile,
            Metadata = new ToolMetadata
            {
                ProcessingSeconds = Math.Round(processingTime.TotalSeconds, 3),
                ColumnsAnalyzed = profile.ColumnCount,
                RowsAnalyzed = profile.RowCount,
                Model = null,
                UsedLlm = false,
                TargetColumn = targetColumn,
                ProfiledAt = startTime.ToString("o"),
                ProfileId = storedInfo?.Id ?? cachedInfo?.Id,
                SchemaHash = ProfileStore.ComputeSchemaHash(profile),
                ContentHash = contentHash ?? (usedCache ? cachedInfo?.ContentHash : ProfileStore.ComputeFileHash(file)),
                Drift = drift
            }
        };

        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(output, jsonOptions));
    }
    catch (Exception ex)
    {
        var errorOutput = new ToolOutput
        {
            Success = false,
            Source = file ?? "none",
            Error = ex.Message
        };
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(errorOutput, jsonOptions));
    }
});

// Store command handlers
storeListCmd.SetAction((parseResult, cancellationToken) =>
{
    var storePath = parseResult.GetValue(storePathOption);
    var store = new ProfileStore(storePath);
    var profiles = store.ListAll(100);
    
    if (profiles.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No stored profiles found.[/]");
        return Task.CompletedTask;
    }
    
    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("ID")
        .AddColumn("File")
        .AddColumn("Rows")
        .AddColumn("Cols")
        .AddColumn("Schema")
        .AddColumn("Stored");
    
    foreach (var p in profiles)
    {
        table.AddRow(
            p.Id,
            Markup.Escape(p.FileName),
            p.RowCount.ToString("N0"),
            p.ColumnCount.ToString(),
            p.SchemaHash[..8],
            p.StoredAt.ToString("yyyy-MM-dd HH:mm"));
    }
    
    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine($"[dim]Total: {profiles.Count} profile(s)[/]");
    return Task.CompletedTask;
});

storeClearCmd.SetAction((parseResult, cancellationToken) =>
{
    var storePath = parseResult.GetValue(storePathOption);
    
    if (!AnsiConsole.Confirm("[red]Clear ALL stored profiles?[/]", defaultValue: false))
    {
        AnsiConsole.MarkupLine("[dim]Cancelled.[/]");
        return Task.CompletedTask;
    }
    
    var store = new ProfileStore(storePath);
    var count = store.ClearAll();
    AnsiConsole.MarkupLine($"[green]Cleared {count} profile(s).[/]");
    return Task.CompletedTask;
});

storePruneCmd.SetAction((parseResult, cancellationToken) =>
{
    var storePath = parseResult.GetValue(storePathOption);
    var keep = parseResult.GetValue(pruneKeepOption);
    
    var store = new ProfileStore(storePath);
    var pruned = store.Prune(keep);
    AnsiConsole.MarkupLine($"[green]Pruned {pruned} old profile(s), keeping {keep} most recent per schema.[/]");
    return Task.CompletedTask;
});

storeStatsCmd.SetAction((parseResult, cancellationToken) =>
{
    var storePath = parseResult.GetValue(storePathOption);
    var store = new ProfileStore(storePath);
    var stats = store.GetStats();
    
    AnsiConsole.Write(new Rule("[cyan]Profile Store Statistics[/]").LeftJustified());
    AnsiConsole.MarkupLine($"[bold]Store path:[/] {Markup.Escape(stats.StorePath)}");
    AnsiConsole.MarkupLine($"[bold]Total profiles:[/] {stats.TotalProfiles}");
    AnsiConsole.MarkupLine($"[bold]Total size:[/] {stats.TotalSizeFormatted}");
    AnsiConsole.MarkupLine($"[bold]Unique schemas:[/] {stats.UniqueSchemas}");
    AnsiConsole.MarkupLine($"[bold]Segment groups:[/] {stats.SegmentGroups}");
    if (stats.OldestProfile.HasValue)
        AnsiConsole.MarkupLine($"[bold]Oldest:[/] {stats.OldestProfile:yyyy-MM-dd HH:mm}");
    if (stats.NewestProfile.HasValue)
        AnsiConsole.MarkupLine($"[bold]Newest:[/] {stats.NewestProfile:yyyy-MM-dd HH:mm}");
    return Task.CompletedTask;
});

storeCmd.SetAction(async (parseResult, cancellationToken) =>
{
    var storePath = parseResult.GetValue(storePathOption);
    var deleteId = parseResult.GetValue(storeDeleteOption);
    
    if (!string.IsNullOrEmpty(deleteId))
    {
        var store = new ProfileStore(storePath);
        if (store.Delete(deleteId))
        {
            AnsiConsole.MarkupLine($"[green]Deleted profile {deleteId}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Profile {deleteId} not found[/]");
        }
        return;
    }
    
    // Interactive menu mode
    await ShowProfileManagementMenu(storePath);
});

// Segment comparison command handler
segmentCmd.SetAction(async (parseResult, cancellationToken) =>
{
    var segmentA = parseResult.GetValue(segmentAOption);
    var segmentB = parseResult.GetValue(segmentBOption);
    var nameA = parseResult.GetValue(segmentNameAOption);
    var nameB = parseResult.GetValue(segmentNameBOption);
    var output = parseResult.GetValue(outputOption);
    var format = parseResult.GetValue(formatOption)?.ToLowerInvariant() ?? "json";
    var storePath = parseResult.GetValue(storePathOption);

    if (string.IsNullOrEmpty(segmentA) || string.IsNullOrEmpty(segmentB))
    {
        AnsiConsole.MarkupLine("[red]Both --segment-a and --segment-b are required[/]");
        AnsiConsole.MarkupLine("[dim]Usage: datasummarizer segment --segment-a <path-or-id> --segment-b <path-or-id>[/]");
        return;
    }

    var store = new ProfileStore(storePath);
    
    // Load profiles - can be file paths or profile IDs
    DataProfile? profileA = null;
    DataProfile? profileB = null;

    // Try to load segment A
    if (File.Exists(segmentA))
    {
        using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, onnxConfig: settings.Onnx);
        var report = await svc.SummarizeAsync(segmentA, useLlm: false);
        profileA = report.Profile;
        nameA ??= Path.GetFileName(segmentA);
    }
    else
    {
        profileA = store.LoadProfile(segmentA);
        var info = store.ListAll().FirstOrDefault(p => p.Id == segmentA);
        nameA ??= info?.FileName ?? segmentA;
    }

    // Try to load segment B
    if (File.Exists(segmentB))
    {
        using var svc = new DataSummarizerService(verbose: false, ollamaModel: null, onnxConfig: settings.Onnx);
        var report = await svc.SummarizeAsync(segmentB, useLlm: false);
        profileB = report.Profile;
        nameB ??= Path.GetFileName(segmentB);
    }
    else
    {
        profileB = store.LoadProfile(segmentB);
        var info = store.ListAll().FirstOrDefault(p => p.Id == segmentB);
        nameB ??= info?.FileName ?? segmentB;
    }

    if (profileA == null || profileB == null)
    {
        AnsiConsole.MarkupLine("[red]Could not load one or both profiles[/]");
        if (profileA == null) AnsiConsole.MarkupLine($"[dim]Segment A not found: {segmentA}[/]");
        if (profileB == null) AnsiConsole.MarkupLine($"[dim]Segment B not found: {segmentB}[/]");
        return;
    }

    // Perform comparison
    var segmentProfiler = new SegmentProfiler();
    var comparison = segmentProfiler.CompareSegments(profileA, profileB, nameA, nameB);
    
    // Also compute anomaly scores for context
    var anomalyA = AnomalyScorer.ComputeAnomalyScore(profileA);
    var anomalyB = AnomalyScorer.ComputeAnomalyScore(profileB);

    var result = new
    {
        comparison.SegmentAName,
        comparison.SegmentBName,
        comparison.SegmentARowCount,
        comparison.SegmentBRowCount,
        comparison.Similarity,
        comparison.OverallDistance,
        AnomalyScoreA = anomalyA.OverallScore,
        AnomalyScoreB = anomalyB.OverallScore,
        comparison.Insights,
        comparison.ColumnComparisons,
        comparison.ComparedAt
    };

    // Format output
    var outputContent = format switch
    {
        "markdown" => FormatSegmentComparisonMarkdown(comparison, anomalyA, anomalyB),
        "html" => FormatSegmentComparisonHtml(comparison, anomalyA, anomalyB),
        _ => System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
    };

    if (!string.IsNullOrEmpty(output))
    {
        await File.WriteAllTextAsync(output, outputContent);
        AnsiConsole.MarkupLine($"[green]Segment comparison saved to:[/] {output}");
    }

    if (format == "json")
    {
        Console.WriteLine(outputContent);
    }
    else
    {
        // Pretty console output
        AnsiConsole.Write(new Rule("[cyan]Segment Comparison[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[bold]Segment A:[/] {Markup.Escape(comparison.SegmentAName)} ({comparison.SegmentARowCount:N0} rows)");
        AnsiConsole.MarkupLine($"[bold]Segment B:[/] {Markup.Escape(comparison.SegmentBName)} ({comparison.SegmentBRowCount:N0} rows)");
        AnsiConsole.WriteLine();
        
        var similarityColor = comparison.Similarity >= 0.8 ? "green" : comparison.Similarity >= 0.5 ? "yellow" : "red";
        AnsiConsole.MarkupLine($"[bold]Similarity:[/] [{similarityColor}]{comparison.Similarity:P1}[/]");
        AnsiConsole.MarkupLine($"[bold]Anomaly Scores:[/] A={anomalyA.OverallScore:F3} ({anomalyA.Interpretation}), B={anomalyB.OverallScore:F3} ({anomalyB.Interpretation})");
        AnsiConsole.WriteLine();

        // Insights
        AnsiConsole.MarkupLine("[bold]Insights:[/]");
        foreach (var insight in comparison.Insights)
        {
            AnsiConsole.MarkupLine($"  - {Markup.Escape(insight)}");
        }
        AnsiConsole.WriteLine();

        // Top differing columns
        var topDiffs = comparison.ColumnComparisons.Take(5).ToList();
        if (topDiffs.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]Top Differences:[/]");
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Column");
            table.AddColumn("Type");
            table.AddColumn("Distance");
            table.AddColumn("A");
            table.AddColumn("B");
            table.AddColumn("Delta");

            foreach (var col in topDiffs)
            {
                var valueA = col.ColumnType == ColumnType.Numeric ? col.MeanA?.ToString("F2") ?? "-" : col.ModeA ?? "-";
                var valueB = col.ColumnType == ColumnType.Numeric ? col.MeanB?.ToString("F2") ?? "-" : col.ModeB ?? "-";
                var delta = col.MeanDelta?.ToString("+0.0;-0.0") ?? (col.NullRateDelta != 0 ? $"{col.NullRateDelta * 100:+0.0;-0.0}pp nulls" : "-");
                
                table.AddRow(
                    col.ColumnName,
                    col.ColumnType.ToString(),
                    $"{col.Distance:F3}",
                    valueA,
                    valueB,
                    delta
                );
            }
            AnsiConsole.Write(table);
        }
    }
});

// to-sqlite command handler
toSqliteCmd.SetAction(async (parseResult, cancellationToken) =>
{
    var file = parseResult.GetValue(fileOption);
    var sqliteOutput = parseResult.GetValue(sqliteOutputOption);
    var tableName = parseResult.GetValue(sqliteTableOption);
    var noIndexes = parseResult.GetValue(sqliteNoIndexOption);
    var overwrite = parseResult.GetValue(sqliteOverwriteOption);
    var verbose = parseResult.GetValue(verboseOption);

    if (string.IsNullOrEmpty(file) || !File.Exists(file))
    {
        AnsiConsole.MarkupLine("[red]Error:[/] Source file is required and must exist.");
        return;
    }

    if (string.IsNullOrEmpty(sqliteOutput))
    {
        AnsiConsole.MarkupLine("[red]Error:[/] Output SQLite path is required (--output).");
        return;
    }

    // Ensure .sqlite extension
    if (!sqliteOutput.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) &&
        !sqliteOutput.EndsWith(".db", StringComparison.OrdinalIgnoreCase) &&
        !sqliteOutput.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase))
    {
        sqliteOutput += ".sqlite";
    }

    var exporter = new SqliteExporter(verbose);
    
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync($"Exporting to SQLite...", async ctx =>
        {
            ctx.Status("Profiling source data...");
            
            var result = await exporter.ExportAsync(
                file,
                sqliteOutput,
                tableName,
                profile: null,
                createIndexes: !noIndexes,
                overwrite: overwrite
            );

            if (result.Success)
            {
                AnsiConsole.MarkupLine($"[green]✓ Export complete![/]");
                AnsiConsole.MarkupLine($"  [dim]Source:[/] {result.SourcePath}");
                AnsiConsole.MarkupLine($"  [dim]Output:[/] {result.SqlitePath}");
                AnsiConsole.MarkupLine($"  [dim]Table:[/]  {result.TableName}");
                AnsiConsole.MarkupLine($"  [dim]Rows:[/]   {result.RowCount:N0}");
                AnsiConsole.MarkupLine($"  [dim]Cols:[/]   {result.ColumnCount}");
                if (result.IndexesCreated?.Count > 0)
                {
                    AnsiConsole.MarkupLine($"  [dim]Indexes:[/] {result.IndexesCreated.Count} created");
                    if (verbose)
                    {
                        foreach (var idx in result.IndexesCreated)
                        {
                            AnsiConsole.MarkupLine($"    - {idx}");
                        }
                    }
                }
                AnsiConsole.MarkupLine($"  [dim]Time:[/]   {result.Duration.TotalSeconds:F2}s");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]✗ Export failed:[/] {result.Error}");
            }
        });
});

// Markdown table conversion handler
convertMdCmd.SetAction(async (parseResult, cancellationToken) =>
{
    var mdInput = parseResult.GetValue(mdInputOption);
    var outputDir = parseResult.GetValue(mdOutputDirOption) ?? "./converted_tables";
    var listOnly = parseResult.GetValue(mdListOnlyOption);
    var verbose = parseResult.GetValue(verboseOption);

    if (string.IsNullOrEmpty(mdInput) || !File.Exists(mdInput))
    {
        AnsiConsole.MarkupLine("[red]Error:[/] Markdown file not found: {0}", mdInput ?? "null");
        return;
    }

    try
    {
        // Check if file contains tables
        var hasTables = await MarkdownTableConverter.ContainsTablesAsync(mdInput, cancellationToken);

        if (!hasTables)
        {
            AnsiConsole.MarkupLine("[yellow]No markdown tables found in {0}[/]", Path.GetFileName(mdInput));
            return;
        }

        // Extract tables
        var content = await File.ReadAllTextAsync(mdInput, cancellationToken);
        var tables = MarkdownTableConverter.ExtractTablesToCsv(content);

        if (listOnly)
        {
            AnsiConsole.MarkupLine("[green]Found {0} table(s) in {1}:[/]", tables.Count, Path.GetFileName(mdInput));
            for (int i = 0; i < tables.Count; i++)
            {
                var lines = tables[i].Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var rowCount = lines.Length - 1; // Exclude header
                var colCount = lines.FirstOrDefault()?.Split(',').Length ?? 0;
                AnsiConsole.MarkupLine("  Table {0}: {1} columns × {2} rows", i + 1, colCount, rowCount);
            }
            return;
        }

        // Convert and save
        var csvPaths = await MarkdownTableConverter.ConvertFileAsync(mdInput, outputDir, cancellationToken);

        AnsiConsole.MarkupLine("[green]✓[/] Converted {0} table(s) from {1}:", csvPaths.Count, Path.GetFileName(mdInput));
        foreach (var csvPath in csvPaths)
        {
            var fileInfo = new FileInfo(csvPath);
            AnsiConsole.MarkupLine("  [cyan]{0}[/] ({1:N0} bytes)", Path.GetFileName(csvPath), fileInfo.Length);

            if (verbose)
            {
                // Show preview
                var lines = await File.ReadAllLinesAsync(csvPath, cancellationToken);
                AnsiConsole.MarkupLine("  [dim]Preview:[/]");
                foreach (var line in lines.Take(3))
                {
                    AnsiConsole.MarkupLine("    [dim]{0}[/]", Markup.Escape(line));
                }
                if (lines.Length > 3)
                {
                    AnsiConsole.MarkupLine("    [dim]... ({0} more rows)[/]", lines.Length - 3);
                }
            }
        }

        AnsiConsole.MarkupLine("\n[dim]Tip: Use 'datasummarizer profile <csv>' to analyze these tables[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine("[red]Error converting markdown tables:[/] {0}", ex.Message);
        if (verbose)
        {
            AnsiConsole.WriteException(ex);
        }
    }
});

// Intelligent search command handler
searchCmd.SetAction(async (parseResult, cancellationToken) =>
{
    var searchQuery = parseResult.GetValue(searchTermArg);
    var file = parseResult.GetValue(fileOption);
    var table = parseResult.GetValue(tableOption);
    var column = parseResult.GetValue(searchColumnOption);
    var limit = parseResult.GetValue(searchLimitOption);
    var jsonOutput = parseResult.GetValue(searchJsonOption);
    var verbose = parseResult.GetValue(verboseOption);

    // Validate file
    if (string.IsNullOrEmpty(file) || !File.Exists(file))
    {
        AnsiConsole.MarkupLine("[red]Error:[/] Source file is required and must exist (--file or -f).");
        return;
    }

    if (string.IsNullOrEmpty(searchQuery))
    {
        AnsiConsole.MarkupLine("[red]Error:[/] Search query is required.");
        return;
    }

    using var searcher = new DataSearcher(verbose);

    // Detect if natural language query (multiple words with action verbs or complex patterns)
    var words = searchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var hasComparisonPattern = System.Text.RegularExpressions.Regex.IsMatch(
        searchQuery, @"\w+\s+(over|above|under|below|greater|less|more|than)\s+\d+", 
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    var isNaturalLanguage = (words.Length > 2 &&
        (searchQuery.Contains("show", StringComparison.OrdinalIgnoreCase) ||
         searchQuery.Contains("find", StringComparison.OrdinalIgnoreCase) ||
         searchQuery.Contains("where", StringComparison.OrdinalIgnoreCase) ||
         searchQuery.Contains("named", StringComparison.OrdinalIgnoreCase) ||
         searchQuery.Contains("with", StringComparison.OrdinalIgnoreCase) ||
         searchQuery.Contains("older", StringComparison.OrdinalIgnoreCase) ||
         searchQuery.Contains("younger", StringComparison.OrdinalIgnoreCase) ||
         searchQuery.Contains("greater", StringComparison.OrdinalIgnoreCase) ||
         searchQuery.Contains("less", StringComparison.OrdinalIgnoreCase) ||
         searchQuery.Contains("containing", StringComparison.OrdinalIgnoreCase))) ||
        hasComparisonPattern;

    SearchResult result;

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync($"Searching...", async ctx =>
        {
            if (isNaturalLanguage)
            {
                ctx.Status("Parsing natural language query...");
                result = await searcher.NaturalSearchAsync(file, searchQuery, table, limit);
            }
            else
            {
                ctx.Status($"Searching for '{searchQuery}'...");
                result = await searcher.SearchAsync(file, searchQuery, table, column, limit);
            }

            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {result.Error}");
                return;
            }

            // Output results
            if (jsonOutput)
            {
                var jsonOptions = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, jsonOptions));
            }
            else
            {
                // Console output with Spectre.Console
                AnsiConsole.MarkupLine($"[green]Search Results[/] ({result.MatchCount} matches in {result.Duration.TotalSeconds:F2}s)");
                AnsiConsole.MarkupLine($"[dim]Source:[/] {result.SourcePath}");
                if (result.IsNaturalLanguage && result.ParsedQuery != null)
                {
                    AnsiConsole.MarkupLine($"[dim]Interpreted as:[/] {string.Join(", ", result.ParsedQuery.Conditions.Select(c => c.Description))}");
                }
                if (result.Strategies.Count > 0)
                {
                    AnsiConsole.MarkupLine($"[dim]Strategies:[/] {string.Join(", ", result.Strategies)}");
                }
                if (verbose && !string.IsNullOrEmpty(result.Sql))
                {
                    AnsiConsole.MarkupLine($"[dim]SQL:[/] {result.Sql}");
                }
                AnsiConsole.WriteLine();

                if (result.Rows.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No matches found.[/]");
                }
                else
                {
                    // Build table for display
                    var consoleTable = new Table();
                    consoleTable.Border(TableBorder.Rounded);

                    // Add columns from first row
                    var firstRow = result.Rows[0];
                    foreach (var key in firstRow.Keys)
                    {
                        consoleTable.AddColumn(new TableColumn(key).Centered());
                    }

                    // Add rows (limit display to reasonable number)
                    var displayRows = result.Rows.Take(50);
                    foreach (var row in displayRows)
                    {
                        var cells = row.Values.Select(v =>
                        {
                            if (v == null) return "[dim]null[/]";
                            var str = v.ToString() ?? "";
                            if (str.Length > 50) str = str[..47] + "...";
                            return Markup.Escape(str);
                        }).ToArray();
                        consoleTable.AddRow(cells);
                    }

                    AnsiConsole.Write(consoleTable);

                    if (result.Rows.Count > 50)
                    {
                        AnsiConsole.MarkupLine($"[dim]... and {result.Rows.Count - 50} more rows (use --json for full output)[/]");
                    }
                }
            }
        });
});

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var file = parseResult.GetValue(fileOption);
    var sheet = parseResult.GetValue(sheetOption);
    var sqliteTable = parseResult.GetValue(tableOption);
    // Use --table for SQLite, --sheet for Excel (but either works for both)
    var tableOrSheet = sqliteTable ?? sheet;
    var model = parseResult.GetValue(modelOption);
    var noLlm = parseResult.GetValue(noLlmOption);
    var verbose = parseResult.GetValue(verboseOption);
    var output = parseResult.GetValue(outputOption);
    var query = parseResult.GetValue(queryOption);
    var onnx = parseResult.GetValue(onnxOption);
    var onnxEnabled = parseResult.GetValue(onnxEnabledOption);
    var onnxModel = parseResult.GetValue(onnxModelOption);
    var onnxGpu = parseResult.GetValue(onnxGpuOption);
    var onnxCpu = parseResult.GetValue(onnxCpuOption);
    var onnxModelDir = parseResult.GetValue(onnxModelDirOption);
    var showPii = parseResult.GetValue(showPiiOption);
    var showPiiTypes = parseResult.GetValue(showPiiTypeOption);
    var hidePiiLabels = parseResult.GetValue(hidePiiLabelsOption);
    var ingestDir = parseResult.GetValue(ingestDirOption);
    var ingestFiles = parseResult.GetValue(ingestFilesOption);
    var registryQuery = parseResult.GetValue(registryQueryOption);
    var vectorDb = parseResult.GetValue(vectorDbOption);
    var sessionId = parseResult.GetValue(sessionIdOption);
    var synthPath = parseResult.GetValue(synthPathOption);
    var synthRows = parseResult.GetValue(synthRowsOption);
    var interactive = parseResult.GetValue(interactiveOption);
    
    // ONNX config - CLI options override appsettings.json
    var onnxConfig = settings.Onnx != null ? new OnnxConfig
    {
        Enabled = onnxEnabled ?? settings.Onnx.Enabled,
        EmbeddingModel = onnxModel != null ? Enum.Parse<OnnxEmbeddingModel>(onnxModel) : settings.Onnx.EmbeddingModel,
        UseQuantized = settings.Onnx.UseQuantized,
        ModelDirectory = onnxModelDir ?? settings.Onnx.ModelDirectory,
        MaxEmbeddingSequenceLength = settings.Onnx.MaxEmbeddingSequenceLength,
        InferenceThreads = settings.Onnx.InferenceThreads,
        UseParallelExecution = settings.Onnx.UseParallelExecution,
        InterOpThreads = settings.Onnx.InterOpThreads,
        ExecutionProvider = onnxGpu ? OnnxExecutionProvider.Auto : 
                           onnxCpu ? OnnxExecutionProvider.Cpu : 
                           settings.Onnx.ExecutionProvider,
        GpuDeviceId = settings.Onnx.GpuDeviceId,
        EmbeddingBatchSize = settings.Onnx.EmbeddingBatchSize
    } : null;
    
    // PII Display config - CLI options override appsettings.json
    var piiDisplayConfig = new PiiDisplayConfig
    {
        ShowPiiValues = showPii || settings.PiiDisplay.ShowPiiValues,
        ShowPiiTypeLabel = !hidePiiLabels && settings.PiiDisplay.ShowPiiTypeLabel,
        RedactionChar = settings.PiiDisplay.RedactionChar,
        VisibleChars = settings.PiiDisplay.VisibleChars,
        TypeSettings = new PiiTypeDisplaySettings
        {
            // If --show-pii is used, show everything
            // If --show-pii-type is used, enable only those types
            ShowSsn = showPii || (showPiiTypes?.Contains("ssn", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowSsn),
            ShowCreditCard = showPii || (showPiiTypes?.Contains("creditcard", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowCreditCard),
            ShowEmail = showPii || (showPiiTypes?.Contains("email", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowEmail),
            ShowPhone = showPii || (showPiiTypes?.Contains("phone", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowPhone),
            ShowPersonName = showPii || (showPiiTypes?.Contains("name", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowPersonName),
            ShowAddress = showPii || (showPiiTypes?.Contains("address", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowAddress),
            ShowDateOfBirth = showPii || (showPiiTypes?.Contains("dob", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowDateOfBirth),
            ShowIpAddress = showPii || (showPiiTypes?.Contains("ip", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowIpAddress),
            ShowBankAccount = showPii || (showPiiTypes?.Contains("bank", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowBankAccount),
            ShowPassport = showPii || (showPiiTypes?.Contains("passport", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowPassport),
            ShowDriversLicense = showPii || (showPiiTypes?.Contains("license", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowDriversLicense),
            ShowMacAddress = settings.PiiDisplay.TypeSettings.ShowMacAddress,
            ShowUrl = settings.PiiDisplay.TypeSettings.ShowUrl,
            ShowUuid = settings.PiiDisplay.TypeSettings.ShowUuid,
            ShowUsState = settings.PiiDisplay.TypeSettings.ShowUsState,
            ShowZipCode = settings.PiiDisplay.TypeSettings.ShowZipCode,
            ShowVin = showPii || (showPiiTypes?.Contains("vin", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowVin),
            ShowIban = showPii || (showPiiTypes?.Contains("iban", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowIban),
            ShowRoutingNumber = showPii || (showPiiTypes?.Contains("routing", StringComparer.OrdinalIgnoreCase) ?? settings.PiiDisplay.TypeSettings.ShowRoutingNumber),
            ShowOther = showPii || settings.PiiDisplay.TypeSettings.ShowOther
        }
    };
    
    // Update settings with CLI-modified config
    settings.PiiDisplay = piiDisplayConfig;
    
    // Profile options
    var columns = parseResult.GetValue(columnsOption);
    var excludeColumns = parseResult.GetValue(excludeColumnsOption);
    var maxColumns = parseResult.GetValue(maxColumnsOption);
    var fastMode = parseResult.GetValue(fastModeOption);
    var skipCorrelations = parseResult.GetValue(skipCorrelationsOption);
    var ignoreErrors = parseResult.GetValue(ignoreErrorsOption);
    var targetColumn = parseResult.GetValue(targetOption);
    var markdownOutput = parseResult.GetValue(markdownOutputOption);
    var skipReport = parseResult.GetValue(noReportOption);
    var focusQuestions = parseResult.GetValue(focusQuestionOption);
    var outputProfileName = parseResult.GetValue(outputProfileOption);
    
    // Resolve analysis profile: --fast is shortcut for "Fast" profile
    var analysisProfile = fastMode 
        ? AnalysisProfileConfig.Fast 
        : settings.GetActiveAnalysisProfile();
    
    // Apply analysis profile settings (CLI flags can still override)
    if (!analysisProfile.UseLlm) noLlm = true;
    if (!analysisProfile.ComputeCorrelations) skipCorrelations = true;
    if (!analysisProfile.DetectPatterns) fastMode = true;
    if (!analysisProfile.GenerateReport) skipReport = true;
    
    // Resolve output profile (separate from analysis - controls display)
    var activeProfile = ResolveOutputProfile(settings, outputProfileName);
    
    sessionId ??= Guid.NewGuid().ToString("N");
    
    // Double-click mode: no file specified, prompt for one
    if (string.IsNullOrWhiteSpace(file) && 
        string.IsNullOrWhiteSpace(ingestDir) && 
        (ingestFiles == null || ingestFiles.Length == 0) &&
        string.IsNullOrWhiteSpace(registryQuery))
    {
        // Check if running in non-interactive mode (CI, piped, etc.)
        if (Console.IsInputRedirected || Console.IsOutputRedirected || !Environment.UserInteractive)
        {
            Console.WriteLine("Usage: datasummarizer [options] <file>");
            Console.WriteLine("Try 'datasummarizer --help' for more information.");
            return;
        }
        
        ShowBanner();
        AnsiConsole.MarkupLine("[cyan]Welcome to DataSummarizer![/]");
        AnsiConsole.MarkupLine("[dim]DuckDB-powered data profiling - analyze CSV, Excel, Parquet, JSON files[/]\n");
        
        file = AnsiConsole.Ask<string>("[green]Enter path to data file:[/] ");
        
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            AnsiConsole.MarkupLine("[red]File not found. Exiting.[/]");
            AnsiConsole.MarkupLine("\n[dim]Press any key to exit...[/]");
            Console.ReadKey(true);
            return;
        }
        
        // In double-click mode, default to interactive
        interactive = AnsiConsole.Confirm("[yellow]Enter interactive mode?[/]", defaultValue: true);
    }
    
    var profileOptions = new ProfileOptions
    {
        Columns = columns?.Length > 0 ? columns.ToList() : settings.ProfileOptions.Columns,
        ExcludeColumns = excludeColumns?.Length > 0 ? excludeColumns.ToList() : settings.ProfileOptions.ExcludeColumns,
        MaxColumns = maxColumns ?? settings.ProfileOptions.MaxColumns,
        MaxCorrelationPairs = settings.ProfileOptions.MaxCorrelationPairs,
        FastMode = fastMode || settings.ProfileOptions.FastMode,
        SkipCorrelations = skipCorrelations || settings.ProfileOptions.SkipCorrelations,
        SampleSize = settings.ProfileOptions.SampleSize,
        IncludeDescriptions = settings.ProfileOptions.IncludeDescriptions,
        IgnoreErrors = ignoreErrors || settings.ProfileOptions.IgnoreErrors,
        TargetColumn = targetColumn ?? settings.ProfileOptions.TargetColumn
    };
    
    var reportOptions = new ReportOptions
    {
        GenerateMarkdown = settings.MarkdownReport.Enabled && !skipReport,
        UseLlm = settings.MarkdownReport.UseLlm,
        IncludeFocusQuestions = settings.MarkdownReport.IncludeFocusQuestions,
        FocusQuestions = settings.MarkdownReport.FocusQuestions?.ToList() ?? new()
    };
    
    if (focusQuestions is { Length: > 0 })
    {
        reportOptions.FocusQuestions = focusQuestions.ToList();
        reportOptions.IncludeFocusQuestions = true;
    }
    
    if (noLlm)
    {
        reportOptions.UseLlm = false;
    }

    var defaultReportDirectory = settings.MarkdownReport.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "reports");
    var resolvedReportPath = markdownOutput;
    if (string.IsNullOrWhiteSpace(resolvedReportPath) && !string.IsNullOrWhiteSpace(output))
    {
        resolvedReportPath = output;
    }
    if (string.IsNullOrWhiteSpace(resolvedReportPath) && reportOptions.GenerateMarkdown)
    {
        var fileName = !string.IsNullOrWhiteSpace(file)
            ? Path.GetFileNameWithoutExtension(file)
            : $"report-{DateTime.UtcNow:yyyyMMddHHmmss}";
        resolvedReportPath = Path.Combine(defaultReportDirectory, $"{fileName}-report.md");
    }


    try
    {
        // Header
        AnsiConsole.Write(new FigletText("DataSummarizer").Color(Color.Cyan1));
        
        var supported = new[] { ".csv", ".xlsx", ".xls", ".parquet", ".json", ".sqlite", ".db", ".sqlite3", ".log" };

        // Helper to validate a single file
        bool ValidateFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!File.Exists(path))
            {
                AnsiConsole.MarkupLine($"[red]Error: File not found: {path}[/]");
                return false;
            }
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!supported.Contains(ext))
            {
                AnsiConsole.MarkupLine($"[red]Error: Unsupported file type: {ext}[/]");
                AnsiConsole.MarkupLine($"[dim]Supported: {string.Join(", ", supported)}[/]");
                return false;
            }
            return true;
        }

        IEnumerable<string> ExpandPatterns(IEnumerable<string> patterns)
        {
            return CliHelpers.ExpandPatternsHelper(patterns, null, supported);
        }

        // Check if --file points to a directory (directory session mode)
        var isDirectorySession = !string.IsNullOrWhiteSpace(file) && Directory.Exists(file);
        List<string>? directoryFiles = null;
        
        if (isDirectorySession)
        {
            directoryFiles = ExpandPatterns([file!]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (directoryFiles.Count == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]No supported files found in directory: {file}[/]");
                AnsiConsole.MarkupLine($"[dim]Supported: {string.Join(", ", supported)}[/]");
                return;
            }
        }

        // Determine mode: ingestion, registry query, or single-file summarize/ask
        var ingestList = new List<string>();
        if (!string.IsNullOrWhiteSpace(ingestDir) && Directory.Exists(ingestDir))
        {
            ingestList.AddRange(ExpandPatterns([ingestDir]));
        }
        if (ingestFiles is { Length: > 0 })
        {
            ingestList.AddRange(ExpandPatterns(ingestFiles));
        }

        // If no ingest, no registry query, and not a directory session, require a file
        if (!ingestList.Any() && string.IsNullOrEmpty(registryQuery) && !isDirectorySession)
        {
            if (!ValidateFile(file))
            {
                Environment.ExitCode = 1;
                return;
            }
        }

        using var summarizer = new DataSummarizerService(
            verbose: verbose,
            ollamaModel: noLlm ? null : model,
            ollamaUrl: "http://localhost:11434",
            onnxSentinelPath: onnx,
            onnxConfig: onnxConfig,
            vectorStorePath: vectorDb,
            sessionId: sessionId,
            profileOptions: profileOptions,
            reportOptions: reportOptions,
            enableClarifierSentinel: settings.EnableClarifierSentinel,
            clarifierSentinelModel: settings.ClarifierSentinelModel
        );

        // Ingest mode
        if (ingestList.Any())
        {
            AnsiConsole.MarkupLine($"[cyan]Ingesting {ingestList.Count} file(s) into registry...[/]");
            foreach (var path in ingestList)
            {
                AnsiConsole.MarkupLine($"[dim]- {Path.GetFileName(path)}[/]");
            }

            await summarizer.IngestAsync(ingestList, maxLlmInsights: 0); // no LLM during ingestion for speed
            AnsiConsole.MarkupLine("[green]Ingestion complete.[/]");
            return;
        }

        // Registry query mode (vector search over ingested data)
        if (!string.IsNullOrEmpty(registryQuery))
        {
            AnsiConsole.MarkupLine($"[yellow]Registry question:[/] {registryQuery}");
            AnsiConsole.MarkupLine($"[dim]Session:[/] {sessionId}");
            AnsiConsole.WriteLine();

            var answer = await summarizer.AskRegistryAsync(registryQuery, topK: 6);
            if (answer != null)
            {
                AnsiConsole.MarkupLine($"[green]Answer:[/] {answer.Description}");
                if (answer.RelatedColumns.Count > 0)
                {
                    AnsiConsole.MarkupLine($"[dim]Context:[/] {string.Join(", ", answer.RelatedColumns)}");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[red]No answer produced (registry empty or LLM unavailable).[/]");
            }
            return;
        }

        // Directory session mode: profile all files in directory with cross-file summary
        if (isDirectorySession && directoryFiles != null && directoryFiles.Count > 0)
        {
            AnsiConsole.MarkupLine($"[cyan]Directory Session:[/] {Path.GetFullPath(file!)}");
            AnsiConsole.MarkupLine($"[cyan]Files found:[/] {directoryFiles.Count}");
            AnsiConsole.WriteLine();
            
            // Group files by extension for display
            var byExt = directoryFiles.GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
                .OrderByDescending(g => g.Count());
            foreach (var group in byExt)
            {
                AnsiConsole.MarkupLine($"  [dim]{group.Key}:[/] {group.Count()} file(s)");
            }
            AnsiConsole.WriteLine();
            
            // Track all profiles for cross-file summary
            var allProfiles = new List<(string FilePath, DataProfile Profile, List<string> Tables)>();
            var totalRows = 0L;
            var totalColumns = 0;
            var allAlerts = new List<(string File, DataAlert Alert)>();
            var startTime = DateTime.UtcNow;
            
            // Process each file
            var fileIndex = 0;
            foreach (var filePath in directoryFiles.OrderBy(f => f))
            {
                fileIndex++;
                var fileName = Path.GetFileName(filePath);
                var fileExt = Path.GetExtension(filePath).ToLowerInvariant();
                var isFileSqlite = fileExt is ".sqlite" or ".db" or ".sqlite3";
                
                AnsiConsole.Write(new Rule($"[cyan][[{fileIndex}/{directoryFiles.Count}]] {Markup.Escape(fileName)}[/]").LeftJustified());
                
                try
                {
                    if (isFileSqlite)
                    {
                        // SQLite: discover and profile all tables
                        var tables = await DiscoverSqliteTablesAsync(filePath);
                        if (tables.Count == 0)
                        {
                            AnsiConsole.MarkupLine($"  [yellow]No tables found[/]");
                            continue;
                        }
                        
                        AnsiConsole.MarkupLine($"  [dim]Tables:[/] {string.Join(", ", tables)}");
                        
                        foreach (var tableName in tables)
                        {
                            var tableReport = await AnsiConsole.Status()
                                .Spinner(Spinner.Known.Dots)
                                .StartAsync($"Profiling {tableName}...", async ctx =>
                                {
                                    profileOptions.OnStatusUpdate = status => ctx.Status(status);
                                    return await summarizer.SummarizeAsync(filePath, tableName, useLlm: !noLlm, maxLlmInsights: 3);
                                });
                            
                            allProfiles.Add((filePath, tableReport.Profile, [tableName]));
                            totalRows += tableReport.Profile.RowCount;
                            totalColumns += tableReport.Profile.ColumnCount;
                            foreach (var alert in tableReport.Profile.Alerts)
                            {
                                allAlerts.Add(($"{fileName}:{tableName}", alert));
                            }
                            
                            AnsiConsole.MarkupLine($"    [dim]{tableName}:[/] {tableReport.Profile.RowCount:N0} rows, {tableReport.Profile.ColumnCount} cols");
                        }
                    }
                    else
                    {
                        // Regular file (CSV, Excel, Parquet, JSON)
                        var fileReport = await AnsiConsole.Status()
                            .Spinner(Spinner.Known.Dots)
                            .StartAsync($"Profiling...", async ctx =>
                            {
                                profileOptions.OnStatusUpdate = status => ctx.Status(status);
                                return await summarizer.SummarizeAsync(filePath, null, useLlm: !noLlm, maxLlmInsights: 3);
                            });
                        
                        allProfiles.Add((filePath, fileReport.Profile, []));
                        totalRows += fileReport.Profile.RowCount;
                        totalColumns += fileReport.Profile.ColumnCount;
                        foreach (var alert in fileReport.Profile.Alerts)
                        {
                            allAlerts.Add((fileName, alert));
                        }
                        
                        AnsiConsole.MarkupLine($"  [dim]Rows:[/] {fileReport.Profile.RowCount:N0}  [dim]Cols:[/] {fileReport.Profile.ColumnCount}");
                        if (fileReport.Profile.Alerts.Count > 0)
                        {
                            AnsiConsole.MarkupLine($"  [dim]Alerts:[/] {fileReport.Profile.Alerts.Count}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"  [red]Error:[/] {ex.Message}");
                }
                
                AnsiConsole.WriteLine();
            }
            
            // Cross-file summary
            var duration = DateTime.UtcNow - startTime;
            AnsiConsole.Write(new Rule("[green]Cross-File Summary[/]").DoubleBorder());
            AnsiConsole.WriteLine();
            
            AnsiConsole.MarkupLine($"[cyan]Directory:[/] {Path.GetFullPath(file!)}");
            AnsiConsole.MarkupLine($"[cyan]Files Processed:[/] {allProfiles.Count}");
            AnsiConsole.MarkupLine($"[cyan]Total Rows:[/] {totalRows:N0}");
            AnsiConsole.MarkupLine($"[cyan]Total Columns:[/] {totalColumns}");
            AnsiConsole.MarkupLine($"[cyan]Processing Time:[/] {duration.TotalSeconds:F1}s");
            AnsiConsole.WriteLine();
            
            // Summary table
            var summaryTable = new Table();
            summaryTable.Border(TableBorder.Rounded);
            summaryTable.AddColumn("File");
            summaryTable.AddColumn("Rows", c => c.RightAligned());
            summaryTable.AddColumn("Cols", c => c.RightAligned());
            summaryTable.AddColumn("Alerts", c => c.RightAligned());
            summaryTable.AddColumn("Type");
            
            foreach (var (path, profile, tables) in allProfiles)
            {
                var name = Path.GetFileName(path);
                if (tables.Count > 0) name += $" ({string.Join(", ", tables)})";
                var alertCount = profile.Alerts.Count;
                var alertColor = alertCount > 0 ? "yellow" : "dim";
                
                // Detect dominant column types
                var numericCount = profile.Columns.Count(c => c.InferredType == ColumnType.Numeric);
                var textCount = profile.Columns.Count(c => c.InferredType == ColumnType.Text || c.InferredType == ColumnType.Categorical);
                var typeHint = numericCount > textCount ? "numeric-heavy" : "text-heavy";
                
                summaryTable.AddRow(
                    Markup.Escape(name.Length > 40 ? name[..37] + "..." : name),
                    profile.RowCount.ToString("N0"),
                    profile.ColumnCount.ToString(),
                    $"[{alertColor}]{alertCount}[/]",
                    $"[dim]{typeHint}[/]"
                );
            }
            
            AnsiConsole.Write(summaryTable);
            AnsiConsole.WriteLine();
            
            // Aggregate alerts by type
            if (allAlerts.Count > 0)
            {
                AnsiConsole.Write(new Rule("[yellow]Alerts Summary[/]").LeftJustified());
                
                var alertsByType = allAlerts.GroupBy(a => a.Alert.Type)
                    .OrderByDescending(g => g.Count());
                
                foreach (var group in alertsByType.Take(10))
                {
                    AnsiConsole.MarkupLine($"  [yellow]{group.Key}:[/] {group.Count()} occurrence(s)");
                    foreach (var (fileName, alert) in group.Take(3))
                    {
                        AnsiConsole.MarkupLine($"    [dim]- {fileName}: {alert.Column}[/]");
                    }
                    if (group.Count() > 3)
                    {
                        AnsiConsole.MarkupLine($"    [dim]  ... and {group.Count() - 3} more[/]");
                    }
                }
                AnsiConsole.WriteLine();
            }
            
            // Cross-file column comparison (find common columns)
            var allColumnNames = allProfiles
                .SelectMany(p => p.Profile.Columns.Select(c => c.Name.ToLowerInvariant()))
                .GroupBy(n => n)
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToList();
            
            if (allColumnNames.Count > 0)
            {
                AnsiConsole.Write(new Rule("[cyan]Common Columns Across Files[/]").LeftJustified());
                foreach (var col in allColumnNames)
                {
                    AnsiConsole.MarkupLine($"  [cyan]{col.Key}[/]: appears in {col.Count()} file(s)");
                }
                AnsiConsole.WriteLine();
            }
            
            // Output JSON if requested
            if (!string.IsNullOrEmpty(output))
            {
                var sessionResult = new
                {
                    Directory = Path.GetFullPath(file!),
                    FilesProcessed = allProfiles.Count,
                    TotalRows = totalRows,
                    TotalColumns = totalColumns,
                    ProcessingSeconds = duration.TotalSeconds,
                    Files = allProfiles.Select(p => new
                    {
                        Path = p.FilePath,
                        Tables = p.Tables,
                        p.Profile.RowCount,
                        p.Profile.ColumnCount,
                        AlertCount = p.Profile.Alerts.Count,
                        Columns = p.Profile.Columns.Select(c => new { c.Name, Type = c.InferredType.ToString() })
                    }),
                    CommonColumns = allColumnNames.Select(c => new { Column = c.Key, AppearanceCount = c.Count() }),
                    AlertsSummary = allAlerts.GroupBy(a => a.Alert.Type).Select(g => new { Type = g.Key.ToString(), Count = g.Count() })
                };
                
                var json = System.Text.Json.JsonSerializer.Serialize(sessionResult, new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
                
                await File.WriteAllTextAsync(output, json);
                AnsiConsole.MarkupLine($"[green]Session summary written to:[/] {output}");
            }
            
            return;
        }

        // Normal single-file modes below
        var ext = Path.GetExtension(file!).ToLowerInvariant();
        
        // SQLite multi-table handling: if no table specified, discover and profile all tables
        var isSqlite = ext is ".sqlite" or ".db" or ".sqlite3";
        if (isSqlite && string.IsNullOrEmpty(tableOrSheet))
        {
            AnsiConsole.MarkupLine($"[cyan]SQLite Database:[/] {Path.GetFileName(file)}");
            AnsiConsole.WriteLine();
            
            // Discover tables
            var tables = await DiscoverSqliteTablesAsync(file!);
            if (tables.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No tables found in SQLite database.[/]");
                return;
            }
            
            AnsiConsole.MarkupLine($"[cyan]Found {tables.Count} table(s):[/] {string.Join(", ", tables)}");
            AnsiConsole.WriteLine();
            
            // Profile each table
            foreach (var tableName in tables)
            {
                AnsiConsole.Write(new Rule($"[cyan]{tableName}[/]").LeftJustified());
                
                var tableReport = await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"Profiling {tableName}...", async ctx =>
                    {
                        profileOptions.OnStatusUpdate = status => ctx.Status(status);
                        return await summarizer.SummarizeAsync(file!, tableName, useLlm: !noLlm, maxLlmInsights: 5);
                    });
                
                // Display summary for this table
                AnsiConsole.MarkupLine($"  [dim]Rows:[/] {tableReport.Profile.RowCount:N0}");
                AnsiConsole.MarkupLine($"  [dim]Columns:[/] {tableReport.Profile.ColumnCount}");
                if (tableReport.Profile.Alerts.Count > 0)
                    AnsiConsole.MarkupLine($"  [dim]Alerts:[/] {tableReport.Profile.Alerts.Count}");
                AnsiConsole.WriteLine();
            }
            
            return;
        }
        
        AnsiConsole.MarkupLine($"[cyan]File:[/] {Path.GetFileName(file)}");
        AnsiConsole.MarkupLine($"[cyan]Type:[/] {ext.TrimStart('.')}");
        if (isSqlite && !string.IsNullOrEmpty(tableOrSheet))
            AnsiConsole.MarkupLine($"[cyan]Table:[/] {tableOrSheet}");
        if (!noLlm && !string.IsNullOrEmpty(model))
            AnsiConsole.MarkupLine($"[cyan]Model:[/] {model}");
        if (!string.IsNullOrWhiteSpace(onnx))
            AnsiConsole.MarkupLine($"[cyan]ONNX Sentinel:[/] {onnx}");
        AnsiConsole.WriteLine();

        // Query mode (single file)
        if (!string.IsNullOrEmpty(query))
        {
            AnsiConsole.MarkupLine($"[yellow]Question:[/] {query}");
            AnsiConsole.MarkupLine($"[dim]Session:[/] {sessionId}");
            AnsiConsole.WriteLine();

            var insight = await summarizer.AskAsync(file!, query, sheet);
            
            if (insight != null)
            {
                AnsiConsole.MarkupLine($"[green]Answer:[/] {Markup.Escape(insight.Description)}");
                if (!string.IsNullOrEmpty(insight.Sql))
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[dim]SQL:[/]");
                    AnsiConsole.Write(new Panel(insight.Sql).BorderColor(Color.Grey));
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Could not generate answer[/]");
            }
            return;
        }

        // Full summarization with status updates
        var report = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Profiling data...", async ctx =>
            {
                // Wire up status callback to update the spinner text
                profileOptions.OnStatusUpdate = status => ctx.Status(status);
                
                return await summarizer.SummarizeAsync(
                    file!, 
                    sheet, 
                    useLlm: !noLlm,
                    maxLlmInsights: 5
                );
            });

        // Persist markdown report if enabled
        if (reportOptions.GenerateMarkdown && !string.IsNullOrWhiteSpace(resolvedReportPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolvedReportPath)!);
            await File.WriteAllTextAsync(resolvedReportPath, report.MarkdownReport);
            AnsiConsole.MarkupLine($"[green]Report saved to:[/] {resolvedReportPath}");
        }

        // Console output - all sections configurable
        var consoleSettings = settings.ConsoleOutput;
        
        // Create PII redaction service for privacy-safe output
        var piiRedactor = new PiiRedactionService(settings.PiiDisplay);
        
        // Helper: Get redacted value for display
        string GetSafeValue(string value, string columnName)
        {
            var piiResult = report.Profile.PiiResults.FirstOrDefault(p => p.ColumnName == columnName);
            if (piiResult?.IsPii == true && piiResult.PrimaryType.HasValue)
            {
                return piiRedactor.RedactValue(value, piiResult.PrimaryType.Value);
            }
            return value;
        }
        
        // Summary section
        if (consoleSettings.ShowSummary)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[cyan]Summary[/]").LeftJustified());
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine(Markup.Escape(report.ExecutiveSummary));
            AnsiConsole.WriteLine();
        }

        // Focus findings section (off by default)
        if (consoleSettings.ShowFocusFindings && report.FocusFindings.Count > 0)
        {
            AnsiConsole.Write(new Rule("[cyan]Focus Findings[/]").LeftJustified());
            foreach (var kvp in report.FocusFindings)
            {
                AnsiConsole.MarkupLine($"[bold]? {kvp.Key}[/]");
                AnsiConsole.WriteLine(Markup.Escape(kvp.Value));
                AnsiConsole.WriteLine();
            }
        }

        // Column table
        if (consoleSettings.ShowColumnTable)
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Column")
                .AddColumn("Type")
                .AddColumn("Nulls")
                .AddColumn("Unique")
                .AddColumn("Stats");

            foreach (var col in report.Profile.Columns)
            {
                var stats = col.InferredType switch
                {
                    ColumnType.Numeric => $"μ={col.Mean:F1}, σ={col.StdDev:F1}, range={col.Min:F1}-{col.Max:F1}",
                    ColumnType.Categorical when col.TopValues?.Count > 0 => $"top: {GetSafeValue(col.TopValues[0].Value ?? "", col.Name)}",
                    ColumnType.DateTime => $"{col.MinDate:yyyy-MM-dd} → {col.MaxDate:yyyy-MM-dd}",
                    _ => "-"
                };

                table.AddRow(
                    Markup.Escape(col.Name),
                    col.InferredType.ToString(),
                    $"{col.NullPercent:F1}%",
                    col.UniqueCount.ToString("N0"),
                    stats);
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            
            // Mini bar charts for categorical top values (if interesting and enabled)
            if (consoleSettings.ShowCharts)
            {
                var categoricalCols = report.Profile.Columns
                    .Where(c => c.InferredType == ColumnType.Categorical && c.TopValues?.Count >= 3 && c.UniqueCount <= 15)
                    .OrderByDescending(c => c.InterestScore)
                    .Take(2);
                
                foreach (var col in categoricalCols)
                {
                    var chart = new BarChart()
                        .Width(60)
                        .Label($"[yellow]{Markup.Escape(col.Name)}[/]")
                        .LeftAlignLabel();
                    
                    foreach (var tv in col.TopValues!.Take(5))
                    {
                        var safeValue = GetSafeValue(tv.Value ?? "(null)", col.Name);
                        var label = safeValue.Length > 20 ? safeValue[..17] + "..." : safeValue;
                        chart.AddItem(Markup.Escape(label), (int)tv.Count, Color.Yellow);
                    }
                    
                    AnsiConsole.Write(chart);
                    AnsiConsole.WriteLine();
                }
            }
        }

        // Alerts section
        if (consoleSettings.ShowAlerts && report.Profile.Alerts.Count > 0)
        {
            AnsiConsole.Write(new Rule("[yellow]Alerts[/]").LeftJustified());
            foreach (var alert in report.Profile.Alerts.Take(consoleSettings.MaxAlerts))
            {
                var color = alert.Severity switch
                {
                    AlertSeverity.Error => "red",
                    AlertSeverity.Warning => "yellow",
                    _ => "blue"
                };
                AnsiConsole.MarkupLine($"[{color}]- {Markup.Escape(alert.Column)}: {Markup.Escape(alert.Message)}[/]");
            }
            if (report.Profile.Alerts.Count > consoleSettings.MaxAlerts)
            {
                AnsiConsole.MarkupLine($"[dim]... and {report.Profile.Alerts.Count - consoleSettings.MaxAlerts} more alerts[/]");
            }
            AnsiConsole.WriteLine();
        }

        // Insights section
        if (consoleSettings.ShowInsights && report.Profile.Insights.Count > 0)
        {
            AnsiConsole.Write(new Rule("[green]Insights[/]").LeftJustified());
            foreach (var insight in report.Profile.Insights.OrderByDescending(i => i.Score).Take(consoleSettings.MaxInsights))
            {
                var scoreText = insight.Score > 0 ? $" (score {insight.Score:F2})" : string.Empty;
                AnsiConsole.MarkupLine($"[bold]{insight.Title}[/]{scoreText}");
                AnsiConsole.WriteLine(Markup.Escape(insight.Description));
                AnsiConsole.WriteLine();
            }
        }
        
        // Interactive mode - continue asking questions
        if (interactive)
        {
            await RunInteractiveMode(summarizer, report, file!, sheet, verbose, sessionId, noLlm);
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.WriteException(ex);
    }
});
 
var parseResult = rootCommand.Parse(args);
var result = await parseResult.InvokeAsync();
return Environment.ExitCode != 0 ? Environment.ExitCode : result;

// Helper function to show the ASCII banner
static void ShowBanner()
{
    AnsiConsole.Write(new FigletText("DataSumma").Color(Color.Cyan1));
    AnsiConsole.Write(new FigletText("    rizer").Color(Color.Cyan1));
}

// Interactive conversation mode
static async Task RunInteractiveMode(
    DataSummarizerService summarizer, 
    DataSummaryReport report,
    string file,
    string? sheet,
    bool verbose,
    string sessionId,
    bool noLlm = false)
{
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[green]Interactive Mode[/]").LeftJustified());
    if (noLlm)
    {
        AnsiConsole.MarkupLine("[dim]LLM disabled. Use '/' commands to explore data. Type '/' for command list.[/]");
    }
    else
    {
        AnsiConsole.MarkupLine("[dim]Ask questions about your data. Type '/' for commands.[/]");
    }
    AnsiConsole.MarkupLine($"[dim]Session: {sessionId}[/]\n");
    
    // Track current output profile
    var currentProfile = "Default";
    var availableProfiles = new Dictionary<string, (OutputProfileConfig Config, string Description)>(StringComparer.OrdinalIgnoreCase)
    {
        ["Default"] = (OutputProfileConfig.Default, "Balanced output for interactive use"),
        ["Tool"] = (OutputProfileConfig.Tool, "Minimal JSON output for MCP/agent consumption"),
        ["Brief"] = (OutputProfileConfig.Brief, "Quick overview - summary and alerts only"),
        ["Detailed"] = (OutputProfileConfig.Detailed, "Full analysis with all sections"),
        ["Markdown"] = (OutputProfileConfig.MarkdownFocus, "Focus on markdown report generation")
    };
    
    // Define available commands for autocomplete
    var slashCommands = new Dictionary<string, string>
    {
        ["/exit"] = "Exit interactive mode",
        ["/quit"] = "Exit interactive mode (alias)",
        ["/help"] = "Show available commands",
        ["/tools"] = "Show available analysis tools",
        ["/profiles"] = "List output profiles",
        ["/profile"] = "Switch output profile (e.g., /profile Tool)",
        ["/status"] = "Show session status",
        ["/columns"] = "List all columns with types",
        ["/column"] = "Show details for a column (e.g., /column Age)",
        ["/alerts"] = "Show data quality alerts",
        ["/insights"] = "Show generated insights",
        ["/summary"] = "Show data summary",
        ["/verbose"] = "Toggle verbose mode"
    };
    
    while (true)
    {
        var question = AnsiConsole.Ask<string>("[cyan]>[/] ");
        
        if (string.IsNullOrWhiteSpace(question))
            continue;
        
        var trimmed = question.Trim();
        
        // System commands start with /
        if (trimmed.StartsWith('/'))
        {
            var cmd = trimmed[1..].ToLowerInvariant();
            
            // Just "/" - show interactive command selector
            if (string.IsNullOrEmpty(cmd))
            {
                var selectedCmd = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[cyan]Select a command:[/]")
                        .PageSize(12)
                        .HighlightStyle(new Style(foreground: Color.Cyan1))
                        .AddChoices(slashCommands.Select(kv => $"{kv.Key,-12} [dim]{kv.Value}[/]"))
                );
                
                // Extract just the command part
                var selectedParts = selectedCmd.Split(' ', 2);
                trimmed = selectedParts[0];
                cmd = trimmed[1..].ToLowerInvariant();
                
                // If it's a command that needs an argument, prompt for it
                if (cmd is "column" or "col" or "profile" or "mode")
                {
                    if (cmd is "column" or "col")
                    {
                        var colNames = report.Profile.Columns.Select(c => c.Name).ToList();
                        if (colNames.Count > 0)
                        {
                            var selectedCol = AnsiConsole.Prompt(
                                new SelectionPrompt<string>()
                                    .Title("[cyan]Select a column:[/]")
                                    .PageSize(15)
                                    .HighlightStyle(new Style(foreground: Color.Cyan1))
                                    .AddChoices(colNames)
                            );
                            ShowColumnDetails(report, selectedCol);
                            continue;
                        }
                    }
                    else if (cmd is "profile" or "mode")
                    {
                        var profileNames = availableProfiles.Keys.ToList();
                        var selectedProfile = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title("[cyan]Select a profile:[/]")
                                .HighlightStyle(new Style(foreground: Color.Cyan1))
                                .AddChoices(profileNames.Select(p => $"{p,-12} [dim]{availableProfiles[p].Description}[/]"))
                        );
                        var profileName = selectedProfile.Split(' ')[0];
                        if (availableProfiles.TryGetValue(profileName, out var profileInfo))
                        {
                            currentProfile = profileName;
                            AnsiConsole.MarkupLine($"[green]Switched to '{profileName}' profile[/]");
                            AnsiConsole.MarkupLine($"[dim]{profileInfo.Description}[/]\n");
                        }
                        continue;
                    }
                }
            }
            
            // Partial command - try to autocomplete
            if (cmd.Length > 0 && !cmd.Contains(' '))
            {
                var matches = slashCommands.Keys
                    .Where(k => k.StartsWith("/" + cmd, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                
                if (matches.Count == 1)
                {
                    // Single match - use it
                    cmd = matches[0][1..]; // Remove the leading /
                }
                else if (matches.Count > 1 && matches.Count <= 5)
                {
                    // Multiple matches - show selector
                    var selectedCmd = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title($"[cyan]Commands matching '/{Markup.Escape(cmd)}':[/]")
                            .HighlightStyle(new Style(foreground: Color.Cyan1))
                            .AddChoices(matches.Select(m => $"{m,-12} [dim]{slashCommands[m]}[/]"))
                    );
                    var selectedParts = selectedCmd.Split(' ', 2);
                    cmd = selectedParts[0][1..];
                }
            }
            
            var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0];
            var arg = parts.Length > 1 ? parts[1] : "";
            
            switch (command)
            {
                case "exit" or "quit" or "q":
                    AnsiConsole.MarkupLine("[dim]Goodbye![/]");
                    return;
                    
                case "help":
                    ShowCommands();
                    break;
                    
                case "tools":
                    ShowAvailableTools();
                    break;
                    
                case "profiles" or "modes":
                    ShowOutputProfiles(availableProfiles, currentProfile);
                    break;
                    
                case "profile" or "mode":
                    if (string.IsNullOrEmpty(arg))
                    {
                        AnsiConsole.MarkupLine($"[dim]Current profile:[/] [green]{currentProfile}[/]");
                        AnsiConsole.MarkupLine("[dim]Use '/profiles' to list, '/profile <name>' to switch[/]\n");
                    }
                    else if (availableProfiles.TryGetValue(arg, out var profileInfo))
                    {
                        currentProfile = arg;
                        AnsiConsole.MarkupLine($"[green]Switched to '{arg}' profile[/]");
                        AnsiConsole.MarkupLine($"[dim]{profileInfo.Description}[/]\n");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]Unknown profile: {Markup.Escape(arg)}[/]");
                        AnsiConsole.MarkupLine("[dim]Use '/profiles' to see available options[/]\n");
                    }
                    break;
                    
                case "status" or "info":
                    ShowSessionStatus(report, file, sessionId, currentProfile, verbose);
                    break;
                    
                case "columns" or "cols":
                    ShowColumnsSummary(report);
                    break;
                    
                case "column" or "col":
                    if (string.IsNullOrEmpty(arg))
                        AnsiConsole.MarkupLine("[yellow]Usage: /column <name>[/]\n");
                    else
                        ShowColumnDetails(report, arg);
                    break;
                    
                case "alerts" or "warnings":
                    ShowAlerts(report);
                    break;
                    
                case "insights":
                    ShowInsights(report);
                    break;
                    
                case "summary":
                    ShowDataSummary(report, file);
                    break;
                    
                case "verbose":
                    verbose = !verbose;
                    AnsiConsole.MarkupLine($"[dim]Verbose mode:[/] {(verbose ? "[green]on[/]" : "[dim]off[/]")}\n");
                    break;
                    
                default:
                    AnsiConsole.MarkupLine($"[yellow]Unknown command: /{Markup.Escape(command)}[/]");
                    AnsiConsole.MarkupLine("[dim]Type '/' for available commands[/]\n");
                    break;
            }
            continue;
        }
        
        // Data questions require LLM
        if (noLlm)
        {
            AnsiConsole.MarkupLine("[yellow]LLM is disabled. Use '/' commands to explore data.[/]");
            AnsiConsole.MarkupLine("[dim]Type '/' to see available commands[/]\n");
            continue;
        }
        
        try
        {
            var answer = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Thinking...", async ctx =>
                {
                    return await summarizer.AskAsync(file, question, sheet);
                });
            
            if (answer != null)
            {
                AnsiConsole.MarkupLine($"\n[green]Answer:[/] {Markup.Escape(answer.Description)}\n");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Could not generate an answer for that question.[/]\n");
            }
        }
        catch (Exception ex)
        {
            if (verbose)
                AnsiConsole.WriteException(ex);
            else
                AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]\n");
        }
    }
}

// Helper to resolve output profile from settings or CLI override
static OutputProfileConfig ResolveOutputProfile(DataSummarizerSettings settings, string? profileName)
{
    if (string.IsNullOrEmpty(profileName))
        return settings.GetActiveProfile();
    
    // Try to find in configured profiles
    if (settings.OutputProfiles.TryGetValue(profileName, out var profile))
        return profile;
    
    // Try built-in profiles by name (case-insensitive)
    return profileName.ToLowerInvariant() switch
    {
        "tool" => OutputProfileConfig.Tool,
        "brief" => OutputProfileConfig.Brief,
        "detailed" => OutputProfileConfig.Detailed,
        "markdown" => OutputProfileConfig.MarkdownFocus,
        _ => settings.GetActiveProfile()
    };
}

// Build ToolProfile from DataProfile (with optional quick mode for minimal output)
static ToolProfile BuildToolProfile(DataProfile profile, bool quickMode)
{
    return new ToolProfile
    {
        SourcePath = profile.SourcePath,
        RowCount = profile.RowCount,
        ColumnCount = profile.ColumnCount,
        ExecutiveSummary = quickMode 
            ? $"{profile.RowCount:N0} rows, {profile.ColumnCount} columns"
            : $"{profile.RowCount:N0} rows, {profile.ColumnCount} columns. " +
              $"{profile.Columns.Count(c => c.NullPercent > 0)} columns have nulls. " +
              $"{profile.Alerts.Count} alerts.",
        Columns = profile.Columns.Select(c => new ToolColumnProfile
        {
            Name = c.Name,
            Type = c.InferredType.ToString(),
            Role = c.SemanticRole != SemanticRole.Unknown ? c.SemanticRole.ToString() : null,
            NullPercent = Math.Round(c.NullPercent, 2),
            UniqueCount = c.UniqueCount,
            UniquePercent = Math.Round(c.UniquePercent, 2),
            Distribution = quickMode ? null : c.Distribution?.ToString(),
            Trend = quickMode ? null : c.Trend?.Direction.ToString(),
            Periodicity = quickMode || c.Periodicity == null ? null : new ToolPeriodicityInfo
            {
                Period = c.Periodicity.DominantPeriod,
                Confidence = Math.Round(c.Periodicity.Confidence, 3),
                Interpretation = c.Periodicity.SuggestedInterpretation
            },
            Stats = quickMode ? new ToolColumnStats
            {
                // Quick mode: only essential stats
                Min = c.Min,
                Max = c.Max,
                Mean = c.Mean != null ? Math.Round(c.Mean.Value, 4) : null,
                TopValue = c.TopValues?.FirstOrDefault()?.Value,
                TopValuePercent = c.TopValues?.FirstOrDefault()?.Percent
            } : new ToolColumnStats
            {
                Min = c.Min,
                Max = c.Max,
                Mean = c.Mean,
                Median = c.Median,
                StdDev = c.StdDev,
                Skewness = c.Skewness,
                Kurtosis = c.Kurtosis,
                OutlierCount = c.OutlierCount > 0 ? c.OutlierCount : null,
                ZeroCount = c.ZeroCount > 0 ? c.ZeroCount : null,
                CoefficientOfVariation = c.CoefficientOfVariation,
                Iqr = c.Iqr,
                TopValue = c.TopValues?.FirstOrDefault()?.Value,
                TopValuePercent = c.TopValues?.FirstOrDefault()?.Percent,
                ImbalanceRatio = c.ImbalanceRatio,
                Entropy = c.Entropy,
                MinDate = c.MinDate?.ToString("yyyy-MM-dd"),
                MaxDate = c.MaxDate?.ToString("yyyy-MM-dd"),
                DateGapDays = c.DateGapDays,
                DateSpanDays = c.DateSpanDays,
                AvgLength = c.AvgLength,
                MaxLength = c.MaxLength,
                MinLength = c.MinLength,
                EmptyStringCount = c.EmptyStringCount > 0 ? c.EmptyStringCount : null
            }
        }).ToList(),
        Alerts = quickMode 
            ? profile.Alerts.Where(a => a.Severity >= AlertSeverity.Warning).Take(5).Select(a => new ToolAlert
            {
                Severity = a.Severity.ToString(),
                Column = a.Column,
                Type = a.Type.ToString(),
                Message = a.Message
            }).ToList()
            : profile.Alerts.Select(a => new ToolAlert
            {
                Severity = a.Severity.ToString(),
                Column = a.Column,
                Type = a.Type.ToString(),
                Message = a.Message
            }).ToList(),
        Insights = quickMode 
            ? [] 
            : profile.Insights.Take(10).Select(i => new ToolInsight
            {
                Title = i.Title,
                Description = i.Description,
                Score = i.Score,
                Source = i.Source.ToString(),
                RelatedColumns = i.RelatedColumns.Count > 0 ? i.RelatedColumns : null
            }).ToList(),
        Correlations = quickMode 
            ? null 
            : profile.Correlations.Take(10).Select(c => new ToolCorrelation
            {
                Column1 = c.Column1,
                Column2 = c.Column2,
                Coefficient = c.Correlation,
                Strength = c.Strength
            }).ToList(),
        TargetAnalysis = profile.Target != null ? new ToolTargetAnalysis
        {
            TargetColumn = profile.Target.ColumnName,
            IsBinary = profile.Target.IsBinary,
            ClassDistribution = profile.Target.ClassDistribution.ToDictionary(
                kv => kv.Key, 
                kv => Math.Round(kv.Value * 100, 2)),
            TopDrivers = profile.Target.FeatureEffects.Take(5).Select(e => new ToolFeatureDriver
            {
                Feature = e.Feature,
                Magnitude = Math.Round(e.Magnitude, 4),
                Support = Math.Round(e.Support, 4),
                Summary = e.Summary,
                Metric = e.Metric
            }).ToList()
        } : null
    };
}

// Compute drift summary between current and baseline profiles
static ToolDriftSummary ComputeDrift(DataProfile current, DataProfile baseline, StoredProfileInfo baselineInfo)
{
    var comparator = new ProfileComparator();
    var diff = comparator.Compare(baseline, current);
    
    return new ToolDriftSummary
    {
        BaselineProfileId = baselineInfo.Id,
        BaselineDate = baselineInfo.StoredAt.ToString("o"),
        DriftScore = Math.Round(diff.OverallDriftScore, 4),
        HasSignificantDrift = diff.HasSignificantDrift,
        RowCountChangePercent = Math.Round(diff.RowCountChange.PercentChange, 2),
        DriftedColumnCount = diff.ColumnDiffs.Count(c => c.Psi >= 0.1),
        RemovedColumns = diff.SchemaChanges.RemovedColumns.Count > 0 ? diff.SchemaChanges.RemovedColumns : null,
        AddedColumns = diff.SchemaChanges.AddedColumns.Count > 0 ? diff.SchemaChanges.AddedColumns : null,
        Summary = diff.Summary,
        Recommendations = diff.Recommendations.Count > 0 ? diff.Recommendations : null
    };
}

// Format constraint validation as markdown
static string FormatConstraintValidationMarkdown(ConstraintValidationResult result)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"# Constraint Validation Report");
    sb.AppendLine();
    sb.AppendLine($"**Suite:** {result.SuiteName}");
    sb.AppendLine($"**Source:** {result.ProfileSource}");
    sb.AppendLine($"**Validated:** {result.ValidatedAt:yyyy-MM-dd HH:mm:ss}");
    sb.AppendLine();
    sb.AppendLine($"## Summary");
    sb.AppendLine();
    sb.AppendLine($"| Metric | Value |");
    sb.AppendLine($"|--------|-------|");
    sb.AppendLine($"| Pass Rate | {result.PassRate:P1} |");
    sb.AppendLine($"| Passed | {result.PassedConstraints} |");
    sb.AppendLine($"| Failed | {result.FailedConstraints} |");
    sb.AppendLine($"| Total | {result.TotalConstraints} |");
    sb.AppendLine();
    
    if (result.FailedConstraints > 0)
    {
        sb.AppendLine($"## Failed Constraints");
        sb.AppendLine();
        foreach (var failure in result.GetFailures())
        {
            sb.AppendLine($"- **{failure.Constraint.Type}**: {failure.Constraint.Description}");
            if (failure.ActualValue != null)
                sb.AppendLine($"  - Actual: {failure.ActualValue}");
            if (!string.IsNullOrEmpty(failure.Details))
                sb.AppendLine($"  - Details: {failure.Details}");
        }
    }
    else
    {
        sb.AppendLine("All constraints passed!");
    }
    
    return sb.ToString();
}

// Format constraint validation as HTML
static string FormatConstraintValidationHtml(ConstraintValidationResult result)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("<!DOCTYPE html><html><head><style>");
    sb.AppendLine("body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 800px; margin: 0 auto; padding: 20px; }");
    sb.AppendLine("table { border-collapse: collapse; width: 100%; margin: 20px 0; }");
    sb.AppendLine("th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }");
    sb.AppendLine("th { background: #f5f5f5; }");
    sb.AppendLine(".pass { color: green; } .fail { color: red; }");
    sb.AppendLine("</style></head><body>");
    sb.AppendLine($"<h1>Constraint Validation Report</h1>");
    sb.AppendLine($"<p><strong>Suite:</strong> {System.Net.WebUtility.HtmlEncode(result.SuiteName)}</p>");
    sb.AppendLine($"<p><strong>Pass Rate:</strong> <span class='{(result.AllPassed ? "pass" : "fail")}'>{result.PassRate:P1}</span></p>");
    
    sb.AppendLine("<table><tr><th>Status</th><th>Constraint</th><th>Actual</th><th>Details</th></tr>");
    foreach (var r in result.Results)
    {
        var status = r.Passed ? "<span class='pass'>PASS</span>" : "<span class='fail'>FAIL</span>";
        sb.AppendLine($"<tr><td>{status}</td><td>{System.Net.WebUtility.HtmlEncode(r.Constraint.Description)}</td><td>{r.ActualValue}</td><td>{System.Net.WebUtility.HtmlEncode(r.Details ?? "")}</td></tr>");
    }
    sb.AppendLine("</table></body></html>");
    
    return sb.ToString();
}

// Format validation/drift as markdown
static string FormatValidationMarkdown(dynamic result, ProfileDiffResult drift)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"# Data Drift Validation Report");
    sb.AppendLine();
    sb.AppendLine($"**Source:** {result.Source}");
    sb.AppendLine($"**Target:** {result.Target}");
    sb.AppendLine();
    sb.AppendLine($"## Summary");
    sb.AppendLine();
    sb.AppendLine($"| Metric | Value |");
    sb.AppendLine($"|--------|-------|");
    sb.AppendLine($"| Drift Score | {result.DriftScore:F3} |");
    sb.AppendLine($"| Anomaly Score | {result.AnomalyScore.OverallScore:F3} ({result.AnomalyScore.Interpretation}) |");
    sb.AppendLine($"| Significant Drift | {(drift.HasSignificantDrift ? "Yes" : "No")} |");
    sb.AppendLine();
    sb.AppendLine(drift.Summary);
    sb.AppendLine();
    
    if (drift.Recommendations.Count > 0)
    {
        sb.AppendLine("## Recommendations");
        sb.AppendLine();
        foreach (var rec in drift.Recommendations)
        {
            sb.AppendLine($"- {rec}");
        }
    }
    
    return sb.ToString();
}

// Format validation/drift as HTML
static string FormatValidationHtml(dynamic result, ProfileDiffResult drift)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("<!DOCTYPE html><html><head><style>");
    sb.AppendLine("body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 900px; margin: 0 auto; padding: 20px; }");
    sb.AppendLine("table { border-collapse: collapse; width: 100%; margin: 20px 0; }");
    sb.AppendLine("th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }");
    sb.AppendLine("th { background: #f5f5f5; }");
    sb.AppendLine(".good { color: green; } .warning { color: orange; } .bad { color: red; }");
    sb.AppendLine("</style></head><body>");
    sb.AppendLine($"<h1>Data Drift Validation Report</h1>");
    sb.AppendLine($"<p><strong>Source:</strong> {System.Net.WebUtility.HtmlEncode((string)result.Source)}</p>");
    sb.AppendLine($"<p><strong>Target:</strong> {System.Net.WebUtility.HtmlEncode((string)result.Target)}</p>");
    
    var driftClass = result.DriftScore < 0.3 ? "good" : result.DriftScore < 0.6 ? "warning" : "bad";
    sb.AppendLine($"<p><strong>Drift Score:</strong> <span class='{driftClass}'>{result.DriftScore:F3}</span></p>");
    sb.AppendLine($"<p><strong>Summary:</strong> {System.Net.WebUtility.HtmlEncode(drift.Summary)}</p>");
    
    if (drift.Recommendations.Count > 0)
    {
        sb.AppendLine("<h2>Recommendations</h2><ul>");
        foreach (var rec in drift.Recommendations)
        {
            sb.AppendLine($"<li>{System.Net.WebUtility.HtmlEncode(rec)}</li>");
        }
        sb.AppendLine("</ul>");
    }
    
    sb.AppendLine("</body></html>");
    return sb.ToString();
}

// Format segment comparison as markdown
static string FormatSegmentComparisonMarkdown(SegmentComparison comparison, AnomalyScoreResult anomalyA, AnomalyScoreResult anomalyB)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"# Segment Comparison Report");
    sb.AppendLine();
    sb.AppendLine($"| Segment | Rows | Anomaly Score |");
    sb.AppendLine($"|---------|------|---------------|");
    sb.AppendLine($"| {comparison.SegmentAName} | {comparison.SegmentARowCount:N0} | {anomalyA.OverallScore:F3} ({anomalyA.Interpretation}) |");
    sb.AppendLine($"| {comparison.SegmentBName} | {comparison.SegmentBRowCount:N0} | {anomalyB.OverallScore:F3} ({anomalyB.Interpretation}) |");
    sb.AppendLine();
    sb.AppendLine($"**Similarity:** {comparison.Similarity:P1}");
    sb.AppendLine();
    sb.AppendLine("## Insights");
    sb.AppendLine();
    foreach (var insight in comparison.Insights)
    {
        sb.AppendLine($"- {insight}");
    }
    sb.AppendLine();
    
    if (comparison.ColumnComparisons.Count > 0)
    {
        sb.AppendLine("## Top Column Differences");
        sb.AppendLine();
        sb.AppendLine("| Column | Type | Distance | A | B |");
        sb.AppendLine("|--------|------|----------|---|---|");
        foreach (var col in comparison.ColumnComparisons.Take(10))
        {
            var valA = col.ColumnType == ColumnType.Numeric ? col.MeanA?.ToString("F2") ?? "-" : col.ModeA ?? "-";
            var valB = col.ColumnType == ColumnType.Numeric ? col.MeanB?.ToString("F2") ?? "-" : col.ModeB ?? "-";
            sb.AppendLine($"| {col.ColumnName} | {col.ColumnType} | {col.Distance:F3} | {valA} | {valB} |");
        }
    }
    
    return sb.ToString();
}

// Format segment comparison as HTML
static string FormatSegmentComparisonHtml(SegmentComparison comparison, AnomalyScoreResult anomalyA, AnomalyScoreResult anomalyB)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("<!DOCTYPE html><html><head><style>");
    sb.AppendLine("body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 900px; margin: 0 auto; padding: 20px; }");
    sb.AppendLine("table { border-collapse: collapse; width: 100%; margin: 20px 0; }");
    sb.AppendLine("th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }");
    sb.AppendLine("th { background: #f5f5f5; }");
    sb.AppendLine(".high { color: green; } .medium { color: orange; } .low { color: red; }");
    sb.AppendLine("</style></head><body>");
    sb.AppendLine($"<h1>Segment Comparison Report</h1>");
    
    var simClass = comparison.Similarity >= 0.8 ? "high" : comparison.Similarity >= 0.5 ? "medium" : "low";
    sb.AppendLine($"<p><strong>Similarity:</strong> <span class='{simClass}'>{comparison.Similarity:P1}</span></p>");
    
    sb.AppendLine("<table><tr><th>Segment</th><th>Rows</th><th>Anomaly Score</th></tr>");
    sb.AppendLine($"<tr><td>{System.Net.WebUtility.HtmlEncode(comparison.SegmentAName)}</td><td>{comparison.SegmentARowCount:N0}</td><td>{anomalyA.OverallScore:F3}</td></tr>");
    sb.AppendLine($"<tr><td>{System.Net.WebUtility.HtmlEncode(comparison.SegmentBName)}</td><td>{comparison.SegmentBRowCount:N0}</td><td>{anomalyB.OverallScore:F3}</td></tr>");
    sb.AppendLine("</table>");
    
    sb.AppendLine("<h2>Insights</h2><ul>");
    foreach (var insight in comparison.Insights)
    {
        sb.AppendLine($"<li>{System.Net.WebUtility.HtmlEncode(insight)}</li>");
    }
    sb.AppendLine("</ul>");
    
    if (comparison.ColumnComparisons.Count > 0)
    {
        sb.AppendLine("<h2>Top Column Differences</h2>");
        sb.AppendLine("<table><tr><th>Column</th><th>Type</th><th>Distance</th><th>A</th><th>B</th></tr>");
        foreach (var col in comparison.ColumnComparisons.Take(10))
        {
            var valA = col.ColumnType == ColumnType.Numeric ? col.MeanA?.ToString("F2") ?? "-" : col.ModeA ?? "-";
            var valB = col.ColumnType == ColumnType.Numeric ? col.MeanB?.ToString("F2") ?? "-" : col.ModeB ?? "-";
            sb.AppendLine($"<tr><td>{col.ColumnName}</td><td>{col.ColumnType}</td><td>{col.Distance:F3}</td><td>{valA}</td><td>{valB}</td></tr>");
        }
        sb.AppendLine("</table>");
    }
    
    sb.AppendLine("</body></html>");
    return sb.ToString();
}

// Interactive profile management menu
static async Task ShowProfileManagementMenu(string? storePath)
{
    var store = new ProfileStore(storePath);
    
    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[cyan]Profile Store Management[/]").LeftJustified());
        AnsiConsole.WriteLine();
        
        var profiles = store.ListAll(100);
        if (profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No stored profiles found.[/]");
            AnsiConsole.MarkupLine("\n[dim]Press any key to exit...[/]");
            Console.ReadKey(true);
            return;
        }
        
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]What would you like to do?[/]")
                .PageSize(10)
                .AddChoices(new[] {
                    "📋 List all profiles",
                    "🔍 View profile details",
                    "📊 Compare two profiles", 
                    "🗑️  Delete profile",
                    "🚫 Exclude from baseline",
                    "📌 Pin as baseline",
                    "🏷️  Add tags/notes",
                    "🧹 Prune old profiles",
                    "📈 Show statistics",
                    "❌ Exit"
                }));
        
        try
        {
            switch (choice)
            {
                case "📋 List all profiles":
                    await ListProfiles(store);
                    break;
                    
                case "🔍 View profile details":
                    await ViewProfileDetails(store, profiles);
                    break;
                    
                case "📊 Compare two profiles":
                    await CompareProfiles(store, profiles);
                    break;
                    
                case "🗑️  Delete profile":
                    await DeleteProfile(store, profiles);
                    break;
                    
                case "🚫 Exclude from baseline":
                    await ExcludeFromBaseline(store, profiles);
                    break;
                    
                case "📌 Pin as baseline":
                    await PinAsBaseline(store, profiles);
                    break;
                    
                case "🏷️  Add tags/notes":
                    await AddTagsNotes(store, profiles);
                    break;
                    
                case "🧹 Prune old profiles":
                    await PruneProfiles(store);
                    break;
                    
                case "📈 Show statistics":
                    await ShowStoreStatistics(store);
                    break;
                    
                case "❌ Exit":
                    return;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
            Console.ReadKey(true);
        }
    }
}

static async Task ListProfiles(ProfileStore store)
{
    var profiles = store.ListAll(100);
    
    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("ID")
        .AddColumn("File")
        .AddColumn("Rows")
        .AddColumn("Cols")
        .AddColumn("Schema")
        .AddColumn("Stored")
        .AddColumn("Flags");
    
    foreach (var p in profiles)
    {
        var flags = new List<string>();
        if (p.IsPinnedBaseline) flags.Add("📌");
        if (p.ExcludeFromBaseline) flags.Add("🚫");
        if (!string.IsNullOrEmpty(p.Tags)) flags.Add("🏷️");
        
        table.AddRow(
            p.Id,
            Markup.Escape(Path.GetFileName(p.FileName)),
            p.RowCount.ToString("N0"),
            p.ColumnCount.ToString(),
            p.SchemaHash[..8],
            p.StoredAt.ToString("yyyy-MM-dd"),
            string.Join(" ", flags));
    }
    
    AnsiConsole.WriteLine();
    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine($"\n[dim]Total: {profiles.Count} profile(s)  |  📌 = pinned baseline  |  🚫 = excluded  |  🏷️ = has tags[/]");
    AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
    Console.ReadKey(true);
}

static async Task ViewProfileDetails(ProfileStore store, List<StoredProfileInfo> profiles)
{
    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<StoredProfileInfo>()
            .Title("[yellow]Select profile to view:[/]")
            .PageSize(15)
            .UseConverter(p => $"{p.Id} - {Path.GetFileName(p.FileName)} ({p.RowCount:N0} rows)")
            .AddChoices(profiles));
    
    var profile = store.LoadProfile(selected.Id);
    if (profile == null)
    {
        AnsiConsole.MarkupLine("[red]Profile not found[/]");
        return;
    }
    
    AnsiConsole.Clear();
    AnsiConsole.Write(new Rule($"[cyan]Profile: {selected.FileName}[/]").LeftJustified());
    AnsiConsole.WriteLine();
    
    var grid = new Grid()
        .AddColumn()
        .AddColumn()
        .AddRow("[bold]ID:[/]", selected.Id)
        .AddRow("[bold]File:[/]", Markup.Escape(selected.SourcePath))
        .AddRow("[bold]Rows:[/]", selected.RowCount.ToString("N0"))
        .AddRow("[bold]Columns:[/]", selected.ColumnCount.ToString())
        .AddRow("[bold]Schema Hash:[/]", selected.SchemaHash)
        .AddRow("[bold]Stored:[/]", selected.StoredAt.ToString("yyyy-MM-dd HH:mm:ss"))
        .AddRow("[bold]Tags:[/]", selected.Tags ?? "[dim]none[/]")
        .AddRow("[bold]Notes:[/]", selected.Notes ?? "[dim]none[/]");
    
    if (selected.IsPinnedBaseline)
    {
        grid.AddRow("[bold]Baseline:[/]", "[green]📌 Pinned as baseline[/]");
    }
    if (selected.ExcludeFromBaseline)
    {
        grid.AddRow("[bold]Excluded:[/]", "[red]🚫 Excluded from baseline[/]");
    }
    
    AnsiConsole.Write(grid);
    AnsiConsole.WriteLine();
    
    AnsiConsole.MarkupLine($"\n[bold]Columns ({profile.ColumnCount}):[/]");
    foreach (var col in profile.Columns.Take(10))
    {
        AnsiConsole.MarkupLine($"  [cyan]{col.Name}[/]: {col.InferredType} ({col.NullPercent:F1}% null, {col.UniquePercent:F1}% unique)");
    }
    if (profile.ColumnCount > 10)
    {
        AnsiConsole.MarkupLine($"  [dim]... and {profile.ColumnCount - 10} more[/]");
    }
    
    AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
    Console.ReadKey(true);
}

static async Task CompareProfiles(ProfileStore store, List<StoredProfileInfo> profiles)
{
    AnsiConsole.MarkupLine("[yellow]Select baseline profile:[/]");
    var baseline = AnsiConsole.Prompt(
        new SelectionPrompt<StoredProfileInfo>()
            .PageSize(15)
            .UseConverter(p => $"{p.Id} - {Path.GetFileName(p.FileName)} ({p.StoredAt:yyyy-MM-dd})")
            .AddChoices(profiles));
    
    AnsiConsole.MarkupLine("\n[yellow]Select current profile to compare:[/]");
    var current = AnsiConsole.Prompt(
        new SelectionPrompt<StoredProfileInfo>()
            .PageSize(15)
            .UseConverter(p => $"{p.Id} - {Path.GetFileName(p.FileName)} ({p.StoredAt:yyyy-MM-dd})")
            .AddChoices(profiles.Where(p => p.Id != baseline.Id)));
    
    var baselineProfile = store.LoadProfile(baseline.Id);
    var currentProfile = store.LoadProfile(current.Id);
    
    if (baselineProfile == null || currentProfile == null)
    {
        AnsiConsole.MarkupLine("[red]Failed to load profiles[/]");
        AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
        return;
    }
    
    AnsiConsole.Status()
        .Start("Comparing profiles...", ctx =>
        {
            var comparator = new ProfileComparator();
            var diff = comparator.Compare(baselineProfile, currentProfile);
            
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[cyan]Profile Comparison[/]").LeftJustified());
            AnsiConsole.WriteLine();
            
            AnsiConsole.MarkupLine($"[bold]Baseline:[/] {baseline.FileName} ({baseline.StoredAt:yyyy-MM-dd})");
            AnsiConsole.MarkupLine($"[bold]Current:[/] {current.FileName} ({current.StoredAt:yyyy-MM-dd})");
            AnsiConsole.WriteLine();
            
            var driftColor = diff.OverallDriftScore > 0.3 ? "red" : (diff.OverallDriftScore > 0.1 ? "yellow" : "green");
            AnsiConsole.MarkupLine($"[bold]Drift Score:[/] [{driftColor}]{diff.OverallDriftScore:F3}[/]");
            AnsiConsole.MarkupLine($"[bold]Row Count Change:[/] {diff.RowCountChange.PercentChange:+0.0;-0.0}%");
            AnsiConsole.WriteLine();
            
            if (diff.SchemaChanges.HasChanges)
            {
                AnsiConsole.MarkupLine("[red]⚠ Schema Changes Detected[/]");
                if (diff.SchemaChanges.AddedColumns.Count > 0)
                    AnsiConsole.MarkupLine($"  [green]+[/] Added: {string.Join(", ", diff.SchemaChanges.AddedColumns)}");
                if (diff.SchemaChanges.RemovedColumns.Count > 0)
                    AnsiConsole.MarkupLine($"  [red]-[/] Removed: {string.Join(", ", diff.SchemaChanges.RemovedColumns)}");
                AnsiConsole.WriteLine();
            }
            
            if (diff.ColumnDiffs.Count > 0)
            {
                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Column")
                    .AddColumn("Type")
                    .AddColumn("PSI")
                    .AddColumn("KS/JS")
                    .AddColumn("Null Δ");
                
                foreach (var col in diff.ColumnDiffs.OrderByDescending(c => c.Psi ?? c.KsDistance ?? c.JsDivergence ?? 0).Take(10))
                {
                    var metric = col.KsDistance?.ToString("F3") ?? col.JsDivergence?.ToString("F3") ?? "-";
                    var psi = col.Psi?.ToString("F3") ?? "-";
                    var nullDelta = col.NullPercentChange?.AbsoluteChange.ToString("+0.0;-0.0") ?? "-";
                    
                    table.AddRow(
                        col.ColumnName,
                        col.ColumnType.ToString(),
                        psi,
                        metric,
                        nullDelta);
                }
                
                AnsiConsole.MarkupLine("[bold]Top Drifted Columns:[/]");
                AnsiConsole.Write(table);
            }
            
            if (diff.Summary != null)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(diff.Summary)}[/]");
            }
        });
    
    AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
    Console.ReadKey(true);
}

static async Task DeleteProfile(ProfileStore store, List<StoredProfileInfo> profiles)
{
    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<StoredProfileInfo>()
            .Title("[yellow]Select profile to delete:[/]")
            .PageSize(15)
            .UseConverter(p => $"{p.Id} - {Path.GetFileName(p.FileName)} ({p.StoredAt:yyyy-MM-dd})")
            .AddChoices(profiles));
    
    if (!AnsiConsole.Confirm($"[red]Delete profile {selected.Id}?[/]", defaultValue: false))
    {
        return;
    }
    
    if (store.Delete(selected.Id))
    {
        AnsiConsole.MarkupLine($"[green]✓ Deleted profile {selected.Id}[/]");
    }
    else
    {
        AnsiConsole.MarkupLine($"[red]✗ Failed to delete profile {selected.Id}[/]");
    }
    
    AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
    Console.ReadKey(true);
}

static async Task ExcludeFromBaseline(ProfileStore store, List<StoredProfileInfo> profiles)
{
    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<StoredProfileInfo>()
            .Title("[yellow]Select profile to exclude from baseline:[/]")
            .PageSize(15)
            .UseConverter(p => $"{p.Id} - {Path.GetFileName(p.FileName)} ({p.StoredAt:yyyy-MM-dd})")
            .AddChoices(profiles));
    
    selected.ExcludeFromBaseline = !selected.ExcludeFromBaseline;
    store.UpdateMetadata(selected);
    
    var status = selected.ExcludeFromBaseline ? "[red]excluded from[/]" : "[green]included in[/]";
    AnsiConsole.MarkupLine($"[green]✓[/] Profile {selected.Id} is now {status} baseline selection");
    
    AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
    Console.ReadKey(true);
}

static async Task PinAsBaseline(ProfileStore store, List<StoredProfileInfo> profiles)
{
    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<StoredProfileInfo>()
            .Title("[yellow]Select profile to pin as baseline:[/]")
            .PageSize(15)
            .UseConverter(p => $"{p.Id} - {Path.GetFileName(p.FileName)} ({p.StoredAt:yyyy-MM-dd})")
            .AddChoices(profiles));
    
    // Unpin others with same schema
    var schemaHash = selected.SchemaHash;
    foreach (var p in profiles.Where(p => p.SchemaHash == schemaHash && p.Id != selected.Id))
    {
        if (p.IsPinnedBaseline)
        {
            p.IsPinnedBaseline = false;
            store.UpdateMetadata(p);
        }
    }
    
    selected.IsPinnedBaseline = !selected.IsPinnedBaseline;
    store.UpdateMetadata(selected);
    
    var status = selected.IsPinnedBaseline ? "[green]📌 pinned as baseline[/]" : "[yellow]unpinned[/]";
    AnsiConsole.MarkupLine($"[green]✓[/] Profile {selected.Id} is now {status}");
    
    AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
    Console.ReadKey(true);
}

static async Task AddTagsNotes(ProfileStore store, List<StoredProfileInfo> profiles)
{
    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<StoredProfileInfo>()
            .Title("[yellow]Select profile to edit:[/]")
            .PageSize(15)
            .UseConverter(p => $"{p.Id} - {Path.GetFileName(p.FileName)} ({p.StoredAt:yyyy-MM-dd})")
            .AddChoices(profiles));
    
    var tags = AnsiConsole.Ask("[yellow]Tags (comma-separated):[/]", selected.Tags ?? "");
    var notes = AnsiConsole.Ask("[yellow]Notes:[/]", selected.Notes ?? "");
    
    selected.Tags = string.IsNullOrWhiteSpace(tags) ? null : tags;
    selected.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
    
    store.UpdateMetadata(selected);
    
    AnsiConsole.MarkupLine($"[green]✓ Updated metadata for profile {selected.Id}[/]");
    AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
    Console.ReadKey(true);
}

static async Task PruneProfiles(ProfileStore store)
{
    var keep = AnsiConsole.Ask("[yellow]How many profiles to keep per schema?[/]", 3);
    
    if (!AnsiConsole.Confirm($"[yellow]Keep {keep} most recent profiles per schema and delete the rest?[/]", defaultValue: false))
    {
        return;
    }
    
    var pruned = store.PruneOldProfiles(keep);
    AnsiConsole.MarkupLine($"[green]✓ Pruned {pruned} old profile(s)[/]");
    
    AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
    Console.ReadKey(true);
}

static async Task ShowStoreStatistics(ProfileStore store)
{
    var stats = store.GetStatistics();
    
    AnsiConsole.Clear();
    AnsiConsole.Write(new Rule("[cyan]Store Statistics[/]").LeftJustified());
    AnsiConsole.WriteLine();
    
    var grid = new Grid()
        .AddColumn()
        .AddColumn()
        .AddRow("[bold]Total Profiles:[/]", stats.TotalProfiles.ToString())
        .AddRow("[bold]Unique Schemas:[/]", stats.UniqueSchemas.ToString())
        .AddRow("[bold]Total Rows Profiled:[/]", stats.TotalRowsProfiled.ToString("N0"))
        .AddRow("[bold]Disk Usage:[/]", $"{stats.TotalDiskUsageMB:F2} MB")
        .AddRow("[bold]Oldest Profile:[/]", stats.OldestProfile?.ToString("yyyy-MM-dd HH:mm") ?? "-")
        .AddRow("[bold]Newest Profile:[/]", stats.NewestProfile?.ToString("yyyy-MM-dd HH:mm") ?? "-");
    
    AnsiConsole.Write(grid);
    
    AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
    Console.ReadKey(true);
}

// ============================================================================
// Interactive Mode Helper Functions
// ============================================================================

static void ShowCommands()
{
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[cyan]Commands[/]").LeftJustified());
    
    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[yellow]Command[/]")
        .AddColumn("Description");
    
    table.AddRow("/", "Show this command list");
    table.AddRow("/help", "Show this command list");
    table.AddRow("/tools", "List available analytics tools");
    table.AddRow("/profiles", "List output profiles");
    table.AddRow("/profile <name>", "Switch output profile (Default, Brief, Detailed, Tool, Markdown)");
    table.AddRow("/status", "Show current session info");
    table.AddRow("/summary", "Show data summary");
    table.AddRow("/columns", "List all columns with types");
    table.AddRow("/column <name>", "Show details for a specific column");
    table.AddRow("/alerts", "Show all alerts/warnings");
    table.AddRow("/insights", "Show detected insights");
    table.AddRow("/verbose", "Toggle verbose mode");
    table.AddRow("/exit", "Exit interactive mode");
    
    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine("\n[dim]Or just type a question about your data![/]\n");
}

static void ShowAvailableTools()
{
    var registry = new AnalyticsToolRegistry();
    var tools = registry.GetAllTools();
    var byCategory = tools.GroupBy(t => t.Category).OrderBy(g => g.Key.ToString());
    
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[cyan]Analytics Tools[/]").LeftJustified());
    AnsiConsole.MarkupLine("[dim]These tools are automatically invoked by the LLM based on your questions.[/]\n");
    
    foreach (var category in byCategory)
    {
        AnsiConsole.MarkupLine($"[yellow]{category.Key}[/]");
        foreach (var tool in category)
        {
            AnsiConsole.MarkupLine($"  [green]{tool.Name}[/] - {Markup.Escape(tool.Description)}");
            if (tool.ExampleQuestions.Count > 0)
            {
                AnsiConsole.MarkupLine($"    [dim]Try: \"{Markup.Escape(tool.ExampleQuestions[0])}\"[/]");
            }
        }
        AnsiConsole.WriteLine();
    }
}

static void ShowOutputProfiles(
    Dictionary<string, (OutputProfileConfig Config, string Description)> profiles, 
    string currentProfile)
{
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[cyan]Output Profiles[/]").LeftJustified());
    
    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[yellow]Profile[/]")
        .AddColumn("Description")
        .AddColumn("Status");
    
    foreach (var (name, info) in profiles)
    {
        var status = name.Equals(currentProfile, StringComparison.OrdinalIgnoreCase) 
            ? "[green]active[/]" 
            : "[dim]-[/]";
        table.AddRow(name, info.Description, status);
    }
    
    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine("\n[dim]Use '/profile <name>' to switch[/]\n");
}

static void ShowSessionStatus(
    DataSummaryReport report, 
    string file, 
    string sessionId, 
    string currentProfile,
    bool verbose)
{
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[cyan]Session Status[/]").LeftJustified());
    
    var grid = new Grid()
        .AddColumn(new GridColumn().Width(20))
        .AddColumn();
    
    grid.AddRow("[bold]File:[/]", Path.GetFileName(file));
    grid.AddRow("[bold]Path:[/]", file);
    grid.AddRow("[bold]Rows:[/]", report.Profile.RowCount.ToString("N0"));
    grid.AddRow("[bold]Columns:[/]", report.Profile.ColumnCount.ToString());
    grid.AddRow("[bold]Session:[/]", sessionId);
    grid.AddRow("[bold]Profile:[/]", currentProfile);
    grid.AddRow("[bold]Verbose:[/]", verbose ? "on" : "off");
    grid.AddRow("[bold]Alerts:[/]", report.Profile.Alerts.Count.ToString());
    grid.AddRow("[bold]Insights:[/]", report.Profile.Insights.Count.ToString());
    
    AnsiConsole.Write(grid);
    AnsiConsole.WriteLine();
}

static void ShowColumnsSummary(DataSummaryReport report)
{
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[cyan]Columns[/]").LeftJustified());
    
    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[yellow]Column[/]")
        .AddColumn("Type")
        .AddColumn("Nulls")
        .AddColumn("Unique")
        .AddColumn("Role");
    
    foreach (var col in report.Profile.Columns)
    {
        var role = col.SemanticRole != SemanticRole.Unknown 
            ? col.SemanticRole.ToString() 
            : "-";
        
        table.AddRow(
            Markup.Escape(col.Name),
            col.InferredType.ToString(),
            $"{col.NullPercent:F1}%",
            col.UniqueCount.ToString("N0"),
            role
        );
    }
    
    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine("\n[dim]Use '/column <name>' for details[/]\n");
}

static void ShowColumnDetails(DataSummaryReport report, string columnName)
{
    var col = report.Profile.Columns
        .FirstOrDefault(c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
    
    if (col == null)
    {
        AnsiConsole.MarkupLine($"[red]Column not found: {Markup.Escape(columnName)}[/]");
        AnsiConsole.MarkupLine("[dim]Use '/columns' to list available columns[/]\n");
        return;
    }
    
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule($"[cyan]Column: {Markup.Escape(col.Name)}[/]").LeftJustified());
    
    var grid = new Grid()
        .AddColumn(new GridColumn().Width(20))
        .AddColumn();
    
    grid.AddRow("[bold]Type:[/]", col.InferredType.ToString());
    grid.AddRow("[bold]Role:[/]", col.SemanticRole != SemanticRole.Unknown ? col.SemanticRole.ToString() : "-");
    grid.AddRow("[bold]Null %:[/]", $"{col.NullPercent:F2}%");
    grid.AddRow("[bold]Unique:[/]", $"{col.UniqueCount:N0} ({col.UniquePercent:F1}%)");
    
    if (col.InferredType == ColumnType.Numeric)
    {
        if (col.Mean.HasValue) grid.AddRow("[bold]Mean:[/]", $"{col.Mean:F4}");
        if (col.Median.HasValue) grid.AddRow("[bold]Median:[/]", $"{col.Median:F4}");
        if (col.StdDev.HasValue) grid.AddRow("[bold]Std Dev:[/]", $"{col.StdDev:F4}");
        if (col.Min.HasValue && col.Max.HasValue) 
            grid.AddRow("[bold]Range:[/]", $"{col.Min:F2} - {col.Max:F2}");
        if (col.OutlierCount > 0)
        {
            var outlierPct = col.Count > 0 ? col.OutlierCount * 100.0 / col.Count : 0;
            grid.AddRow("[bold]Outliers:[/]", $"{col.OutlierCount} ({outlierPct:F1}%)");
        }
    }
    
    if (col.Distribution.HasValue && col.Distribution != DistributionType.Unknown)
        grid.AddRow("[bold]Distribution:[/]", col.Distribution.ToString()!);
    
    if (col.TopValues?.Count > 0)
    {
        var topVals = string.Join(", ", col.TopValues.Take(3).Select(v => $"{v.Value} ({v.Percent:F1}%)"));
        grid.AddRow("[bold]Top Values:[/]", topVals);
    }
    
    AnsiConsole.Write(grid);
    
    // Show alerts for this column
    var colAlerts = report.Profile.Alerts.Where(a => a.Column == col.Name).ToList();
    if (colAlerts.Count > 0)
    {
        AnsiConsole.MarkupLine("\n[yellow]Alerts:[/]");
        foreach (var alert in colAlerts)
        {
            var color = alert.Severity switch
            {
                AlertSeverity.Error => "red",
                AlertSeverity.Warning => "yellow",
                _ => "dim"
            };
            AnsiConsole.MarkupLine($"  [{color}]• {Markup.Escape(alert.Message)}[/]");
        }
    }
    
    AnsiConsole.WriteLine();
}

static void ShowAlerts(DataSummaryReport report)
{
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[cyan]Alerts[/]").LeftJustified());
    
    if (report.Profile.Alerts.Count == 0)
    {
        AnsiConsole.MarkupLine("[green]No alerts detected![/]\n");
        return;
    }
    
    var grouped = report.Profile.Alerts
        .GroupBy(a => a.Severity)
        .OrderByDescending(g => g.Key);
    
    foreach (var group in grouped)
    {
        var color = group.Key switch
        {
            AlertSeverity.Error => "red",
            AlertSeverity.Warning => "yellow",
            _ => "blue"
        };
        
        AnsiConsole.MarkupLine($"\n[{color}]{group.Key}[/] ({group.Count()})");
        
        foreach (var alert in group.Take(10))
        {
            var col = string.IsNullOrEmpty(alert.Column) ? "" : $"[dim]{alert.Column}:[/] ";
            AnsiConsole.MarkupLine($"  • {col}{Markup.Escape(alert.Message)}");
        }
        
        if (group.Count() > 10)
        {
            AnsiConsole.MarkupLine($"  [dim]... and {group.Count() - 10} more[/]");
        }
    }
    
    AnsiConsole.WriteLine();
}

static void ShowInsights(DataSummaryReport report)
{
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[cyan]Insights[/]").LeftJustified());
    
    if (report.Profile.Insights.Count == 0)
    {
        AnsiConsole.MarkupLine("[dim]No insights generated yet.[/]\n");
        return;
    }
    
    foreach (var insight in report.Profile.Insights.Take(15))
    {
        AnsiConsole.MarkupLine($"\n[green]{Markup.Escape(insight.Title)}[/] [dim](score: {insight.Score:F2})[/]");
        AnsiConsole.MarkupLine($"  {Markup.Escape(insight.Description)}");
        
        if (insight.RelatedColumns.Count > 0)
        {
            AnsiConsole.MarkupLine($"  [dim]Columns: {string.Join(", ", insight.RelatedColumns)}[/]");
        }
    }
    
    if (report.Profile.Insights.Count > 15)
    {
        AnsiConsole.MarkupLine($"\n[dim]... and {report.Profile.Insights.Count - 15} more insights[/]");
    }
    
    AnsiConsole.WriteLine();
}

static void ShowDataSummary(DataSummaryReport report, string file)
{
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[cyan]Data Summary[/]").LeftJustified());
    
    var profile = report.Profile;
    
    var grid = new Grid()
        .AddColumn(new GridColumn().Width(20))
        .AddColumn();
    
    grid.AddRow("[bold]File:[/]", Path.GetFileName(file));
    grid.AddRow("[bold]Rows:[/]", profile.RowCount.ToString("N0"));
    grid.AddRow("[bold]Columns:[/]", profile.ColumnCount.ToString());
    
    var numericCount = profile.Columns.Count(c => c.InferredType == ColumnType.Numeric);
    var categoricalCount = profile.Columns.Count(c => c.InferredType == ColumnType.Categorical);
    var dateCount = profile.Columns.Count(c => c.InferredType == ColumnType.DateTime);
    var textCount = profile.Columns.Count(c => c.InferredType == ColumnType.Text);
    var idCount = profile.Columns.Count(c => c.InferredType == ColumnType.Id);
    
    grid.AddRow("[bold]Types:[/]", 
        $"{numericCount} numeric, {categoricalCount} categorical, {dateCount} date, {textCount} text, {idCount} id");
    
    var nullCols = profile.Columns.Count(c => c.NullPercent > 0);
    if (nullCols > 0)
        grid.AddRow("[bold]With Nulls:[/]", $"{nullCols} columns");
    
    var criticalAlerts = profile.Alerts.Count(a => a.Severity == AlertSeverity.Error);
    var warningAlerts = profile.Alerts.Count(a => a.Severity == AlertSeverity.Warning);
    grid.AddRow("[bold]Alerts:[/]", $"{criticalAlerts} critical, {warningAlerts} warnings");
    
    if (profile.Target != null)
        grid.AddRow("[bold]Target:[/]", profile.Target.ColumnName);
    
    AnsiConsole.Write(grid);
    AnsiConsole.WriteLine();
}

/// <summary>
/// Discover all tables in a SQLite database using DuckDB's SQLite extension
/// </summary>
static async Task<List<string>> DiscoverSqliteTablesAsync(string sqlitePath)
{
    var tables = new List<string>();
    
    try
    {
        await using var conn = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        await conn.OpenAsync();
        
        // Install and load SQLite extension
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSTALL sqlite; LOAD sqlite;";
            await cmd.ExecuteNonQueryAsync();
        }
        
        // Attach the SQLite database
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"ATTACH '{sqlitePath.Replace("'", "''")}' AS sqlite_db (TYPE sqlite)";
            await cmd.ExecuteNonQueryAsync();
        }
        
        // Query information_schema for tables (DuckDB's way to see attached database tables)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_catalog = 'sqlite_db' AND table_schema = 'main' ORDER BY table_name";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error discovering tables: {ex.Message}[/]");
    }
    
    return tables;
}
