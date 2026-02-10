using System.ComponentModel;
using System.Diagnostics;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DoomSummarizer.Commands;

/// <summary>
///     Manage query exemplars for the embedding-based classifier.
///     Exemplars are YAML files defining representative questions per topic/type.
///     The classifier pre-embeds them at startup for deterministic cosine-similarity classification.
/// </summary>
public sealed class ExemplarsCommand : AsyncCommand<ExemplarsCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Init)
            return await InitExemplarsAsync();

        if (settings.List)
            return ListExemplars();

        if (settings.Rebuild)
            return await RebuildCacheAsync(settings, cancellationToken);

        if (settings.Validate)
            return ValidateExemplars();

        if (settings.Test)
            return await RunDiagnosticTestAsync(settings, cancellationToken);

        if (settings.Benchmark)
            return await RunBenchmarkAsync(settings, cancellationToken);

        if (settings.Learn)
            return await RunLearnAsync(settings, cancellationToken);

        if (settings.LearnApply)
            return await RunLearnApplyAsync(settings, cancellationToken);

        if (settings.LearnSchedule)
            return await ShowLearnScheduleAsync(settings, cancellationToken);

        // Default: show summary
        return ShowSummary();
    }

    private static int ShowSummary()
    {
        var exemplars = QueryClassifier.LoadAllExemplars();
        var userDir = Path.Combine(ConfigService.GetConfigDir(), "exemplars");
        var hasUserDir = Directory.Exists(userDir);
        var userFileCount = hasUserDir
            ? Directory.GetFiles(userDir, "*.yaml").Length + Directory.GetFiles(userDir, "*.yml").Length
            : 0;

        var byTopic = exemplars.GroupBy(e => e.Topic).OrderBy(g => g.Key).ToList();
        var byType = exemplars.GroupBy(e => e.Type).OrderBy(g => g.Key).ToList();
        var withVibe = exemplars.Count(e => e.Vibe != null);
        var withComplexity = exemplars.Count(e => e.Complexity != null);

        AnsiConsole.MarkupLine($"[bold cyan]Query Exemplars[/]");
        AnsiConsole.MarkupLine($"  Total: [green]{exemplars.Count}[/] exemplars");
        AnsiConsole.MarkupLine($"  Topics: [green]{byTopic.Count}[/] ({string.Join(", ", byTopic.Select(g => g.Key))})");
        AnsiConsole.MarkupLine($"  Types: [green]{byType.Count}[/] ({string.Join(", ", byType.Select(g => g.Key))})");
        AnsiConsole.MarkupLine($"  With vibe: [magenta]{withVibe}[/], with complexity: [red]{withComplexity}[/]");
        AnsiConsole.MarkupLine($"  User dir: {FormattingHelpers.Esc(userDir)} ({(hasUserDir ? $"{userFileCount} files" : "not created")})");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[grey]Commands:[/]");
        AnsiConsole.MarkupLine("  [grey]exemplars --list[/]      List all exemplars");
        AnsiConsole.MarkupLine("  [grey]exemplars --init[/]      Create user exemplar directory with template");
        AnsiConsole.MarkupLine("  [grey]exemplars --rebuild[/]   Re-embed all exemplars (after editing YAML)");
        AnsiConsole.MarkupLine("  [grey]exemplars --validate[/]  Check exemplar YAML files for errors");
        AnsiConsole.MarkupLine("  [grey]exemplars --test[/]      Run diagnostic test matrix (80+ prompts)");
        AnsiConsole.MarkupLine("  [grey]exemplars --test -v[/]   Full per-query breakdown");
        AnsiConsole.MarkupLine("  [grey]exemplars --benchmark[/] Benchmark classifier latency (p50/p95/p99)");
        AnsiConsole.MarkupLine("  [grey]exemplars --learn[/]     Analyze sentinel disagreements, propose exemplars");
        AnsiConsole.MarkupLine("  [grey]exemplars --learn-apply[/] Validate proposals against test matrix and merge");
        AnsiConsole.MarkupLine("  [grey]exemplars --learn-schedule[/] Show learning schedule and next auto-learn time");

        return 0;
    }

    private static int ListExemplars()
    {
        var exemplars = QueryClassifier.LoadAllExemplars();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Topic")
            .AddColumn("Type")
            .AddColumn("Vibe")
            .AddColumn("Cmplx")
            .AddColumn("Question")
            .AddColumn("Sources");

        foreach (var group in exemplars.GroupBy(e => e.Topic).OrderBy(g => g.Key))
        {
            foreach (var e in group.OrderBy(x => x.Type))
            {
                table.AddRow(
                    $"[cyan]{Markup.Escape(e.Topic)}[/]",
                    $"[yellow]{Markup.Escape(e.Type)}[/]",
                    e.Vibe != null ? $"[magenta]{Markup.Escape(e.Vibe)}[/]" : "[grey]-[/]",
                    e.Complexity != null ? $"[red]{Markup.Escape(e.Complexity)}[/]" : "[grey]-[/]",
                    Markup.Escape(e.Question),
                    e.Sources != null ? Markup.Escape(string.Join(", ", e.Sources)) : "[grey]-[/]");
            }
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]Total: {exemplars.Count} exemplars[/]");

        return 0;
    }

    private static async Task<int> InitExemplarsAsync()
    {
        var userDir = Path.Combine(ConfigService.GetConfigDir(), "exemplars");
        Directory.CreateDirectory(userDir);

        var templatePath = Path.Combine(userDir, "my-exemplars.yaml");
        if (!File.Exists(templatePath))
        {
            var template = """
                           # Custom Query Exemplars
                           # Add your own exemplar questions to customize query classification.
                           # After editing, run: doomsummarizer exemplars --rebuild
                           #
                           # Each exemplar needs:
                           #   question: The representative question text (gets embedded)
                           #   topic: Routing category (technology, ai, entertainment, health, etc.)
                           #   type: Query type (roundup, qa, howto, deep_dive, comparison)
                           #   sources: (optional) Preferred source hints [hn, reddit, bbc, etc.]

                           exemplars:
                             # Example: add a niche topic
                             # - question: "Latest developments in Rust programming language"
                             #   topic: programming
                             #   type: roundup
                             #   sources: [hn, reddit, lobsters]

                             # Example: add a domain-specific QA exemplar
                             # - question: "How do I configure PostgreSQL connection pooling?"
                             #   topic: technology
                             #   type: howto
                           """;
            await File.WriteAllTextAsync(templatePath, template);
            AnsiConsole.MarkupLine($"[green]Created:[/] {FormattingHelpers.Esc(templatePath)}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Already exists:[/] {FormattingHelpers.Esc(templatePath)}");
        }

        AnsiConsole.MarkupLine($"[grey]Edit the file, then run: doomsummarizer exemplars --rebuild[/]");
        return 0;
    }

    private static async Task<int> RebuildCacheAsync(Settings settings, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[grey]Loading exemplars...[/]");
        var exemplars = QueryClassifier.LoadAllExemplars();
        AnsiConsole.MarkupLine($"[green]Loaded {exemplars.Count} exemplars[/]");

        AnsiConsole.MarkupLine("[grey]Initializing embedding service...[/]");
        var boot = await CommandBootstrap.CreateAsync(settings.GpuDevice, ct);
        await using (boot)
        {
            var classifier = new QueryClassifier();
            await AnsiConsole.Status()
                .StartAsync("Embedding exemplars...", async ctx =>
                {
                    await classifier.InitializeAsync(boot.Embedding, ct);
                });

            AnsiConsole.MarkupLine(
                $"[green]Embedded {classifier.ExemplarCount} exemplars successfully[/]");

            // Test with a few sample queries to verify
            if (!settings.Quiet)
            {
                AnsiConsole.MarkupLine("\n[bold]Sample classifications:[/]");
                var testQueries = new[]
                {
                    "latest tech news",
                    "What is quantum computing?",
                    "celebrity gossip",
                    "How do I set up Docker?",
                    "Compare React vs Vue",
                    "doom-scroll the worst news",
                    "AI news and also politics",
                    "What time is it in Tokyo?",
                    "implications of EU AI Act on open source",
                    // Short query feature tests
                    "tech news",
                    "AI news",
                    "Docker help",
                    "define ontological",
                    "convert miles km",
                    "compare React Vue",
                };

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Query")
                    .AddColumn("Top Topic")
                    .AddColumn("Score")
                    .AddColumn("Type")
                    .AddColumn("Vibe")
                    .AddColumn("Flags");

                foreach (var query in testQueries)
                {
                    var result = await classifier.ClassifyAsync(query, ct);
                    var topCat = result.Categories
                        .OrderByDescending(kv => kv.Value)
                        .FirstOrDefault();
                    var flags = (result.IsComposite ? "C" : "") + (result.IsComplex ? "X" : "")
                                + (result.Features != null ? "F" : "");
                    table.AddRow(
                        Markup.Escape(query),
                        $"[cyan]{Markup.Escape(topCat.Key ?? "none")}[/]",
                        $"{topCat.Value:F2}",
                        $"[yellow]{Markup.Escape(result.QueryType)}[/]",
                        result.Vibe != null ? $"[magenta]{Markup.Escape(result.Vibe)}[/]" : "[grey]-[/]",
                        !string.IsNullOrEmpty(flags) ? $"[red]{flags}[/]" : "[grey]-[/]");
                }

                AnsiConsole.Write(table);
            }
        }

        return 0;
    }

    private static int ValidateExemplars()
    {
        var errors = 0;
        var userDir = Path.Combine(ConfigService.GetConfigDir(), "exemplars");

        AnsiConsole.MarkupLine("[bold]Validating exemplar files...[/]");

        // Validate all exemplars
        try
        {
            var all = QueryClassifier.LoadAllExemplars();
            AnsiConsole.MarkupLine($"  [green]Total:[/] {all.Count} exemplars loaded");

            var validVibes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "doom", "hopeful", "snarky", "funny", "upbeat", "friendly", "toon", "neutral", "concise" };
            var validComplexities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "simple", "complex" };

            // Check for missing fields and field consistency
            foreach (var e in all)
            {
                if (string.IsNullOrWhiteSpace(e.Question))
                {
                    AnsiConsole.MarkupLine($"  [red]Error:[/] empty question in topic={e.Topic}");
                    errors++;
                }

                if (string.IsNullOrWhiteSpace(e.Topic))
                {
                    AnsiConsole.MarkupLine($"  [red]Error:[/] empty topic for \"{Markup.Escape(e.Question)}\"");
                    errors++;
                }

                if (e.Vibe != null && !validVibes.Contains(e.Vibe))
                {
                    AnsiConsole.MarkupLine($"  [yellow]Warning:[/] unknown vibe \"{Markup.Escape(e.Vibe)}\" for \"{Markup.Escape(e.Question)}\"");
                }

                if (e.Complexity != null && !validComplexities.Contains(e.Complexity))
                {
                    AnsiConsole.MarkupLine($"  [yellow]Warning:[/] unknown complexity \"{Markup.Escape(e.Complexity)}\" for \"{Markup.Escape(e.Question)}\"");
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]Error loading exemplars:[/] {Markup.Escape(ex.Message)}");
            errors++;
        }

        // Validate user files individually
        if (Directory.Exists(userDir))
        {
            var files = Directory.GetFiles(userDir, "*.yaml")
                .Concat(Directory.GetFiles(userDir, "*.yml"));
            foreach (var file in files)
            {
                try
                {
                    var exemplars = QueryClassifier.LoadExemplarsFromFile(file);
                    AnsiConsole.MarkupLine(
                        $"  [green]{FormattingHelpers.Esc(Path.GetFileName(file))}:[/] {exemplars.Count} exemplars");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine(
                        $"  [red]{FormattingHelpers.Esc(Path.GetFileName(file))}:[/] {Markup.Escape(ex.Message)}");
                    errors++;
                }
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"  [grey]No user exemplar directory ({FormattingHelpers.Esc(userDir)})[/]");
        }

        if (errors == 0)
            AnsiConsole.MarkupLine("\n[green]All exemplar files valid.[/]");
        else
            AnsiConsole.MarkupLine($"\n[red]{errors} error(s) found.[/]");

        return errors > 0 ? 1 : 0;
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Diagnostic test matrix ──────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Expected classification result for a test prompt.
    ///     Null fields mean "don't check this dimension".
    /// </summary>
    private record TestCase(
        string Query,
        string? ExpectedTopic = null,
        string? ExpectedType = null,
        string? ExpectedVibe = null,
        bool? ExpectedComposite = null,
        bool? ExpectedComplex = null);

    /// <summary>
    ///     Run the full diagnostic test matrix against the classifier.
    ///     Shows per-query pass/fail with optional verbose breakdown, then aggregate stats.
    /// </summary>
    private static async Task<int> RunDiagnosticTestAsync(Settings settings, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[grey]Initializing embedding service...[/]");
        var boot = await CommandBootstrap.CreateAsync(settings.GpuDevice, ct);
        await using (boot)
        {
            var classifier = new QueryClassifier();
            await AnsiConsole.Status()
                .StartAsync("Embedding exemplars...", async _ =>
                {
                    await classifier.InitializeAsync(boot.Embedding, ct);
                });

            AnsiConsole.MarkupLine(
                $"[green]Loaded {classifier.ExemplarCount} exemplars[/]\n");

            var testCases = BuildTestMatrix();
            AnsiConsole.MarkupLine($"[bold cyan]Diagnostic Test Matrix[/] — {testCases.Count} test cases\n");

            var passed = 0;
            var failed = 0;
            var topicPass = 0; var topicTotal = 0;
            var typePass = 0; var typeTotal = 0;
            var vibePass = 0; var vibeTotal = 0;
            var compositePass = 0; var compositeTotal = 0;
            var complexPass = 0; var complexTotal = 0;
            var failures = new List<(TestCase Test, QueryClassification Result, string Reason)>();

            foreach (var test in testCases)
            {
                var result = await classifier.ClassifyAsync(test.Query, ct);
                var topTopic = result.Categories
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .FirstOrDefault() ?? "none";
                var topTopics = result.Categories
                    .OrderByDescending(kv => kv.Value)
                    .Take(3)
                    .Select(kv => kv.Key)
                    .ToList();

                var queryPass = true;
                var reason = new List<string>();

                // Check topic (allow top-3)
                if (test.ExpectedTopic != null)
                {
                    topicTotal++;
                    if (topTopics.Contains(test.ExpectedTopic, StringComparer.OrdinalIgnoreCase))
                    {
                        topicPass++;
                    }
                    else
                    {
                        queryPass = false;
                        reason.Add($"topic: expected={test.ExpectedTopic}, got=[{string.Join(",", topTopics)}]");
                    }
                }

                // Check type
                if (test.ExpectedType != null)
                {
                    typeTotal++;
                    if (result.QueryType.Equals(test.ExpectedType, StringComparison.OrdinalIgnoreCase))
                    {
                        typePass++;
                    }
                    else
                    {
                        queryPass = false;
                        reason.Add($"type: expected={test.ExpectedType}, got={result.QueryType}");
                    }
                }

                // Check vibe — "none" means assert no vibe detected (result.Vibe should be null)
                if (test.ExpectedVibe != null)
                {
                    vibeTotal++;
                    var expectNoVibe = test.ExpectedVibe.Equals("none", StringComparison.OrdinalIgnoreCase);
                    var vibeMatch = expectNoVibe
                        ? result.Vibe == null
                        : string.Equals(result.Vibe, test.ExpectedVibe, StringComparison.OrdinalIgnoreCase);
                    if (vibeMatch)
                    {
                        vibePass++;
                    }
                    else
                    {
                        queryPass = false;
                        reason.Add($"vibe: expected={test.ExpectedVibe}, got={result.Vibe ?? "null"}");
                    }
                }

                // Check composite
                if (test.ExpectedComposite != null)
                {
                    compositeTotal++;
                    if (result.IsComposite == test.ExpectedComposite.Value)
                    {
                        compositePass++;
                    }
                    else
                    {
                        queryPass = false;
                        reason.Add($"composite: expected={test.ExpectedComposite}, got={result.IsComposite}");
                    }
                }

                // Check complex
                if (test.ExpectedComplex != null)
                {
                    complexTotal++;
                    if (result.IsComplex == test.ExpectedComplex.Value)
                    {
                        complexPass++;
                    }
                    else
                    {
                        queryPass = false;
                        reason.Add($"complex: expected={test.ExpectedComplex}, got={result.IsComplex}");
                    }
                }

                if (queryPass)
                    passed++;
                else
                {
                    failed++;
                    failures.Add((test, result, string.Join("; ", reason)));
                }

                // Per-query output
                var statusMark = queryPass ? "[green]PASS[/]" : "[red]FAIL[/]";
                var queryDisplay = FormattingHelpers.TruncEsc(test.Query, 55);

                if (settings.Verbose)
                {
                    AnsiConsole.MarkupLine($"{statusMark} {queryDisplay}");
                    AnsiConsole.MarkupLine(
                        $"  [grey]Topic:[/] [cyan]{Markup.Escape(topTopic)}[/] ({result.Categories.GetValueOrDefault(topTopic):F2})  " +
                        $"[grey]Type:[/] [yellow]{Markup.Escape(result.QueryType)}[/] ({result.QueryTypeConfidence:F2})  " +
                        $"[grey]Vibe:[/] {(result.Vibe != null ? $"[magenta]{Markup.Escape(result.Vibe)}[/] ({result.VibeConfidence:F2})" : "[grey]-[/]")}  " +
                        $"[grey]Comp:[/] {(result.IsComposite ? "[red]Y[/]" : "N")}  " +
                        $"[grey]Cmplx:[/] {(result.IsComplex ? "[red]Y[/]" : "N")}  " +
                        $"[grey]Best:[/] {result.BestMatchScore:F2}");

                    // Top 3 matches
                    foreach (var m in result.TopMatches.Take(3))
                        AnsiConsole.MarkupLine(
                            $"    {m.Score:F3} [grey][[{Markup.Escape(m.Topic)}/{Markup.Escape(m.Type)}]][/] {FormattingHelpers.TruncEsc(m.Question, 60)}");

                    // Features (if short query)
                    if (result.Features is QueryFeatureSet f)
                        AnsiConsole.MarkupLine(
                            $"    [dim]Features: words={f.WordCount} q?={f.HasQuestionWord} cmp={f.HasComparisonMarker} " +
                            $"howto={f.HasHowtoMarker} search={f.HasSearchOnlyMarker} qa={f.HasQaMarker} " +
                            $"composite={f.HasCompositeConjunction} imperative={f.HasImperativeVerb}[/]");

                    if (!queryPass)
                        AnsiConsole.MarkupLine($"    [red]{Markup.Escape(string.Join("; ", reason))}[/]");
                    AnsiConsole.WriteLine();
                }
                else if (!queryPass)
                {
                    // Compact mode: only show failures
                    AnsiConsole.MarkupLine(
                        $"{statusMark} {queryDisplay} — [red]{Markup.Escape(string.Join("; ", reason))}[/]");
                }
            }

            // ── Aggregate stats ──
            AnsiConsole.WriteLine();
            var rule = new Rule("[bold]Results[/]").RuleStyle("cyan");
            AnsiConsole.Write(rule);

            var overallRate = testCases.Count > 0 ? (double)passed / testCases.Count : 0;
            var overallColor = overallRate >= 0.90 ? "green" : overallRate >= 0.75 ? "yellow" : "red";
            AnsiConsole.MarkupLine(
                $"  Overall: [{overallColor}]{passed}/{testCases.Count} ({overallRate:P0})[/]");

            PrintDimensionStat("Topic", topicPass, topicTotal);
            PrintDimensionStat("Type", typePass, typeTotal);
            PrintDimensionStat("Vibe", vibePass, vibeTotal);
            PrintDimensionStat("Composite", compositePass, compositeTotal);
            PrintDimensionStat("Complex", complexPass, complexTotal);

            // ── Failure summary ──
            if (failures.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[bold red]Failures ({failures.Count}):[/]");
                var failTable = new Table()
                    .Border(TableBorder.Simple)
                    .AddColumn("Query")
                    .AddColumn("Got")
                    .AddColumn("Reason");

                foreach (var (test, result, failReason) in failures)
                {
                    var gotSummary =
                        $"{result.Categories.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "?"}/{result.QueryType}";
                    if (result.Vibe != null) gotSummary += $"/{result.Vibe}";
                    if (result.IsComposite) gotSummary += " [C]";
                    if (result.IsComplex) gotSummary += " [X]";
                    failTable.AddRow(
                        FormattingHelpers.TruncEsc(test.Query, 45),
                        Markup.Escape(gotSummary),
                        Markup.Escape(failReason));
                }

                AnsiConsole.Write(failTable);
            }
            else
            {
                AnsiConsole.MarkupLine("\n[bold green]All tests passed![/]");
            }

            return failed > 0 ? 1 : 0;
        }
    }

    private static void PrintDimensionStat(string name, int pass, int total)
    {
        if (total == 0) return;
        var rate = (double)pass / total;
        var color = rate >= 0.90 ? "green" : rate >= 0.75 ? "yellow" : "red";
        AnsiConsole.MarkupLine($"  {name,-12} [{color}]{pass}/{total} ({rate:P0})[/]");
    }

    /// <summary>
    ///     Build the comprehensive test matrix covering all classification dimensions.
    /// </summary>
    private static List<TestCase> BuildTestMatrix() =>
    [
        // ══════════════════════════════════════════════
        // ── Topic detection ──────────────────────────
        // ══════════════════════════════════════════════
        new("What's the latest tech news?", ExpectedTopic: "technology", ExpectedType: "roundup"),
        new("Show me AI and machine learning news", ExpectedTopic: "ai"),
        new("Latest .NET framework updates", ExpectedTopic: "programming"),
        new("Celebrity gossip and entertainment", ExpectedTopic: "entertainment"),
        new("Recent physics research breakthroughs", ExpectedTopic: "science"),
        new("Health news and medical research", ExpectedTopic: "health"),
        new("Stock market and investment news", ExpectedTopic: "finance"),
        new("Political developments today", ExpectedTopic: "politics"),
        new("International headlines", ExpectedTopic: "world"),
        new("Climate change updates", ExpectedTopic: "environment"),
        new("Football results this weekend", ExpectedTopic: "sports"),
        new("Rocket launches and space missions", ExpectedTopic: "space"),
        new("Data breaches and cybersecurity", ExpectedTopic: "security"),
        new("Video game releases this month", ExpectedTopic: "gaming"),
        new("Local crime reports and statistics", ExpectedTopic: "crime"),
        new("Flood warnings in my area", ExpectedTopic: "flooding"),
        new("New drug approvals FDA", ExpectedTopic: "pharma"),
        new("Satirical news roundup", ExpectedTopic: "satire"),
        new("Airline delays and travel news", ExpectedTopic: "transport"),
        new("Food safety recalls", ExpectedTopic: "food"),
        new("Business acquisitions this quarter", ExpectedTopic: "business"),
        new("UK parliament debates this week", ExpectedTopic: "uk"),

        // ══════════════════════════════════════════════
        // ── Type detection ───────────────────────────
        // ══════════════════════════════════════════════
        new("Give me a roundup of today's news", ExpectedType: "roundup"),
        new("Latest tech news roundup", ExpectedType: "roundup"),
        new("How do I set up a Docker container?", ExpectedType: "howto"),
        new("How to configure Nginx reverse proxy", ExpectedType: "howto"),
        new("Step by step guide to Kubernetes", ExpectedType: "howto"),
        new("Compare React vs Angular", ExpectedType: "comparison"),
        new("Pros and cons of TypeScript vs JavaScript", ExpectedType: "comparison"),
        new("What is quantum entanglement?", ExpectedType: "deep_dive"),
        new("In-depth analysis of AI safety", ExpectedType: "deep_dive"),
        new("Explain how neural networks work", ExpectedType: "qa"),  // "explain what X is" pattern → qa
        new("What time is it in Tokyo?", ExpectedType: "search_only"),
        new("Define ontological", ExpectedType: "search_only"),
        new("What's the population of France?", ExpectedType: "search_only"),
        new("Convert 100 dollars to euros", ExpectedType: "search_only"),
        new("How tall is Mount Everest?", ExpectedType: "search_only"),
        new("Weather in London right now", ExpectedType: "search_only"),

        // ══════════════════════════════════════════════
        // ── Vibe detection ───────────────────────────
        // ══════════════════════════════════════════════
        new("Give me the most depressing news", ExpectedVibe: "doom"),
        new("Doom-scroll today's terrible headlines", ExpectedVibe: "doom"),
        new("Show me the worst doom and gloom stories", ExpectedVibe: "doom"),
        new("Sarcastic take on today's news", ExpectedVibe: "snarky"),
        new("Roast the latest tech announcements", ExpectedVibe: "snarky"),
        new("Show me positive uplifting stories", ExpectedVibe: "hopeful"),
        new("Give me some good news for a change", ExpectedVibe: "hopeful"),
        new("Tell me the funniest news stories", ExpectedVibe: "funny"),
        new("Make the news entertaining and hilarious", ExpectedVibe: "funny"),
        // Negative vibe: these neutral queries should NOT get a vibe
        new("What's the latest tech news?", ExpectedVibe: "none"),
        new("Latest tech news roundup", ExpectedVibe: "none"),
        new("What's going on in the world today?", ExpectedVibe: "none"),
        new("Give me a roundup of today's news", ExpectedVibe: "none"),

        // ══════════════════════════════════════════════
        // ── Composite detection ──────────────────────
        // ══════════════════════════════════════════════
        new("Tech news and also what's in politics", ExpectedComposite: true),
        new("AI developments this week and compare the top models", ExpectedComposite: true),
        new("Get me business news and find out what's new in AI", ExpectedComposite: true),
        new("Summarize tech news and also what's happening in politics", ExpectedComposite: true),
        // Punctuation-based composite (no "and also" needed)
        new("AI news; politics; ukraine", ExpectedComposite: true),
        // These should NOT be composite
        new("Latest tech news", ExpectedComposite: false),
        new("What's happening in AI today?", ExpectedComposite: false),
        new("Compare React and Vue", ExpectedComposite: false),

        // ══════════════════════════════════════════════
        // ── Complex detection ────────────────────────
        // ══════════════════════════════════════════════
        new("What are the second-order effects of rising interest rates on tech?", ExpectedComplex: true),
        new("How might the EU AI Act affect open source development in practice?", ExpectedComplex: true),
        // These should NOT be complex
        new("Latest tech news", ExpectedComplex: false),
        new("What is quantum computing?", ExpectedComplex: false),
        new("What's happening in AI today?", ExpectedComplex: false),

        // ══════════════════════════════════════════════
        // ── Short queries ────────────────────────────
        // ══════════════════════════════════════════════
        new("tech news", ExpectedTopic: "technology", ExpectedType: "roundup"),
        new("AI news", ExpectedTopic: "ai", ExpectedType: "roundup"),
        new("Docker help", ExpectedType: "howto"),
        new("define ontological", ExpectedType: "search_only"),
        new("convert miles km", ExpectedType: "search_only"),
        new("compare React Vue", ExpectedType: "comparison"),
        new("celebrity gossip", ExpectedTopic: "entertainment"),
        new("sports scores", ExpectedTopic: "sports"),
        new("space news", ExpectedTopic: "space"),
        new("crypto prices", ExpectedTopic: "finance"),  // no "crypto" topic; routes to finance

        // ══════════════════════════════════════════════
        // ── Real-world natural prompts ───────────────
        // ══════════════════════════════════════════════
        new("What's going on in the world today?", ExpectedTopic: "world", ExpectedType: "roundup"),
        new("Catch me up on the news", ExpectedType: "roundup"),
        new("doom-scroll the worst news", ExpectedVibe: "doom"),
        new("implications of EU AI Act on open source", ExpectedComplex: true),
        new("AI news and also politics", ExpectedComposite: true),
        new("show me the latest on climate change", ExpectedTopic: "environment"),
        new("what happened today", ExpectedType: "news"),  // "what happened" → news, not roundup
        new("is there any good news?", ExpectedVibe: "hopeful"),
        new("tell me something funny from the news", ExpectedVibe: "funny"),
        new("how do I deploy to Azure?", ExpectedType: "howto"),
        new("who won the match?", ExpectedTopic: "sports"),
        new("any new breakthroughs in fusion energy?", ExpectedTopic: "science"),
        new("what's the deal with quantum computing?", ExpectedTopic: "science"),
        new("give me a snarky summary of the news", ExpectedVibe: "snarky"),
    ];

    /// <summary>
    ///     Benchmark the classifier: measures embedding + scoring latency separately.
    ///     Runs N iterations (default 100) with warmup, reports p50/p95/p99/mean.
    /// </summary>
    private static async Task<int> RunBenchmarkAsync(Settings settings, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[grey]Initializing embedding service...[/]");
        var boot = await CommandBootstrap.CreateAsync(settings.GpuDevice, ct);
        await using (boot)
        {
            var classifier = new QueryClassifier();
            await AnsiConsole.Status()
                .StartAsync("Embedding exemplars...", async _ =>
                {
                    await classifier.InitializeAsync(boot.Embedding, ct);
                });

            AnsiConsole.MarkupLine($"[green]Loaded {classifier.ExemplarCount} exemplars[/]\n");

            var iterations = settings.BenchmarkIterations ?? 100;
            var queries = new[]
            {
                "latest tech news",
                "How do I set up a Docker container?",
                "Compare React vs Angular",
                "Give me the most depressing news",
                "AI news and also what's happening in politics",
                "What time is it in Tokyo?",
                "What are the second-order effects of rising interest rates on tech?",
                "celebrity gossip",
                "define ontological",
                "What's happening in the world today?",
            };

            AnsiConsole.MarkupLine($"[bold cyan]Benchmark[/] — {iterations} iterations x {queries.Length} queries\n");

            // Warmup: 5 iterations (primes JIT, ONNX session, caches)
            for (var w = 0; w < 5; w++)
                foreach (var q in queries)
                    await classifier.ClassifyAsync(q, ct);

            // Collect per-iteration timings (embedding + classify combined)
            var totalTimes = new List<double>(iterations * queries.Length);
            // Collect classify-only times (no embedding, measures scoring logic)
            var classifyOnlyTimes = new List<double>(iterations * queries.Length);

            // Pre-embed all queries once for classify-only benchmark
            var preEmbedded = new float[queries.Length][];
            for (var i = 0; i < queries.Length; i++)
                preEmbedded[i] = await boot.Embedding.EmbedAsync(queries[i], ct);

            var sw = new Stopwatch();

            for (var i = 0; i < iterations; i++)
            {
                foreach (var q in queries)
                {
                    sw.Restart();
                    await classifier.ClassifyAsync(q, ct);
                    sw.Stop();
                    totalTimes.Add(sw.Elapsed.TotalMicroseconds);
                }
            }

            // Classify-only: use ClassifyWithEmbeddingAsync if available,
            // otherwise measure just the scoring portion by timing ClassifyAsync
            // (embedding is cached/fast after warmup anyway)
            for (var i = 0; i < Math.Min(iterations, 50); i++)
            {
                foreach (var q in queries)
                {
                    sw.Restart();
                    await classifier.ClassifyAsync(q, ct);
                    sw.Stop();
                    classifyOnlyTimes.Add(sw.Elapsed.TotalMicroseconds);
                }
            }

            // Stats
            totalTimes.Sort();
            classifyOnlyTimes.Sort();

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Metric")
                .AddColumn("Total (embed+classify)")
                .AddColumn("Classify-only (warmed)");

            table.AddRow("Samples", $"{totalTimes.Count}", $"{classifyOnlyTimes.Count}");
            table.AddRow("Mean", FormatUs(totalTimes.Average()), FormatUs(classifyOnlyTimes.Average()));
            table.AddRow("p50", FormatUs(Percentile(totalTimes, 0.50)), FormatUs(Percentile(classifyOnlyTimes, 0.50)));
            table.AddRow("p95", FormatUs(Percentile(totalTimes, 0.95)), FormatUs(Percentile(classifyOnlyTimes, 0.95)));
            table.AddRow("p99", FormatUs(Percentile(totalTimes, 0.99)), FormatUs(Percentile(classifyOnlyTimes, 0.99)));
            table.AddRow("Min", FormatUs(totalTimes[0]), FormatUs(classifyOnlyTimes[0]));
            table.AddRow("Max", FormatUs(totalTimes[^1]), FormatUs(classifyOnlyTimes[^1]));

            AnsiConsole.Write(table);

            // Per-query breakdown
            if (settings.Verbose)
            {
                AnsiConsole.MarkupLine("\n[bold]Per-query breakdown (last iteration):[/]");
                var perQuery = new Table()
                    .Border(TableBorder.Simple)
                    .AddColumn("Query")
                    .AddColumn("Time");

                foreach (var q in queries)
                {
                    sw.Restart();
                    await classifier.ClassifyAsync(q, ct);
                    sw.Stop();
                    perQuery.AddRow(
                        FormattingHelpers.TruncEsc(q, 55),
                        FormatUs(sw.Elapsed.TotalMicroseconds));
                }

                AnsiConsole.Write(perQuery);
            }

            return 0;
        }
    }

    private static string FormatUs(double microseconds) =>
        microseconds >= 1000 ? $"{microseconds / 1000:F2} ms" : $"{microseconds:F0} us";

    private static double Percentile(List<double> sorted, double p)
    {
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(idx, sorted.Count - 1))];
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Learning from sentinel disagreements ────────────────────────
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Analyze sentinel disagreements and propose new exemplars.
    ///     Reads from the learning_log table, clusters by embedding similarity,
    ///     and outputs proposed YAML exemplars.
    /// </summary>
    private static async Task<int> RunLearnAsync(Settings settings, CancellationToken ct)
    {
        var boot = await CommandBootstrap.CreateAsync(settings.GpuDevice, ct);
        await using (boot)
        {
            var minCluster = settings.LearnMinCluster ?? boot.Config.Learning.MinClusterSize;
            var analyzer = new LearningAnalyzer(boot.Storage, minCluster);

            var (total, unpromoted, promoted) = await boot.Storage.GetLearningLogCountsAsync();
            AnsiConsole.MarkupLine($"[bold cyan]Learning Log[/]");
            AnsiConsole.MarkupLine($"  Total entries: [green]{total}[/]");
            AnsiConsole.MarkupLine($"  Unpromoted: [yellow]{unpromoted}[/]");
            AnsiConsole.MarkupLine($"  Promoted: [grey]{promoted}[/]");

            if (unpromoted == 0)
            {
                AnsiConsole.MarkupLine(
                    "\n[yellow]No unpromoted disagreements found.[/] Run queries that trigger the sentinel LLM to accumulate data.");
                return 0;
            }

            AnsiConsole.MarkupLine("\n[grey]Analyzing disagreements...[/]");
            var result = await analyzer.AnalyzeAsync(ct);

            // Show disagreement stats
            var stats = await boot.Storage.GetDisagreementStatsAsync();
            if (stats.Count > 0)
            {
                var statsTable = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Topic")
                    .AddColumn("Type")
                    .AddColumn("Count")
                    .AddColumn("Topic Disagree")
                    .AddColumn("Type Disagree")
                    .AddColumn("Avg Emb Score");

                foreach (var stat in stats)
                {
                    statsTable.AddRow(
                        $"[cyan]{FormattingHelpers.Esc(stat.SentinelTopic ?? "?")}[/]",
                        $"[yellow]{FormattingHelpers.Esc(stat.SentinelType)}[/]",
                        stat.Count.ToString(),
                        stat.TopicDisagreeCount.ToString(),
                        stat.TypeDisagreeCount.ToString(),
                        $"{stat.AvgEmbeddingScore:F2}");
                }

                AnsiConsole.Write(statsTable);
            }

            // Show proposed exemplars
            if (result.TotalProposals == 0)
            {
                AnsiConsole.MarkupLine(
                    $"\n[yellow]No proposals generated.[/] Need at least {minCluster} disagreements per (topic, type) bucket.");
                if (result.SmallGroupCount > 0)
                    AnsiConsole.MarkupLine(
                        $"  [grey]{result.SmallGroupCount} groups below threshold ({result.SmallGroupQueries.Count} queries)[/]");
                return 0;
            }

            AnsiConsole.MarkupLine($"\n[bold green]Proposed Exemplars ({result.TotalProposals}):[/]");

            var proposalTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Gap")
                .AddColumn("Count")
                .AddColumn("Proposed Question")
                .AddColumn("Topic")
                .AddColumn("Type")
                .AddColumn("Emb Score");

            foreach (var gap in result.Gaps)
            {
                foreach (var proposal in gap.ProposedExemplars)
                {
                    proposalTable.AddRow(
                        $"{FormattingHelpers.Esc(gap.SentinelTopic)}/{FormattingHelpers.Esc(gap.SentinelType)}",
                        gap.DisagreementCount.ToString(),
                        FormattingHelpers.TruncEsc(proposal.Question, 50),
                        $"[cyan]{FormattingHelpers.Esc(proposal.Topic)}[/]",
                        $"[yellow]{FormattingHelpers.Esc(proposal.Type)}[/]",
                        $"{proposal.EmbeddingScore:F2}");
                }
            }

            AnsiConsole.Write(proposalTable);

            // Write YAML to user exemplars dir
            var allProposals = result.Gaps.SelectMany(g => g.ProposedExemplars).ToList();
            var yaml = LearningAnalyzer.ProposalsToYaml(allProposals);

            var userDir = Path.Combine(ConfigService.GetConfigDir(), "exemplars");
            Directory.CreateDirectory(userDir);
            var outputPath = Path.Combine(userDir, "auto-learned.yaml");
            await File.WriteAllTextAsync(outputPath, yaml, ct);

            AnsiConsole.MarkupLine($"\n[green]Written to:[/] {FormattingHelpers.Esc(outputPath)}");
            AnsiConsole.MarkupLine("[grey]Run: exemplars --learn-apply  to validate and merge[/]");

            return 0;
        }
    }

    /// <summary>
    ///     Merge proposed exemplars, validate against the test matrix, and commit if no regressions.
    /// </summary>
    private static async Task<int> RunLearnApplyAsync(Settings settings, CancellationToken ct)
    {
        var userDir = Path.Combine(ConfigService.GetConfigDir(), "exemplars");
        var proposalPath = Path.Combine(userDir, "auto-learned.yaml");

        if (!File.Exists(proposalPath))
        {
            AnsiConsole.MarkupLine("[red]No proposals found.[/] Run: exemplars --learn  first");
            return 1;
        }

        // Load proposals
        var proposalExemplars = QueryClassifier.LoadExemplarsFromFile(proposalPath);
        if (proposalExemplars.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No exemplars in proposal file.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold cyan]Validating {proposalExemplars.Count} proposed exemplars[/]\n");

        var boot = await CommandBootstrap.CreateAsync(settings.GpuDevice, ct);
        await using (boot)
        {
            // Run baseline test matrix
            AnsiConsole.MarkupLine("[grey]Running baseline test matrix...[/]");
            var baselineClassifier = new QueryClassifier();
            await baselineClassifier.InitializeAsync(boot.Embedding, ct);
            var testCases = BuildTestMatrix();

            var baselinePass = 0;
            var baselineResults = new List<(bool Passed, string? Reason)>();
            foreach (var test in testCases)
            {
                var result = await baselineClassifier.ClassifyAsync(test.Query, ct);
                var (passed, reason) = EvaluateTestCase(test, result);
                baselineResults.Add((passed, reason));
                if (passed) baselinePass++;
            }

            AnsiConsole.MarkupLine($"  Baseline: [green]{baselinePass}/{testCases.Count}[/] tests pass");

            // Run with proposals merged
            AnsiConsole.MarkupLine("[grey]Running with proposals merged...[/]");
            var allExemplars = QueryClassifier.LoadAllExemplars();
            allExemplars.AddRange(proposalExemplars);

            var mergedClassifier = new QueryClassifier();
            await mergedClassifier.InitializeWithExemplarsAsync(boot.Embedding, allExemplars, ct);

            var mergedPass = 0;
            var regressions = new List<(TestCase Test, string Reason)>();
            var improvements = new List<string>();

            for (var i = 0; i < testCases.Count; i++)
            {
                var result = await mergedClassifier.ClassifyAsync(testCases[i].Query, ct);
                var (passed, reason) = EvaluateTestCase(testCases[i], result);
                if (passed) mergedPass++;

                // Detect regressions and improvements
                if (baselineResults[i].Passed && !passed)
                    regressions.Add((testCases[i], reason ?? "unknown"));
                else if (!baselineResults[i].Passed && passed)
                    improvements.Add(testCases[i].Query);
            }

            AnsiConsole.MarkupLine($"  Merged:   [green]{mergedPass}/{testCases.Count}[/] tests pass");

            if (improvements.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[green]Improvements ({improvements.Count}):[/]");
                foreach (var q in improvements)
                    AnsiConsole.MarkupLine($"  [green]+[/] {FormattingHelpers.TruncEsc(q, 60)}");
            }

            if (regressions.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[bold red]REGRESSIONS ({regressions.Count}) — proposals REJECTED:[/]");
                foreach (var (test, reason) in regressions)
                    AnsiConsole.MarkupLine(
                        $"  [red]-[/] {FormattingHelpers.TruncEsc(test.Query, 50)} — {FormattingHelpers.Esc(reason)}");

                AnsiConsole.MarkupLine(
                    "\n[yellow]Fix the proposals in auto-learned.yaml and try again, or delete the file.[/]");
                return 1;
            }

            // All good — the proposals file is already in the exemplars dir, so it will be loaded
            // by the classifier on next startup. Mark entries as promoted.
            var analyzer = new LearningAnalyzer(boot.Storage, settings.LearnMinCluster ?? boot.Config.Learning.MinClusterSize);
            var analysis = await analyzer.AnalyzeAsync(ct);
            var allEntryIds = analysis.Gaps.SelectMany(g => g.EntryIds).ToList();
            if (allEntryIds.Count > 0)
                await boot.Storage.MarkPromotedAsync(allEntryIds);

            AnsiConsole.MarkupLine(
                $"\n[bold green]Proposals accepted![/] {proposalExemplars.Count} new exemplars in auto-learned.yaml");
            AnsiConsole.MarkupLine(
                $"  Net improvement: [green]+{improvements.Count}[/] tests, [red]0[/] regressions");
            AnsiConsole.MarkupLine($"  Promoted {allEntryIds.Count} learning log entries");

            return 0;
        }
    }

    /// <summary>
    ///     Show the current learning schedule: config, last run, next due time.
    /// </summary>
    private static async Task<int> ShowLearnScheduleAsync(Settings settings, CancellationToken ct)
    {
        var boot = await CommandBootstrap.CreateAsync(settings.GpuDevice, ct);
        await using (boot)
        {
            var config = boot.Config.Learning;
            var lastRun = await boot.Storage.GetLastLearnRunAsync();
            var (total, unpromoted, _) = await boot.Storage.GetLearningLogCountsAsync();

            AnsiConsole.MarkupLine("[bold cyan]Learning Schedule[/]");
            AnsiConsole.MarkupLine($"  Enabled:        [green]{config.Enabled}[/]");
            AnsiConsole.MarkupLine($"  Scan interval:  [green]{config.ScanInterval}[/]");
            AnsiConsole.MarkupLine($"  Min cluster:    [green]{config.MinClusterSize}[/]");
            AnsiConsole.MarkupLine($"  Auto-merge:     [green]{config.AutoMerge}[/]");
            AnsiConsole.MarkupLine($"  Last promoted:  {(lastRun.HasValue ? $"[green]{lastRun.Value:g}[/]" : "[grey]never[/]")}");
            AnsiConsole.MarkupLine($"  Log entries:    [green]{total}[/] total, [yellow]{unpromoted}[/] unpromoted");

            if (lastRun.HasValue)
            {
                var nextDue = lastRun.Value + config.ScanInterval;
                var now = DateTimeOffset.UtcNow;
                if (now >= nextDue)
                    AnsiConsole.MarkupLine($"  Next due:       [yellow]NOW[/] (overdue by {now - nextDue:hh\\:mm})");
                else
                    AnsiConsole.MarkupLine($"  Next due:       [green]{nextDue:g}[/] (in {nextDue - now:hh\\:mm})");
            }
            else
            {
                AnsiConsole.MarkupLine($"  Next due:       [grey]after first sentinel disagreements are promoted[/]");
            }

            AnsiConsole.MarkupLine("");
            AnsiConsole.MarkupLine("[grey]Config: learning section in ~/.doomsummarizer/config.yaml[/]");
            return 0;
        }
    }

    /// <summary>
    ///     Check if auto-learning is due on CLI startup.
    ///     Called by commands like scroll/ask after bootstrap.
    ///     If learning is enabled and scan interval has elapsed, runs analysis and optionally auto-merges.
    ///     Returns the number of new exemplars proposed (0 if nothing to learn or not due).
    /// </summary>
    public static async Task<int> CheckAutoLearnAsync(CommandBootstrap boot, CancellationToken ct)
    {
        var config = boot.Config.Learning;
        if (!config.Enabled)
            return 0;

        var (_, unpromoted, _) = await boot.Storage.GetLearningLogCountsAsync();
        if (unpromoted < config.MinClusterSize)
            return 0; // Not enough data yet

        // Check if enough time has passed since last promoted learn run
        var lastRun = await boot.Storage.GetLastLearnRunAsync();
        if (lastRun.HasValue && DateTimeOffset.UtcNow - lastRun.Value < config.ScanInterval)
            return 0; // Not due yet

        // Run analysis
        var analyzer = new LearningAnalyzer(boot.Storage, config.MinClusterSize);
        var result = await analyzer.AnalyzeAsync(ct);

        if (result.TotalProposals == 0)
            return 0;

        var allProposals = result.Gaps.SelectMany(g => g.ProposedExemplars).ToList();

        if (config.AutoMerge)
        {
            // Validate proposals against test matrix
            var validation = await LearningAnalyzer.ValidateProposalsAsync(
                allProposals, boot.Embedding,
                () => BuildTestMatrix().Select(t =>
                    (t.Query, t.ExpectedTopic, t.ExpectedType, t.ExpectedVibe,
                     t.ExpectedComposite, t.ExpectedComplex)).ToList(),
                ct);

            if (validation.Regressions.Count == 0 &&
                validation.Improvements.Count >= config.AutoMergeMinImprovement)
            {
                // Write exemplars and mark promoted
                var yaml = LearningAnalyzer.ProposalsToYaml(allProposals);
                var userDir = Path.Combine(ConfigService.GetConfigDir(), "exemplars");
                Directory.CreateDirectory(userDir);
                var outputPath = Path.Combine(userDir, "auto-learned.yaml");
                await File.WriteAllTextAsync(outputPath, yaml, ct);

                var allEntryIds = result.Gaps.SelectMany(g => g.EntryIds).ToList();
                if (allEntryIds.Count > 0)
                    await boot.Storage.MarkPromotedAsync(allEntryIds);

                AnsiConsole.MarkupLine(
                    $"[green]Auto-learned {allProposals.Count} exemplars (+{validation.Improvements.Count} tests, 0 regressions)[/]");
                return allProposals.Count;
            }
        }
        else
        {
            // Just notify the user that learning data is available
            AnsiConsole.MarkupLine(
                $"[cyan]Learning:[/] {unpromoted} disagreements ready. Run [bold]exemplars --learn[/] to propose {result.TotalProposals} exemplar(s).");
        }

        return 0;
    }

    /// <summary>
    ///     Evaluate a single test case against a classification result.
    /// </summary>
    private static (bool Passed, string? Reason) EvaluateTestCase(TestCase test, QueryClassification result)
    {
        var reasons = new List<string>();

        if (test.ExpectedTopic != null)
        {
            var topTopics = result.Categories
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .Select(kv => kv.Key)
                .ToList();
            if (!topTopics.Contains(test.ExpectedTopic, StringComparer.OrdinalIgnoreCase))
                reasons.Add($"topic: expected={test.ExpectedTopic}, got=[{string.Join(",", topTopics)}]");
        }

        if (test.ExpectedType != null &&
            !result.QueryType.Equals(test.ExpectedType, StringComparison.OrdinalIgnoreCase))
            reasons.Add($"type: expected={test.ExpectedType}, got={result.QueryType}");

        if (test.ExpectedVibe != null)
        {
            var expectNone = test.ExpectedVibe.Equals("none", StringComparison.OrdinalIgnoreCase);
            var vibeMatch = expectNone
                ? result.Vibe == null
                : string.Equals(result.Vibe, test.ExpectedVibe, StringComparison.OrdinalIgnoreCase);
            if (!vibeMatch)
                reasons.Add($"vibe: expected={test.ExpectedVibe}, got={result.Vibe ?? "null"}");
        }

        if (test.ExpectedComposite != null && result.IsComposite != test.ExpectedComposite.Value)
            reasons.Add($"composite: expected={test.ExpectedComposite}, got={result.IsComposite}");

        if (test.ExpectedComplex != null && result.IsComplex != test.ExpectedComplex.Value)
            reasons.Add($"complex: expected={test.ExpectedComplex}, got={result.IsComplex}");

        return reasons.Count == 0
            ? (true, null)
            : (false, string.Join("; ", reasons));
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--list")]
        [Description("List all loaded exemplars (defaults + user)")]
        public bool List { get; init; }

        [CommandOption("--init")]
        [Description("Create the user exemplars directory with a template file")]
        public bool Init { get; init; }

        [CommandOption("--rebuild")]
        [Description("Re-embed all exemplars (run after editing YAML files)")]
        public bool Rebuild { get; init; }

        [CommandOption("--validate")]
        [Description("Check exemplar YAML files for errors")]
        public new bool Validate { get; init; }

        [CommandOption("--test")]
        [Description("Run diagnostic test matrix: classify 80+ prompts and show pass/fail breakdown")]
        public bool Test { get; init; }

        [CommandOption("--benchmark")]
        [Description("Benchmark classifier latency (p50/p95/p99/mean over N iterations)")]
        public bool Benchmark { get; init; }

        [CommandOption("--iterations|-n")]
        [Description("Number of benchmark iterations (default: 100)")]
        public int? BenchmarkIterations { get; init; }

        [CommandOption("--verbose|-v")]
        [Description("Show full breakdown per query in --test or --benchmark mode")]
        public bool Verbose { get; init; }

        [CommandOption("--quiet|-q")]
        [Description("Suppress sample classification output during rebuild")]
        public bool Quiet { get; init; }

        [CommandOption("--gpu")]
        [Description("GPU device ID for ONNX embedding")]
        public int? GpuDevice { get; init; }

        [CommandOption("--learn")]
        [Description("Analyze sentinel disagreements and propose new exemplars")]
        public bool Learn { get; init; }

        [CommandOption("--learn-apply")]
        [Description("Merge proposals into classifier, validate against test matrix, commit if safe")]
        public bool LearnApply { get; init; }

        [CommandOption("--learn-min-cluster")]
        [Description("Minimum disagreement cluster size for learning (default: from config)")]
        public int? LearnMinCluster { get; init; }

        [CommandOption("--learn-schedule")]
        [Description("Show learning schedule: config, last run, next due time")]
        public bool LearnSchedule { get; init; }
    }
}
