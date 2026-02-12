using System.ComponentModel;
using DoomSummarizer.Core.Services;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using DoomSummarizer.Plugins.Runtime;
using DoomSummarizer.Services;
using DoomSummarizer.Services.LongFormGeneration;
using LucidRAG.Decomposer.Analysis;
using LucidRAG.Decomposer.Integration;
using LucidRAG.Decomposer.Models;
using LucidRAG.Decomposer.Orchestration;
using LucidRAG.Decomposer.Refinement;
using Mostlylucid.DocSummarizer.Content;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

public sealed partial class ScrollCommand : AsyncCommand<ScrollCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        if (settings.ListGpus) { await CommandBootstrap.ListGpusAsync(); return 0; }

#if FEATURE_COMPLETE
        // Handle --easter-egg: play the DoomSummarizer animation
        if (settings.EasterEgg)
        {
            await PlayEasterEggAnimationAsync(cancellationToken);
            return 0;
        }
#endif

        // Handle --list-templates
        if (settings.ListTemplates)
        {
            var templateService = new TemplateService();
            await templateService.LoadCustomTemplatesAsync(
                Path.Combine(ConfigService.GetConfigDir(), "templates"));

            AnsiConsole.MarkupLine("[bold cyan]Available Templates:[/]");
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Template")
                .AddColumn("Best For");

            table.AddRow("default", "Standard markdown output");
            table.AddRow("console", "Compact console display");
            table.AddRow("compact", "Minimal bullet list");
            table.AddRow("detailed", "Full details with sentiment");
            table.AddRow("file", "Clean markdown for file export");
            table.AddRow("email", "HTML email with inline styles");
            table.AddRow("newsletter", "Professional newsletter HTML");
            table.AddRow("slack", "Slack-formatted message");
            table.AddRow("json", "Raw JSON for API/automation");
            table.AddRow("image", "Single item with featured image");
            table.AddRow("[bold]blog-article[/]", "[cyan]Multi-section long-form article (auto-detects timeline)[/]");
            table.AddRow("[bold]blog-timeline[/]", "[cyan]Chronological article with timeline structure[/]");
            table.AddRow("[bold]blog-newsletter[/]", "[cyan]Curated newsletter with editorial picks[/]");
            table.AddRow("[bold]blog-newsletter-html[/]", "[cyan]Newsletter as styled HTML email[/]");

            // Show YAML-defined templates
            foreach (var name in templateService.ListDefinitions())
            {
                var def = templateService.GetDefinition(name);
                var desc = def?.Description ?? "Custom YAML template";
                var sections = def?.HasFixedSections == true
                    ? $" ({def.Sections.Count} sections)"
                    : "";
                table.AddRow($"[bold yellow]{Markup.Escape(name)}[/]",
                    $"[yellow]{Markup.Escape(desc)}{sections}[/]");
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine(
                "\n[grey]Custom templates: place .liquid or .yaml files in ~/.doomsummarizer/templates/[/]");
            return 0;
        }

        await using var boot = await CommandBootstrap.CreateAsync(settings.GpuDeviceId, cancellationToken);
        if (settings.DebugPipeline)
            AnsiConsole.MarkupLine(
                $"[grey]Config: {Markup.Escape(ConfigService.LoadedConfigPath ?? "embedded default")}[/]");

        // Handle --clear-storage: wipe all cached data and exit
        if (settings.ClearStorage)
        {
            await boot.Storage.ClearAllAsync();

            // Delete the DuckDB vector store file directly.
            // Opening connections just to clear tables is fragile (DuckDB.NET
            // doesn't support multiple connections to the same file per process).
            var clearVectorDbPath = ConfigService.GetVectorDbPath(boot.Config);
            foreach (var ext in new[] { "", ".wal" })
            {
                var file = clearVectorDbPath + ext;
                if (File.Exists(file))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Could not delete {Markup.Escape(Path.GetFileName(file))}: {Markup.Escape(ex.Message)}[/]");
                    }
                }
            }

            AnsiConsole.MarkupLine(
                "[green]All stored data cleared (segments, queries, entities, circuit state, API usage, vectors)[/]");
            return 0;
        }

        // Auto-backfill FTS5 index if empty (one-time migration for existing KB items)
        if (await boot.Storage.IsFtsIndexEmptyAsync()) await BackfillFtsIndexAsync(boot.Storage, settings.Quiet);

        // Check if background learning is due (lightweight — only queries DB if enabled)
        try
        {
            await ExemplarsCommand.CheckAutoLearnAsync(boot, cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Auto-learn check failed: {ex.Message}");
        }

        // Initialize DuckDB vector store and entity graph store if needed
        var vectorDbPath = ConfigService.GetVectorDbPath(boot.Config);
        if (File.Exists(vectorDbPath) || settings.Graph || settings.BackfillEntityProfiles)
            await boot.InitializeEntityStoresAsync();

        // Handle --backfill-entity-profiles: compute entity profiles for existing KB items
        if (settings.BackfillEntityProfiles)
        {
            if (boot.VectorStore == null)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]No vector store found. Run with --graph flag first to create the knowledge graph.[/]");
                return 1;
            }

            var entityProfileService = new EntityProfileService(boot.Embedding, boot.EntityStore!);
            var graphService = new KnowledgeGraphService(boot.VectorStore, boot.EntityStore!, entityProfileService);

            AnsiConsole.MarkupLine("[cyan]Backfilling entity profiles for existing KB items...[/]");

            var processed = await AnsiConsole.Status()
                .StartAsync("Computing entity profiles...", async ctx =>
                {
                    var total = 0;
                    var batch = 0;
                    while (true)
                    {
                        var count = await graphService.BackfillEntityProfilesAsync(50, cancellationToken);
                        if (count == 0) break;
                        total += count;
                        batch++;
                        ctx.Status($"Computed {total} entity profiles (batch {batch})...");
                    }

                    return total;
                });

            if (processed > 0)
                AnsiConsole.MarkupLine($"[green]Backfill complete: {processed} entity profiles computed[/]");
            else
                AnsiConsole.MarkupLine(
                    "[yellow]No items needed backfilling (all items already have entity profiles, or no entity mentions exist)[/]");

            return 0;
        }

        // ── Local file ingestion: detect file paths in -s/--source or prompt ──
        // When -s points to a file/directory, auto-ingest into a named collection
        string? ingestedSourceFilter = null;
        string? ingestedCollectionName = null;
        var ingestedDocType = IngestDocumentType.Unknown;
        var ingestedSegmentCount = 0;
        var isImageSource = false;

        var candidateSources = settings.Sources ?? [];
        // Also check if the prompt itself is a file path (routed via CliApp smart routing)
        if (candidateSources.Length == 0 && !string.IsNullOrEmpty(settings.Prompt) &&
            (File.Exists(settings.Prompt) || Directory.Exists(settings.Prompt)))
        {
            candidateSources = [settings.Prompt];
        }
        // If prompt looks like a file path but the file doesn't exist, warn instead of
        // treating it as a search query (which produces random/irrelevant results)
        else if (candidateSources.Length == 0 && !string.IsNullOrEmpty(settings.Prompt) &&
                 LooksLikeFilePath(settings.Prompt))
        {
            AnsiConsole.MarkupLine($"[red]File not found: '{Markup.Escape(settings.Prompt)}'[/]");
            var ext = Path.GetExtension(settings.Prompt);
            if (!string.IsNullOrEmpty(ext))
            {
                var registry = new DocumentHandlerRegistry();
                registry.RegisterDefaultHandlers();
                var supported = string.Join(", ", registry.GetSupportedExtensions());
                AnsiConsole.MarkupLine(
                    $"[yellow]Supported document formats: {Markup.Escape(supported)}[/]");
            }

            return 1;
        }

        if (candidateSources.Length > 0)
        {
            var (files, autoName, imgSource) = ResolveLocalSources(candidateSources, settings.Name);
            isImageSource = imgSource;

            // If local files/directories were provided but no supported files were resolved,
            // warn about unsupported format instead of falling through to an empty scroll
            if (files.Count == 0)
            {
                var unsupportedFiles = candidateSources.Where(File.Exists).ToArray();
                var emptyDirs = candidateSources.Where(s => Directory.Exists(s) && !File.Exists(s)).ToArray();
                if (unsupportedFiles.Length > 0 || emptyDirs.Length > 0)
                {
                    var registry = new DocumentHandlerRegistry();
                    registry.RegisterDefaultHandlers();
                    var supported = string.Join(", ", registry.GetSupportedExtensions());
                    foreach (var f in unsupportedFiles)
                        AnsiConsole.MarkupLine(
                            $"[red]Unsupported file format '{Markup.Escape(Path.GetExtension(f))}'.[/]");
                    foreach (var d in emptyDirs)
                        AnsiConsole.MarkupLine(
                            $"[red]No supported files found in '{Markup.Escape(d)}'.[/]");
                    AnsiConsole.MarkupLine(
                        $"[yellow]Supported document formats: {Markup.Escape(supported)}[/]");
                    return 1;
                }
            }

            if (files.Count > 0)
            {
                ingestedCollectionName = autoName;

                // Initialize entity store for NER extraction during file ingestion
                // (character names, locations, etc. feed the knowledge graph for fiction queries)
                if (boot.EntityStore == null)
                    await boot.InitializeEntityStoresAsync();

                AnsiConsole.MarkupLine(
                    $"[cyan]Ingesting {files.Count} file(s) into collection '{Markup.Escape(autoName)}'...[/]");

                await ProgressHelper.RunAsync(async ctx =>
                {
                    var task = ctx.AddTask("[cyan]Processing files[/]", maxValue: 100);
                    var (sourceFilter, count, docType) = await IngestLocalFilesAsync(
                        files, autoName, boot, task, settings.Force, cancellationToken);
                    ingestedSourceFilter = sourceFilter;
                    ingestedSegmentCount = count;
                    ingestedDocType = docType;
                });
            }
        }

        // Initialize template service for output rendering
        var outputTemplates = new TemplateService();
        await outputTemplates.LoadCustomTemplatesAsync(Path.Combine(ConfigService.GetConfigDir(), "templates"));

        var ollama = boot.CreateOllama();
        var circuitBreaker = await boot.InitializeCircuitBreakerAsync();
        if (settings.DebugPipeline)
            circuitBreaker.PrintCircuitStatus();

        AnsiConsole.MarkupLine("[grey]Detecting LLM providers...[/]");
        var llmRouter = await boot.InitializeLlmStackAsync(circuitBreaker, cancellationToken);
        AnsiConsole.MarkupLine($"[green]LLM:[/] {FormattingHelpers.Esc(llmRouter.StatusDescription)}");

        using var httpClient = HttpClientFactory.CreateDefault();

        // Status helper: overwrites the previous status line to keep output compact.
        // Only the latest status is visible at any time.
        var hasStatusLine = false;

        void WriteStatus(string markup)
        {
            if (!settings.Full) return;
            if (hasStatusLine)
                Console.Write("\x1b[1A\x1b[2K"); // Move up one line, clear it
            AnsiConsole.MarkupLine(markup);
            hasStatusLine = true;
        }

        if (settings.Full)
            RenderStartupPanel(boot.Config, ConfigService.LoadedConfigPath, llmRouter, boot.Embedding, boot.ApiKeys!,
                circuitBreaker, settings.Prompt);

        // NER preprocessing: extract entities from query BEFORE the LLM sentinel
        // This gives us structured search filters, cached segment lookups, and URL dedup
        QueryNerContext? nerContext = null;
        if (!string.IsNullOrEmpty(settings.Prompt))
        {
            nerContext = await QueryPreprocessor.PreprocessAsync(
                settings.Prompt, boot.Embedding, boot.Storage, settings.Locale, cancellationToken);

            if (nerContext.HasEntities)
            {
                var entityStr = string.Join(", ", nerContext.Entities
                    .Select(e => $"{e.Text} ({e.Type})"));
                WriteStatus($"[grey]NER: {Markup.Escape(entityStr)}[/]");

                // Show recognizer signals (dates, numbers, etc.)
                if (nerContext.RecognizerSignals?.HasAnySignals == true)
                {
                    var signals = nerContext.RecognizerSignals;
                    var signalParts = new List<string>();
                    if (signals.DateTimes.Count > 0)
                        signalParts.Add($"dates:[{string.Join(", ", signals.DateTimes.Select(d => d.Text))}]");
                    if (signals.Numbers.Count > 0)
                        signalParts.Add($"nums:[{string.Join(", ", signals.Numbers.Select(n => n.Text))}]");
                    WriteStatus($"[grey]Recognizers: {Markup.Escape(string.Join(" ", signalParts))}[/]");
                }
            }
        }

        // Interpret the prompt if provided
        // Skip sentinel interpretation in --name mode (KB query doesn't need web source detection)
        InterpretedPrompt? interpreted = null;
        var vibe = settings.Vibe;
        var isNamedKbQuery = !string.IsNullOrWhiteSpace(settings.Name) || ingestedSourceFilter != null;

        if (!string.IsNullOrEmpty(settings.Prompt) && !isNamedKbQuery)
        {
            WriteStatus($"[grey]Interpreting: {Markup.Escape(settings.Prompt)}[/]");

            var interpreter = new PromptInterpreter(ollama, boot.Embedding);
            interpreted = await interpreter.InterpretAsync(settings.Prompt, nerContext);

            // Composite query handling: add subqueries as additional search queries
            // This ensures each part of a composite question gets searched separately
            if (interpreted.SentinelIntent?.HasSubqueries == true)
                foreach (var subquery in interpreted.SentinelIntent.Subqueries!)
                    // Don't add duplicates or near-duplicates
                    if (!interpreted.SearchQueries.Any(sq =>
                            sq.Contains(subquery, StringComparison.OrdinalIgnoreCase) ||
                            subquery.Contains(sq, StringComparison.OrdinalIgnoreCase)))
                        interpreted.SearchQueries.Add(subquery);

            // Show embedding classification in debug mode
            if (settings.DebugPipeline && interpreted.EmbeddingClassification != null)
            {
                var ec = interpreted.EmbeddingClassification;
                var embCats = string.Join(", ", ec.Categories
                    .OrderByDescending(kv => kv.Value).Take(5)
                    .Select(kv => $"{kv.Key}={kv.Value:F2}"));
                var vibeStr = ec.Vibe != null ? $" | vibe={ec.Vibe} ({ec.VibeConfidence:F2})" : "";
                var flagsStr = (ec.IsComposite ? " | composite" : "") + (ec.IsComplex ? " | complex" : "");
                AnsiConsole.MarkupLine(
                    $"[grey]Embedding: {Markup.Escape(embCats)} | type={ec.QueryType} ({ec.QueryTypeConfidence:F2}){Markup.Escape(vibeStr)}{flagsStr}[/]");
                if (ec.TopMatches.Count > 0)
                {
                    var topStr = string.Join(", ", ec.TopMatches.Take(3)
                        .Select(m => $"\"{Markup.Escape(m.Question)}\" ({m.Score:F2})"));
                    AnsiConsole.MarkupLine($"[grey]  Top matches: {topStr}[/]");
                }
                if (interpreted.SentinelIntent != null)
                {
                    var sentCats = string.Join(", ", (interpreted.SentinelIntent.Categories ?? new())
                        .OrderByDescending(kv => kv.Value).Take(5)
                        .Select(kv => $"{kv.Key}={kv.Value:F2}"));
                    AnsiConsole.MarkupLine(
                        $"[grey]Final: {Markup.Escape(sentCats)} | intent={interpreted.SentinelIntent.Intent} | vibe={interpreted.Vibe}[/]");
                }
            }

            // Use interpreted vibe unless explicitly overridden
            if (settings.Vibe == "neutral" && interpreted.Vibe != "neutral")
                vibe = interpreted.Vibe;

            var sourcesStr = string.Join(", ", interpreted.Sources
                .Concat(interpreted.Websites)
                .Concat(interpreted.SearchQueries.Select(q => $"search:{q}")));

            // Show temporal extraction from sentinel (LLM-driven, not regex!)
            var temporalInfo = "";
            if (interpreted.SentinelIntent != null)
            {
                var si = interpreted.SentinelIntent;
                var temporalParts = new List<string>();
                if (si.RequiresFresh) temporalParts.Add("requires_fresh");
                if (!string.IsNullOrEmpty(si.TimeSensitivity) && si.TimeSensitivity != "any")
                    temporalParts.Add($"time={si.TimeSensitivity}");
                if (si.DateRange != null)
                    temporalParts.Add($"range={si.DateRange.Original ?? si.DateRange.Unit}");
                if (temporalParts.Count > 0)
                    temporalInfo = $", temporal=[{string.Join(", ", temporalParts)}]";
            }

            WriteStatus(
                $"[grey]Detected: sources=[[{Markup.Escape(sourcesStr)}]], vibe={vibe}{Markup.Escape(temporalInfo)}[/]");

            // Show selected sources (always, unless --quiet)
            if (!settings.Quiet)
            {
                var selectedSources = interpreted.Sources
                    .Concat(interpreted.Websites)
                    .Concat(interpreted.SearchQueries.Select(q => $"search:{q}"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                RenderSelectedSources(selectedSources, boot.ApiKeys!, circuitBreaker);
            }
        }

        // Detect search_only intent: weather, scores, prices — needs search, not feeds
        var isSearchOnlyIntent = interpreted?.SentinelIntent?.Intent == "search_only";
        if (isSearchOnlyIntent && interpreted != null && interpreted.Limit > 10)
            interpreted.Limit = 10; // Cap fetch count for direct-answer queries

        // ─── Decomposer: classify, analyze, plan ───
        // Runs AFTER PromptInterpreter, BEFORE cache check.
        // Fast-path: simple queries get concept classification + sentinel enhancement only.
        // Complex: multi-topic, tool-use, comparisons get full decomposition.
        DecompositionResult? decomposition = null;
        DecompositionEnrichment? decompositionEnrichment = null;

        if (!string.IsNullOrEmpty(settings.Prompt))
            try
            {
                var decomposer = new DecompositionPipeline(
                    new ComplexityClassifier(boot.Embedding),
                    new ConceptClassifier(boot.Embedding),
                    new IQueryAnalyzer[]
                    {
                        new ReferenceExtractor(),
                        new StructuralAnalyzer(boot.Embedding),
                        new EntityRelationAnalyzer(boot.Embedding),
                        new TemporalAnalyzer(),
                        new SemanticClusterAnalyzer(boot.Embedding),
                        new ToolUseAnalyzer(boot.Embedding)
                    },
                    new SentinelRefiner(),
                    boot.Embedding);

                // Build sentinel refinement input from PromptInterpreter output
                object? sentinelInput = null;
                if (interpreted?.SentinelIntent != null)
                {
                    var si = interpreted.SentinelIntent;
                    sentinelInput = DoomSummarizerAdapter.ToRefinementInput(
                        si.IsComposite,
                        si.Subqueries?.ToList(),
                        si.CorrectedQuery,
                        si.FilterKeywords?.ToList(),
                        si.SearchQueries?.ToList(),
                        si.Entities?.ToList(),
                        si.TimeSensitivity,
                        si.RequiresFresh,
                        si.Intent,
                        si.Categories?.ToDictionary(k => k.Key, k => k.Value));
                }

                var hasUrls = nerContext?.RecognizerSignals?.Urls.Count > 0
                              || interpreted?.Websites.Count > 0;
                var hasDateTimes = nerContext?.RecognizerSignals?.DateTimes.Count > 0;

                decomposition = await decomposer.DecomposeAsync(
                    settings.Prompt,
                    nerContext?.Entities?.ToList(),
                    hasUrls,
                    hasDateTimes,
                    sentinelInput,
                    cancellationToken);

                decompositionEnrichment = DoomSummarizerAdapter.GetEnrichment(decomposition);

                if (settings.DebugPipeline)
                {
                    var conceptPolicy = new ConceptRegistry().GetPolicy(decomposition.Concept);
                    WriteStatus($"[grey]Decomposer: complexity={decomposition.Complexity}, " +
                                $"concept={decomposition.Concept} (budget={conceptPolicy.FetchBudget}), " +
                                $"nodes={decomposition.Nodes.Count}, " +
                                $"fastPath={decomposition.IsFastPath}, " +
                                $"tools={decomposition.HasToolActions}[/]");

                    if (decomposition.HasToolActions)
                        foreach (var tool in decompositionEnrichment.ToolActions)
                        {
                            var paramStr = string.Join(", ", tool.Parameters.Select(p => $"{p.Key}={p.Value}"));
                            WriteStatus(
                                $"[grey]  Tool: {tool.Tool} → {Markup.Escape(tool.Intent)} ({Markup.Escape(paramStr)})[/]");
                        }

                    if (!decomposition.IsFastPath && decomposition.Nodes.Count > 1)
                        foreach (var node in decomposition.Nodes)
                            WriteStatus(
                                $"[grey]  Node: {Markup.Escape($"[{node.Type}]")} {Markup.Escape(node.Query)}[/]");
                }

                // Feed decomposer content references back into interpreted prompt websites
                if (interpreted != null && decompositionEnrichment.ContentReferences.Count > 0)
                    foreach (var reference in decompositionEnrichment.ContentReferences)
                        if (reference.Kind == ContentReferenceKind.Url &&
                            !interpreted.Websites.Contains(reference.Uri))
                            interpreted.Websites.Add(reference.Uri);

                // Feed decomposer sub-query search terms back into interpreted prompt
                if (interpreted != null && !decomposition.IsFastPath)
                    foreach (var node in decomposition.Nodes.Where(n =>
                                 n.Type == QueryNodeType.Atomic && n.SearchQueries.Count > 0))
                    foreach (var sq in node.SearchQueries)
                        if (!interpreted.SearchQueries.Any(existing =>
                                existing.Contains(sq, StringComparison.OrdinalIgnoreCase) ||
                                sq.Contains(existing, StringComparison.OrdinalIgnoreCase)))
                            interpreted.SearchQueries.Add(sq);
            }
            catch (Exception ex)
            {
                // Decomposer failure is non-fatal — the existing pipeline works without it
                if (settings.DebugPipeline)
                    WriteStatus($"[yellow]Decomposer failed (non-fatal): {Markup.Escape(ex.Message)}[/]");
            }

        // Resolve vibe via VibeResolver (checks lens YAML files, then config vibes, then custom text)
        var resolvedVibe = boot.VibeResolver.Resolve(vibe);
        var vibePrompt = resolvedVibe.Prompt;

        var ollamaAvailable = !settings.NoLlm && await ollama.IsAvailableAsync();
        if (!ollamaAvailable)
            WriteStatus("[yellow]Warning: Ollama not available. Summaries will be limited.[/]");

        // Query feedback: check for similar recent query to reuse cached segments
        var queryText = interpreted?.RawPrompt ?? settings.Prompt ?? "";
        float[]? earlyQueryEmbedding = null;
        QueryMatch? cachedQuery = null;
        var useCachedSegments = false;

        if (!settings.Force && !settings.LocalOnly && string.IsNullOrWhiteSpace(settings.Name) &&
            !string.IsNullOrWhiteSpace(queryText))
        {
            // Temporal intent bypass: if query needs fresh data, skip cache entirely
            var requiresFresh = interpreted?.SentinelIntent?.RequiresFresh == true;
            var isTimeSensitive = interpreted?.SentinelIntent?.TimeSensitivity is "breaking" or "today";

            if (requiresFresh || isTimeSensitive)
            {
                if (settings.DebugPipeline)
                    WriteStatus(
                        $"[grey]Cache bypass: temporal intent detected (fresh={requiresFresh}, time={interpreted?.SentinelIntent?.TimeSensitivity})[/]");
            }
            else
            {
                earlyQueryEmbedding = await boot.Embedding.EmbedAsync(queryText, cancellationToken);
                cachedQuery = await boot.Storage.FindSimilarQueryAsync(earlyQueryEmbedding, 0.97);
                if (cachedQuery != null)
                {
                    useCachedSegments = true;
                    var ageMin = (int)(DateTimeOffset.UtcNow - cachedQuery.IssuedAt).TotalMinutes;
                    WriteStatus(
                        $"[grey]Reusing {cachedQuery.ItemIds.Count} segments ({cachedQuery.Similarity:F2} match, {ageMin}m ago)[/]");
                }
            }
        }

        // Clear the status line before Progress takes over rendering
        if (hasStatusLine)
            Console.Write("\x1b[1A\x1b[2K");

        var items = new List<ContentItem>();
        var uniqueItems = new List<ContentItem>();

        // Rendering state — hoisted so console output happens after progress bars are gone
        var analyzedItems =
            new List<(string title, string summary, string topic, float sentiment, string url, double relevance)>();
        var finalSummary = "";
        var template = "default";
        var isBlogTemplate = false;
        DigestData? templateData = null;
        string? streamingPrompt = null; // Set when using streaming synthesis path
        var allEntities = new List<NerEntity>();
        var articleEntityMap = new List<(ContentItem item, List<NerEntity> entities)>();
        var extractEntities = false;
        var linkCacheHits = 0;
        var linksSkippedByRelevance = 0;
        List<string>? missingTerms = null; // Terms from query not found in corpus (Lucene verified)

        await ProgressHelper.RunAsync(async ctx =>
        {
            // Stage 1: Fetch content (or load from knowledge base)
            // --name implies --local mode; file ingestion also forces local mode
            var isLocalMode = settings.LocalOnly || !string.IsNullOrWhiteSpace(settings.Name)
                                                 || ingestedSourceFilter != null;
            var fetchTask = ctx.AddTask(
                isLocalMode ? "[cyan]Loading from knowledge base[/]" : "[cyan]Fetching content[/]",
                maxValue: 100);

            // --local / --name mode: skip ALL fetching, query stored knowledge base only
            // Delegates to shared RetrievalPipeline (Lucene FTS + embedding HNSW + entity profiles + RRF)
            if (isLocalMode)
            {
                var localQuery = interpreted?.RawPrompt ?? settings.Prompt ?? "";

                // Derive source filter: ingested files take priority, then --name + --source merge
                var sourceFilter = ingestedSourceFilter;
                if (sourceFilter == null && !string.IsNullOrWhiteSpace(settings.Name))
                {
                    // Check if the name matches a known collection (crawl:X, page:X, file:X)
                    var collections = await boot.Storage.GetCollectionsAsync();
                    var firstName = settings.Name.Split(',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                    var matchingCollection = firstName != null
                        ? collections.FirstOrDefault(c =>
                            c.Source.Equals(firstName, StringComparison.OrdinalIgnoreCase) ||
                            c.Source.Equals($"crawl:{firstName}", StringComparison.OrdinalIgnoreCase) ||
                            c.Source.Equals($"page:{firstName}", StringComparison.OrdinalIgnoreCase) ||
                            c.Source.Equals($"file:{firstName}", StringComparison.OrdinalIgnoreCase))
                        : null;

                    // If the name resolves to a single known collection, use it directly
                    if (matchingCollection != null && !settings.Name.Contains(','))
                        sourceFilter = matchingCollection.Source;
                    else
                        sourceFilter = null; // Multi-name or unknown — handled by merged filter below
                }
                else if (sourceFilter == null)
                {
                    sourceFilter = settings.Sources?.FirstOrDefault(s =>
                        s.StartsWith("crawl:", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("page:", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("file:", StringComparison.OrdinalIgnoreCase));
                }

                // For file collections: detect document type from stored items if not already known
                var isFileSource = sourceFilter?.StartsWith("file:") == true;
                if (isFileSource && ingestedDocType == IngestDocumentType.Unknown)
                {
                    var sampleItems = await boot.Storage.GetRecentItemsAsync(36500, sourceFilter);
                    ingestedSegmentCount = sampleItems.Count;
                    if (sampleItems.Count > 0)
                    {
                        var sampleText = string.Join("\n", sampleItems.Take(5)
                            .Select(i => $"{i.Title} {i.Content ?? i.Summary ?? ""}"));
                        if (sampleText.Length > 5000) sampleText = sampleText[..5000];
                        ingestedDocType = DetectDocumentType(sampleText);
                    }
                }

                // Document-type-specific default prompts when no explicit query given
                if (string.IsNullOrWhiteSpace(localQuery) && (ingestedSourceFilter != null || isFileSource))
                    localQuery = ingestedDocType switch
                    {
                        IngestDocumentType.Fiction =>
                            "Analyze this work of fiction: identify the main characters and their roles, the setting, plot summary, key themes, and significant events",
                        IngestDocumentType.NonFiction =>
                            "Summarize this book: identify the main arguments, key concepts, supporting evidence, and conclusions",
                        IngestDocumentType.Academic =>
                            "Summarize this paper: research questions, methodology, key findings, conclusions, and limitations",
                        IngestDocumentType.Technical =>
                            "Summarize the key concepts, architecture, setup instructions, and important features",
                        _ => "Summarize the key themes, arguments, and important points of this document"
                    };

                var collectionLabel = sourceFilter ?? "all";
                var collectionName = ingestedCollectionName ?? settings.Name ?? "default";
                fetchTask.Value = 10;

                if (!string.IsNullOrWhiteSpace(localQuery))
                {
                    // Adaptive retrieval: books need much broader coverage than news articles
                    var adaptiveTopK = isFileSource
                        ? ingestedDocType switch
                        {
                            IngestDocumentType.Fiction => Math.Clamp(ingestedSegmentCount / 3, 50, 200),
                            IngestDocumentType.NonFiction => Math.Clamp(ingestedSegmentCount / 4, 40, 150),
                            _ => Math.Clamp(ingestedSegmentCount / 5, 30, 100)
                        }
                        : settings.Limit * 2;
                    var adaptiveMinRelevance = isFileSource ? 0.05f : 0.15f;

                    // Fiction-aware entity expansion: when querying about "characters" in fiction,
                    // look up PER entities from the knowledge graph to boost retrieval
                    var queryEntities = interpreted?.SentinelIntent?.Entities ?? new List<string>();
                    if (isFileSource && ingestedDocType == IngestDocumentType.Fiction
                                     && boot.EntityStore != null)
                    {
                        var lowerQuery = localQuery.ToLowerInvariant();
                        var isCharacterQuery = lowerQuery.Contains("character") ||
                                               lowerQuery.Contains("protagonist") ||
                                               lowerQuery.Contains("people") ||
                                               lowerQuery.Contains("cast") ||
                                               lowerQuery.Contains("who ");
                        if (isCharacterQuery)
                            try
                            {
                                var topPeople = await boot.EntityStore.GetTopEntitiesAsync(
                                    20, "PER");
                                var characterNames = topPeople
                                    .Where(e => e.MentionCount >= 2)
                                    .Select(e => e.Name)
                                    .ToList();
                                if (characterNames.Count > 0)
                                {
                                    queryEntities = queryEntities.Concat(characterNames)
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList();
                                    if (settings.DebugPipeline)
                                        AnsiConsole.MarkupLine(
                                            $"[grey]Fiction entity expansion: {FormattingHelpers.Esc(string.Join(", ", characterNames))}[/]");
                                }
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[yellow]Entity store query failed: {FormattingHelpers.Esc(ex.Message)}[/]");
                            }
                    }

                    // Build merged source filters for multi-name support
                    var mergedSourceFilters = sourceFilter != null
                        ? new[] { sourceFilter }
                        : SourceFilterSet.MergeNameAndSource(settings.Name, settings.Sources);

                    var retrieval = new RetrievalPipeline(boot.Embedding, boot.Storage, boot.EntityStore,
                        boot.Config.Expansion);
                    var retrievalResult = await retrieval.SearchAsync(localQuery, new RetrievalOptions
                    {
                        SourceFilters = mergedSourceFilters,
                        CollectionName = collectionName,
                        TopK = adaptiveTopK,
                        MinRelevance = adaptiveMinRelevance,
                        IsKnowledgeBase = true,
                        UseEmbeddingDedup = true,
                        // Named KB: all items are from a curated collection — strict
                        // authority/freshness gates add no value, only hurt recall for
                        // vague or overview queries like "What is this?"
                        RelaxScoringGates = true,
                        QueryEntities = queryEntities.Count > 0 ? queryEntities : null
                    }, cancellationToken);

                    items.AddRange(retrievalResult.Items);

                    // Term verification: check if key content terms from the query exist in the corpus.
                    // If Lucene returns zero hits for a specific term, the corpus has no knowledge
                    // of it — a definitive signal that the LLM should not claim support.
                    missingTerms = TermVerifier.Verify(
                        localQuery, boot.Storage.DataPath, collectionName,
                        null, // KB queries check all sources in the collection
                        interpreted?.SentinelIntent?.Entities,
                        nerContext?.Entities?.Select(e => e.Text).ToList());
                    if (missingTerms != null && settings.DebugPipeline)
                        AnsiConsole.MarkupLine(
                            $"[grey]Term verification: missing from corpus: {Markup.Escape(string.Join(", ", missingTerms))}[/]");
                }
                else
                {
                    // No query: return most recent from the collection
                    var storedLocal = sourceFilter != null
                        ? await boot.Storage.GetRecentItemsAsync(365, sourceFilter)
                        : await boot.Storage.GetRecentItemsAsync(30);

                    var localItems = storedLocal
                        .Where(s => !string.IsNullOrEmpty(s.Summary) || !string.IsNullOrEmpty(s.Title))
                        .Select(s => s.ToContentItem())
                        .OrderByDescending(i => i.FetchedAt)
                        .Take(settings.Limit)
                        .ToList();
                    items.AddRange(localItems);
                }

                fetchTask.Value = 100;
                fetchTask.Description = $"[cyan]KB: {items.Count} items matched[/]";
                if (settings.DebugPipeline)
                    AnsiConsole.MarkupLine(
                        $"[grey]KB query ({Markup.Escape(collectionLabel)}): {items.Count} items matched[/]");
            }

            // Segment reuse: load cached items from a similar recent query
            if (!isLocalMode && useCachedSegments && cachedQuery != null)
            {
                var cachedStored = await boot.Storage.GetItemsByIdsAsync(cachedQuery.ItemIds);
                var cachedItems = cachedStored
                    .Where(s => !string.IsNullOrEmpty(s.Summary) || !string.IsNullOrEmpty(s.Title))
                    .Select(s => s.ToContentItem())
                    .ToList();

                // Relevance gate: verify cached segments have sufficient salience for THIS query
                // Only reuse cache when local data is genuinely good — otherwise fetch fresh
                if (earlyQueryEmbedding != null && cachedItems.Count > 0)
                {
                    var withEmbeddings = cachedItems.Where(i => i.Embedding != null).ToList();
                    if (withEmbeddings.Count > 0)
                    {
                        var similarities = withEmbeddings
                            .Select(i => VectorMath.CosineSimilarity(earlyQueryEmbedding, i.Embedding!))
                            .OrderByDescending(s => s)
                            .ToList();

                        var topRelevance = similarities.Take(5).Average();
                        var bestSingle = similarities.First();
                        var aboveThreshold = similarities.Count(s => s >= 0.30f);

                        // Require: (1) top-5 average >= 0.40, AND (2) best single >= 0.50,
                        // AND (3) at least 3 items above 0.30 — ensures genuine salience
                        if (topRelevance < 0.40f || bestSingle < 0.50f || aboveThreshold < 3)
                        {
                            useCachedSegments = false;
                            if (settings.Full)
                                AnsiConsole.MarkupLine(
                                    $"[yellow]Cached segments lack salience for this query (avg={topRelevance:F2}, best={bestSingle:F2}, above-0.30={aboveThreshold}) — fetching fresh[/]");
                        }
                        else if (settings.DebugPipeline)
                        {
                            AnsiConsole.MarkupLine(
                                $"[grey]Cache salience: avg={topRelevance:F2}, best={bestSingle:F2}, above-0.30={aboveThreshold} — reusing[/]");
                        }
                    }
                    else
                    {
                        // No embeddings to evaluate — can't verify salience, fetch fresh
                        useCachedSegments = false;
                        if (settings.Full)
                            AnsiConsole.MarkupLine("[yellow]Cached segments have no embeddings — fetching fresh[/]");
                    }
                }

                if (useCachedSegments)
                {
                    items.AddRange(cachedItems);
                    fetchTask.Value = 100;
                    fetchTask.Description = $"[green]Reused {cachedItems.Count} cached items (skipped fetching)[/]";
                }
            }

            // Detect query type early — used for roundup date-gating inside fetch mode
            // AND for adaptive RRF weights in Stage 2.5 (outside the fetch block)
            var earlyQueryType =
                QueryTypeDetector.Detect(interpreted?.RawPrompt ?? settings.Prompt, interpreted?.SentinelIntent);

            if (!isLocalMode && !useCachedSegments)
            {
                // Normal fetch mode
                var fetchTasks = new List<Task<List<ContentItem>>>();

                // Determine what to fetch
                var sources = settings.Sources?.ToList() ?? [];
                if (interpreted != null)
                {
                    sources.AddRange(interpreted.Sources);
                    sources.AddRange(interpreted.Websites);
                    sources.AddRange(interpreted.SearchQueries.Select(q => $"search:{q}"));
                }

                // If nothing specified, use general search sources (not tech-specific)
                // Google News search + DuckDuckGo cover most topics
                if (sources.Count == 0)
                {
                    var query = interpreted?.RawPrompt ?? settings.Prompt;
                    sources.AddRange([$"gnews:{query}", $"search:{query}"]);
                }

                // Dedupe sources
                sources = sources.Distinct().ToList();

                var perSourceLimit = Math.Max(3, settings.Limit * 3 / Math.Max(1, sources.Count));

                // Initialize plugin registry (builtins + runtime plugins)
                var pluginRegistry = new SourcePluginRegistry();
                var outputRegistry = new OutputPluginRegistry();
                BuiltinPlugins.RegisterAllSources(pluginRegistry);
                BuiltinPlugins.RegisterAllOutputs(outputRegistry);

                // Load runtime plugins from manifest (~/.doomsummarizer/plugins/)
                var pluginManager = new PluginManager(httpClient);
                pluginManager.LoadAndRegister(pluginRegistry, outputRegistry);

                var pluginServices = new SourcePluginServices
                {
                    HttpClient = httpClient,
                    ApiKeys = boot.ApiKeys!,
                    ApiBudget = boot.ApiBudget!,
                    CircuitBreaker = circuitBreaker
                };
                await pluginRegistry.InitializeAllAsync(pluginServices, cancellationToken);

                // Create parallel fetch tasks via plugin registry
                foreach (var source in sources)
                {
                    var fetchCtx = SourceFetchContext.ParseWithCompositeKeys(
                        source,
                        pluginRegistry.AllKeys,
                        interpreted?.RawPrompt ?? settings.Prompt,
                        perSourceLimit,
                        vibe,
                        boot.Config,
                        interpreted?.RawPrompt ?? settings.Prompt);

                    var plugin = pluginRegistry.FindByKey(fetchCtx.SourceKey);
                    if (plugin != null)
                    {
                        var capturedCtx = fetchCtx;
                        fetchTasks.Add(Task.Run(async () =>
                            await plugin.FetchAsync(capturedCtx, cancellationToken)));
                    }
                    else if (fetchCtx.SourceKey.StartsWith("http"))
                    {
                        // URL fallback — route to the web plugin
                        var webPlugin = pluginRegistry.FindByKey("web");
                        if (webPlugin != null)
                        {
                            var webCtx = fetchCtx with { RawSource = source };
                            fetchTasks.Add(Task.Run(async () =>
                                await webPlugin.FetchAsync(webCtx, cancellationToken)));
                        }
                    }
                }

                fetchTask.Value = 20;

                // Wait for all fetches in parallel
                var results = await Task.WhenAll(fetchTasks);
                foreach (var result in results) items.AddRange(result);

                // Fix broken URLs from aggregators (Google News, Bing News)
                // These return redirect URLs that often return 400/404
                var urlFixer = new UrlFixerService(httpClient);
                var urlsNeedingFix = items.Count(i => UrlFixerService.NeedsFix(i.Url));
                if (urlsNeedingFix > 0)
                {
                    if (settings.DebugPipeline)
                        AnsiConsole.MarkupLine($"[grey]URL fixer: resolving {urlsNeedingFix} aggregator URLs...[/]");
                    await urlFixer.FixUrlsAsync(items, cancellationToken);
                }

                fetchTask.Value = 80;

                // Source diversity fallback: if initial fetch returned too few items,
                // auto-add search fallbacks to fill the gap via plugin registry
                var minDesired = Math.Max(5, settings.Limit / 3);
                if (items.Count < minDesired && !string.IsNullOrEmpty(interpreted?.RawPrompt ?? settings.Prompt))
                {
                    var fallbackQuery = interpreted?.RawPrompt ?? settings.Prompt ?? "";
                    var fallbackSources = new List<Task<List<ContentItem>>>();

                    var hasSearchSource = sources.Any(s =>
                        s.StartsWith("search:", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("gsearch", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("brave", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("serper", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("tavily", StringComparison.OrdinalIgnoreCase));

                    if (!hasSearchSource)
                    {
                        var searchPlugin = pluginRegistry.FindByKey("search");
                        if (searchPlugin != null)
                        {
                            var searchCtx = new SourceFetchContext
                            {
                                RawSource = $"search:{fallbackQuery}",
                                SourceKey = "search",
                                SubParams = [fallbackQuery],
                                Query = fallbackQuery,
                                RawPrompt = fallbackQuery,
                                Limit = perSourceLimit,
                                Vibe = vibe,
                                Config = boot.Config
                            };
                            fallbackSources.Add(Task.Run(async () =>
                                await searchPlugin.FetchAsync(searchCtx, cancellationToken)));
                        }
                    }

                    var hasNewsSource = sources.Any(s =>
                        s.StartsWith("gnews", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("newsapi", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("newsdata", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("currents", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("bravenews", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("serpernews", StringComparison.OrdinalIgnoreCase));

                    if (!hasNewsSource)
                    {
                        var searchPlugin = pluginRegistry.FindByKey("search");
                        if (searchPlugin != null)
                        {
                            if (boot.ApiKeys!.IsAvailable("newsapi"))
                            {
                                var newsCtx = new SourceFetchContext
                                {
                                    RawSource = $"newsapi:{fallbackQuery}",
                                    SourceKey = "newsapi",
                                    SubParams = [fallbackQuery],
                                    Query = fallbackQuery,
                                    Limit = perSourceLimit,
                                    Vibe = vibe,
                                    Config = boot.Config
                                };
                                fallbackSources.Add(Task.Run(async () =>
                                    await searchPlugin.FetchAsync(newsCtx, cancellationToken)));
                            }

                            if (boot.ApiKeys!.IsAvailable("currents"))
                            {
                                var currentsCtx = new SourceFetchContext
                                {
                                    RawSource = $"currents:{fallbackQuery}",
                                    SourceKey = "currents",
                                    SubParams = [fallbackQuery],
                                    Query = fallbackQuery,
                                    Limit = perSourceLimit,
                                    Vibe = vibe,
                                    Config = boot.Config
                                };
                                fallbackSources.Add(Task.Run(async () =>
                                    await searchPlugin.FetchAsync(currentsCtx, cancellationToken)));
                            }

                            // GNews RSS as final news fallback if no API keys
                            if (!boot.ApiKeys!.IsAvailable("newsapi") && !boot.ApiKeys!.IsAvailable("currents"))
                            {
                                var gnewsPlugin = pluginRegistry.FindByKey("gnews");
                                if (gnewsPlugin != null)
                                {
                                    var gnewsCtx = new SourceFetchContext
                                    {
                                        RawSource = $"gnews:{fallbackQuery}",
                                        SourceKey = "gnews",
                                        SubParams = [fallbackQuery],
                                        Query = fallbackQuery,
                                        Limit = perSourceLimit,
                                        Vibe = vibe,
                                        Config = boot.Config
                                    };
                                    fallbackSources.Add(Task.Run(async () =>
                                        await gnewsPlugin.FetchAsync(gnewsCtx, cancellationToken)));
                                }
                            }
                        }
                    }

                    if (fallbackSources.Count > 0)
                    {
                        var fallbackResults = await Task.WhenAll(fallbackSources);
                        var fallbackCount = 0;
                        foreach (var fb in fallbackResults)
                        {
                            items.AddRange(fb);
                            fallbackCount += fb.Count;
                        }

                        if (fallbackCount > 0)
                            fetchTask.Description = $"[cyan]Fallback: +{fallbackCount} items[/]";
                        if (settings.DebugPipeline && fallbackCount > 0)
                            AnsiConsole.MarkupLine(
                                $"[grey]Diversity fallback: added {fallbackCount} items from backup sources[/]");
                    }
                }

                fetchTask.Value = 100;
                fetchTask.Description = $"[green]Fetched {items.Count} items[/]";

                // Apply topic filter ONLY to items from generic sources (hn, reddit, lobsters, devto)
                // that don't natively filter by topic. Skip for search/gnews/category-specific feeds
                // since those already fetched topic-relevant content.
                if (interpreted?.Topics.Count > 0)
                {
                    var topicTerms = interpreted.Topics.SelectMany(t => t.Split(' ')).ToList();
                    // Sources that already searched/filtered for the topic
                    var topicAwareSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "gnews", "search", "bbc", "guardian", "cnn", "reuters", "currents",
                        "factcheck", "spaceflight", "earthquake", "wikipedia", "arxiv"
                    };

                    var preFilterCount = items.Count;

                    var filtered = items.Where(item =>
                    {
                        // Keep all items from topic-aware sources (they already filtered)
                        if (topicAwareSources.Contains(item.Source))
                            return true;

                        // Filter generic sources by topic terms
                        var text = $"{item.Title} {item.Content ?? ""}".ToLowerInvariant();
                        return topicTerms.Any(term => text.Contains(term.ToLowerInvariant()));
                    }).ToList();

                    // Graceful fallback: if topic filter is too aggressive (< 5 items),
                    // keep all topic-aware items + relax to allow partial term matches
                    if (filtered.Count < 5 && preFilterCount >= 5)
                    {
                        // Softer filter: any single word from topic terms (not full phrases)
                        var singleWords = topicTerms
                            .SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            .Where(w => w.Length > 3)
                            .Select(w => w.ToLowerInvariant())
                            .Distinct()
                            .ToList();

                        filtered = items.Where(item =>
                        {
                            if (topicAwareSources.Contains(item.Source))
                                return true;
                            var text = $"{item.Title} {item.Content ?? ""}".ToLowerInvariant();
                            return singleWords.Any(word => text.Contains(word));
                        }).ToList();

                        // If still too few, skip topic filter entirely (rely on downstream relevance)
                        if (filtered.Count < 5)
                        {
                            fetchTask.Description = "[cyan]Topic filter: skipped (too few)[/]";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine(
                                    $"[grey]Topic filter: {preFilterCount} → {filtered.Count} (too aggressive, skipping)[/]");
                            filtered = items;
                        }
                        else
                        {
                            fetchTask.Description = $"[cyan]Topic filter: {filtered.Count} items[/]";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine(
                                    $"[grey]Topic filter: {preFilterCount} → {filtered.Count} items (relaxed)[/]");
                        }
                    }
                    else if (filtered.Count < preFilterCount)
                    {
                        fetchTask.Description = $"[cyan]Topic filter: {filtered.Count} items[/]";
                        if (settings.DebugPipeline)
                            AnsiConsole.MarkupLine($"[grey]Topic filter: {preFilterCount} → {filtered.Count} items[/]");
                    }

                    items = filtered;
                }

                // Temporal filtering: use sentinel LLM extraction (not regex patterns!)
                var sentinelIntent = interpreted?.SentinelIntent;
                var needsRecencyFilter = sentinelIntent?.RequiresFresh == true
                                         || sentinelIntent?.TimeSensitivity is "today" or "breaking" or "week"
                                         || sentinelIntent?.DateRange != null;

                // For roundup intent, also penalize topic drift
                if (earlyQueryType == QueryType.Roundup)
                    foreach (var item in items)
                        if (QueryTypeDetector.IsTopicDrift(item))
                            item.RelevanceScore *= 0.3; // Heavy penalty

                // Date-gate using sentinel's temporal extraction
                if (needsRecencyFilter)
                {
                    // Get max age from sentinel intent (LLM-driven, not hardcoded)
                    var maxAge = QueryTypeDetector.GetMaxAge(sentinelIntent, interpreted?.RawPrompt ?? settings.Prompt);

                    foreach (var item in items)
                    {
                        var mult = QueryTypeDetector.GetFreshnessMultiplier(item, maxAge);
                        item.RelevanceScore *= mult;
                    }

                    // Re-sort by relevance after freshness adjustment
                    items = items.OrderByDescending(i => i.RelevanceScore).ToList();

                    var freshCount = items.Count(i => DateTimeOffset.UtcNow - i.CreatedAt <= maxAge);
                    var ageDesc = maxAge.TotalHours <= 48 ? $"{maxAge.TotalHours}h" : $"{maxAge.TotalDays}d";
                    fetchTask.Description = $"[cyan]Date-gate ({ageDesc}): {freshCount}/{items.Count} fresh[/]";
                    if (settings.DebugPipeline)
                    {
                        var reason = sentinelIntent?.RequiresFresh == true
                            ? "requires_fresh"
                            : sentinelIntent?.TimeSensitivity ?? "date_range";
                        AnsiConsole.MarkupLine(
                            $"[grey]Temporal filter ({reason}): {freshCount}/{items.Count} items within {ageDesc}[/]");
                    }
                }

                // Show raw content if requested
                if (settings.ShowRaw && !settings.Json)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[bold yellow]Raw Fetched Content:[/]");
                    foreach (var item in items.Take(settings.Limit))
                    {
                        AnsiConsole.MarkupLine($"[cyan]---[/] {Markup.Escape(item.Title)}");
                        if (!string.IsNullOrEmpty(item.Url))
                            AnsiConsole.MarkupLine($"[grey]URL:[/] {Markup.Escape(item.Url)}");
                        if (!string.IsNullOrEmpty(item.Content))
                        {
                            var content = item.Content.Length > 1000 ? item.Content[..1000] + "..." : item.Content;
                            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(content)}[/]");
                        }

                        AnsiConsole.WriteLine();
                    }
                }

                // Load recent stored items to combine with fresh content (skip with --force)
                var storedItems = settings.Force ? [] : await boot.Storage.GetRecentItemsAsync(1);
                var storedContentItems = storedItems
                    .Where(s => !string.IsNullOrEmpty(s.Summary)) // Only include analyzed items
                    .Select(s => s.ToContentItem() with { Score = 0 }) // Lower priority than fresh items
                    .ToList();

                // Combine fresh items first (higher priority), then stored items
                items.AddRange(storedContentItems);

                // Inject NER-matched cached items (entity-specific, high relevance)
                if (nerContext?.HasCachedData == true)
                {
                    var existingUrls = new HashSet<string>(
                        items.Where(i => !string.IsNullOrEmpty(i.Url)).Select(i => i.Url!),
                        StringComparer.OrdinalIgnoreCase);

                    var nerCachedItems = nerContext.CachedItems
                        .Where(s => !string.IsNullOrEmpty(s.Summary))
                        .Select(s => s.ToContentItem())
                        .Where(c => !existingUrls.Contains(c.Url ?? ""))
                        .ToList();

                    if (nerCachedItems.Count > 0)
                    {
                        items.AddRange(nerCachedItems);
                        fetchTask.Description = $"[cyan]NER cache: +{nerCachedItems.Count} items[/]";
                        if (settings.DebugPipeline)
                            AnsiConsole.MarkupLine(
                                $"[grey]NER cache: injected {nerCachedItems.Count} entity-matched items[/]");
                    }
                }
            } // end normal fetch mode

            // Stage 2: Deduplicate by URL (not embedding - topic queries have similar content)
            // Also web-validates URLs found in storage — bridges source corpora with crawl KBs.
            var dedupeTask = ctx.AddTask("[cyan]Deduplicating[/]", maxValue: items.Count);
            uniqueItems.Clear(); // Use outer scope variable
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var webValidatedCount = 0;

            foreach (var item in items)
            {
                // Dedupe by URL (same article from different sources)
                // and by title (catches exact duplicates without URL)
                var normalizedUrl = item.Url?.Split('?')[0].Split('#')[0].TrimEnd('/') ?? "";
                var normalizedTitle = item.Title.ToLowerInvariant().Trim();

                var isDuplicate = (!string.IsNullOrEmpty(normalizedUrl) && seenUrls.Contains(normalizedUrl))
                                  || seenTitles.Contains(normalizedTitle);

                if (!isDuplicate)
                {
                    // Web-validate: if this URL already exists in storage (from any source),
                    // mark it as web-validated so it passes KB exclusion in future queries.
                    if (!string.IsNullOrEmpty(item.Url))
                    {
                        var existing = await boot.Storage.FindByUrlAsync(item.Url);
                        if (existing != null)
                        {
                            await boot.Storage.WebValidateByUrlAsync(item.Url);
                            webValidatedCount++;
                            // Use cached content if the fetched item lacks it
                            if (string.IsNullOrEmpty(item.Content) && !string.IsNullOrEmpty(existing.Content))
                                item.Content = existing.Content;
                            if (item.Embedding == null && existing.Embedding != null)
                                item.Embedding = EmbeddingCompat.FromBytes(existing.Embedding);
                            // Preserve fresh topic/sentiment when available from current analysis
                            if (!string.IsNullOrEmpty(existing.DetectedTopic) && string.IsNullOrEmpty(item.DetectedTopic))
                                item.DetectedTopic = existing.DetectedTopic;
                            if (existing.SentimentScore != 0 && item.SentimentScore == 0)
                                item.SentimentScore = existing.SentimentScore;
                        }
                    }

                    uniqueItems.Add(item);
                    if (!string.IsNullOrEmpty(normalizedUrl))
                        seenUrls.Add(normalizedUrl);
                    seenTitles.Add(normalizedTitle);
                }

                dedupeTask.Increment(1);
            }

            var webValInfo = webValidatedCount > 0 ? $" ({webValidatedCount} web-validated)" : "";
            dedupeTask.Description = $"[green]Found {uniqueItems.Count} unique items{webValInfo}[/]";

            // Stage 2.1: Source domain filtering (allow/block lists)
            if (boot.Config.SourceFilter.AllowedDomains.Count > 0 || boot.Config.SourceFilter.BlockedDomains.Count > 0)
            {
                var preFilterCount = uniqueItems.Count;
                uniqueItems = ApplySourceDomainFilter(uniqueItems, boot.Config.SourceFilter);

                if (uniqueItems.Count < preFilterCount)
                    fetchTask.Description = $"[cyan]Source filter: {uniqueItems.Count} items[/]";
                if (settings.DebugPipeline && uniqueItems.Count < preFilterCount)
                    AnsiConsole.MarkupLine($"[grey]Source filter: {preFilterCount} → {uniqueItems.Count} items[/]");
            }

            // Stage 2.1b: Filter out homepage/section pages and site descriptions
            // These are aggregator homepages (Engadget, TechCrunch, etc.) and items whose
            // content is just "X provides the latest news..." rather than actual articles.
            if (!isLocalMode)
            {
                var preHomepageCount = uniqueItems.Count;
                uniqueItems = uniqueItems.Where(item =>
                {
                    if (DoomSummarizer.Services.OllamaService.IsHomepageTitle(item.Title)) return false;
                    // Filter site descriptions only when there's no actual article content
                    if (DoomSummarizer.Services.OllamaService.IsSiteDescription(item.Summary ?? "")
                        && string.IsNullOrEmpty(item.Content)) return false;
                    // URL-based homepage detection: short paths = homepage/section page
                    if (IsHomepageUrl(item.Url)) return false;
                    return true;
                }).ToList();

                if (uniqueItems.Count < preHomepageCount)
                {
                    fetchTask.Description = $"[cyan]Homepage filter: {uniqueItems.Count} items[/]";
                    if (settings.DebugPipeline)
                        AnsiConsole.MarkupLine(
                            $"[grey]Homepage/site-description filter: {preHomepageCount} → {uniqueItems.Count} items[/]");
                }
            }

            // Stage 2.2: KB enrichment (web queries only) — Lucene + Embeddings
            // Uses sentinel-generated Lucene query + semantic similarity for better recall
            if (!isLocalMode && uniqueItems.Count > 0)
            {
                var enrichQuery = interpreted?.RawPrompt ?? settings.Prompt ?? "";
                if (!string.IsNullOrWhiteSpace(enrichQuery))
                {
                    var candidateIds = new HashSet<string>();
                    var luceneCount = 0;
                    var embedCount = 0;

                    // Layer 1: Lucene search (sentinel-generated query for salience)
                    try
                    {
                        var luceneIndexPath = Path.Combine(boot.Storage.DataPath, "lucene", "enrichment");
                        using var lucene = new LuceneSearchService(luceneIndexPath);
                        lucene.Open();

                        // Ensure KB items are indexed (incremental)
                        var recentItems = await boot.Storage.GetRecentItemsAsync(90);
                        var itemsToIndex = recentItems
                            .Where(s => !lucene.ContainsDocument(s.Id))
                            .Select(s => s.ToContentItem())
                            .ToList();
                        if (itemsToIndex.Count > 0)
                        {
                            lucene.IndexItems(itemsToIndex);
                            lucene.Commit();
                        }

                        // Generate Lucene query from natural language (via sentinel)
                        var luceneQuery =
                            await LuceneQueryGenerator.GenerateQueryAsync(enrichQuery, ollama, cancellationToken);
                        if (settings.DebugPipeline)
                            AnsiConsole.MarkupLine($"[grey]KB Lucene query: {Markup.Escape(luceneQuery)}[/]");

                        var luceneResults = lucene.Search(luceneQuery, limit: 15);
                        foreach (var r in luceneResults) candidateIds.Add(r.Id);
                        luceneCount = luceneResults.Count;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine(
                            $"[grey]KB Lucene search skipped: {FormattingHelpers.Esc(ex.Message)}[/]");
                    }

                    // Layer 2: Embedding search for semantic coverage (catches related content)
                    try
                    {
                        var queryEmbed = await boot.Embedding.EmbedAsync(enrichQuery, cancellationToken);
                        var embeddingResults = await boot.Storage.FindSimilarAsync(queryEmbed, 10, 0.25);
                        foreach (var r in embeddingResults) candidateIds.Add(r.Id);
                        embedCount = embeddingResults.Count;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine(
                            $"[grey]KB embedding search skipped: {FormattingHelpers.Esc(ex.Message)}[/]");
                    }

                    // Layer 3: Entity profile HNSW search (when entity profiles exist)
                    var entityCount = 0;
                    if (boot.EntityStore != null && interpreted?.SentinelIntent?.Entities?.Count >= 2)
                        try
                        {
                            var hasProfiles = await boot.EntityStore!.HasEntityProfilesAsync();
                            if (hasProfiles)
                            {
                                var entityProfileService = new EntityProfileService(boot.Embedding);
                                var entityDocCounts = await boot.EntityStore!.GetEntityDocCountsAsync();
                                var totalDocs = await boot.EntityStore!.GetTotalDocsWithEntitiesAsync();

                                // Infer entity types using heuristics (ORG, PER, LOC, MISC)
                                var queryEntities = interpreted.SentinelIntent.Entities
                                    .Select(e => (name: e, type: EntityProfileService.InferEntityType(e),
                                        confidence: 0.8f))
                                    .ToList();

                                var queryEntityProfile = await entityProfileService.ComputeQueryProfileAsync(
                                    queryEntities, entityDocCounts, totalDocs);

                                if (queryEntityProfile.Length > 0)
                                {
                                    var entityResults = await boot.EntityStore!.FindRelatedByEntityProfileAsync(
                                        queryEntityProfile, 8, 0.25f);
                                    foreach (var (itemId, _, _) in entityResults)
                                        candidateIds.Add(itemId);
                                    entityCount = entityResults.Count;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine(
                                    $"[grey]Entity profile search skipped: {FormattingHelpers.Esc(ex.Message)}[/]");
                        }

                    // Merge into results — with salience gate
                    // Only keep KB items that are genuinely relevant to THIS query
                    if (candidateIds.Count > 0)
                    {
                        var storedItems = await boot.Storage.LoadItemsByIdsAsync(candidateIds.ToList());
                        var existingIds = new HashSet<string>(uniqueItems.Select(i => i.Id));
                        var existingUrls2 = new HashSet<string>(
                            uniqueItems.Where(i => !string.IsNullOrEmpty(i.Url))
                                .Select(i => i.Url!.Split('?')[0].TrimEnd('/').ToLowerInvariant()),
                            StringComparer.OrdinalIgnoreCase);
                        var newFromKb = storedItems.Where(s =>
                                !existingIds.Contains(s.Id) &&
                                (string.IsNullOrEmpty(s.Url) ||
                                 !existingUrls2.Contains(s.Url.Split('?')[0].TrimEnd('/').ToLowerInvariant())))
                            .ToList();

                        // Salience gate: score KB candidates against query, keep only salient items
                        var enrichQueryEmbed = await boot.Embedding.EmbedAsync(enrichQuery, cancellationToken);
                        var preGateCount = newFromKb.Count;
                        newFromKb = newFromKb.Where(item =>
                        {
                            if (item.Embedding == null) return false;
                            var sim = VectorMath.CosineSimilarity(enrichQueryEmbed, item.Embedding);
                            return sim >= 0.30f;
                        }).ToList();

                        if (newFromKb.Count > 0)
                        {
                            uniqueItems.AddRange(newFromKb);
                            fetchTask.Description = $"[cyan]KB enrichment: +{newFromKb.Count} items[/]";
                            var entityInfo = entityCount > 0 ? $", Entity={entityCount}" : "";
                            var gateInfo = preGateCount > newFromKb.Count
                                ? $", Gated={preGateCount - newFromKb.Count} below salience"
                                : "";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine(
                                    $"[grey]KB enrichment: Lucene={luceneCount}, Embed={embedCount}{entityInfo}, Merged={newFromKb.Count}{gateInfo}[/]");
                        }
                        else if (settings.DebugPipeline && preGateCount > 0)
                        {
                            AnsiConsole.MarkupLine(
                                $"[grey]KB enrichment: {preGateCount} candidates all below salience threshold (0.30) — skipped[/]");
                        }
                    }
                }
            }

            // Stage 2.5: Unified scoring pipeline (5-signal RRF with PRF + outlier penalty)
            // All scoring goes through RetrievalPipeline.ScoreItemsAsync — single path for
            // KB queries (zero auth/freshness + Lucene FTS), web queries (query-type-adaptive),
            // and MCP tools. BM25 handled by Lucene at retrieval, not in scorer.
            var queryText = interpreted?.RawPrompt ?? settings.Prompt ?? "";
            float[]? queryEmbedding = null;
            List<float[]>? subqueryEmbeddings = null;

            // Compute query embedding (needed for scoring and post-scoring steps)
            if (!string.IsNullOrWhiteSpace(queryText))
            {
                queryEmbedding = await boot.Embedding.EmbedAsync(queryText, cancellationToken);

                // Composite query: embed each subquery for multi-query evidence checks
                if (interpreted?.SentinelIntent?.HasSubqueries == true)
                {
                    var subqueryTexts = interpreted.SentinelIntent.Subqueries!;
                    var sqEmbeddings = await boot.Embedding.EmbedBatchAsync(subqueryTexts, cancellationToken);
                    subqueryEmbeddings = sqEmbeddings.ToList();
                    if (settings.DebugPipeline)
                        AnsiConsole.MarkupLine($"[grey]Multi-query: {subqueryEmbeddings.Count} subquery embeddings[/]");
                }
            }

            var scoringVibeText = vibe != "neutral" ? GetVibeRepresentativeText(resolvedVibe) : null;

            // Construct pipeline once — reused for scoring and potential re-search
            var scoringPipeline = new RetrievalPipeline(boot.Embedding, boot.Storage, boot.EntityStore);
            ScoringOptions? scoringOpts = null;
            ScoringResult? scoringResult = null;

            if (!string.IsNullOrWhiteSpace(queryText) && queryEmbedding != null)
            {
                var preScoreCount = uniqueItems.Count;

                // Broad queries (news/roundup) use a lower gate to avoid over-filtering
                // diverse results; focused queries (qa/howto/research) use the strict default.
                // Check sentinel intent, earlyQueryType, AND raw heuristic (sentinel may override
                // Roundup→Explainer for "Summarize X" queries that are really roundups).
                var rawQueryType = QueryTypeDetector.Detect(queryText);
                var isBroadIntent = interpreted?.SentinelIntent?.Intent is "news" or "roundup" or "general"
                                    || earlyQueryType is QueryType.Roundup
                                    || rawQueryType is QueryType.Roundup;
                float? gateOverride = isBroadIntent ? 0.15f : null;

                scoringOpts = new ScoringOptions
                {
                    Query = queryText,
                    QueryEmbedding = queryEmbedding,
                    VibeText = scoringVibeText,
                    IsKnowledgeBase = isLocalMode,
                    QueryType = earlyQueryType,
                    UseOutlierPenalty = true, // Outlier penalty still useful to filter genuinely off-topic items
                    UseEmbeddingDedup = false, // Web-mode uses URL/title dedup instead
                    RelaxScoringGates =
                        isLocalMode || isSearchOnlyIntent, // KB + search_only queries need relaxed gates
                    Phase1GateOverride = gateOverride
                };

                scoringResult = await scoringPipeline.ScoreItemsAsync(uniqueItems, scoringOpts, cancellationToken);
                uniqueItems = scoringResult.Items;

                if (uniqueItems.Count < preScoreCount)
                    fetchTask.Description = $"[cyan]Relevance: {uniqueItems.Count} items[/]";
                fetchTask.Description = $"[cyan]RRF ranked: {uniqueItems.Count} items[/]";

                if (settings.DebugPipeline)
                {
                    if (uniqueItems.Count < preScoreCount)
                        AnsiConsole.MarkupLine(
                            $"[grey]Fast relevance filter: {preScoreCount} → {uniqueItems.Count} items[/]");

                    // Post-hoc signal breakdown for debug display
                    var authLookup2 = RelevanceScorer.ComputeAuthorityScores(uniqueItems)
                        .ToDictionary(x => x.item.Id, x => x.score);

                    AnsiConsole.WriteLine();
                    var table = new Table()
                        .Title("[bold yellow]Scoring Pipeline Results (5-signal RRF)[/]")
                        .Border(TableBorder.Rounded)
                        .AddColumn("[cyan]#[/]")
                        .AddColumn("[cyan]Source[/]")
                        .AddColumn("[cyan]Fresh[/]")
                        .AddColumn("[cyan]Auth[/]")
                        .AddColumn("[cyan]QSim[/]")
                        .AddColumn("[cyan]Qual[/]")
                        .AddColumn("[cyan]Vibe[/]")
                        .AddColumn("[cyan]RRF[/]")
                        .AddColumn("[cyan]Title[/]");

                    float[]? debugVibeEmbed = null;
                    if (scoringVibeText != null)
                        debugVibeEmbed = await boot.Embedding.EmbedAsync(scoringVibeText, cancellationToken);

                    // Quality anchors for debug display
                    var debugHighQ =
                        await boot.Embedding.EmbedAsync(RelevanceScorer.HighQualityAnchorText, cancellationToken);
                    var debugLowQ =
                        await boot.Embedding.EmbedAsync(RelevanceScorer.LowQualityAnchorText, cancellationToken);

                    var rank = 1;
                    // Use ORIGINAL query embedding for debug QSim — not the PRF-refined one.
                    // PRF centroid can drift toward off-topic items, showing misleading uniform
                    // similarity values. The original embedding reflects the actual user query.
                    var debugQueryEmbed = queryEmbedding;

                    // Diagnostic: embedding state
                    var withEmbed = uniqueItems.Count(i => i.Embedding != null);
                    var nullEmbed = uniqueItems.Count(i => i.Embedding == null);
                    AnsiConsole.MarkupLine(
                        $"[grey]Embeddings: {withEmbed} set, {nullEmbed} null | queryEmbed: {(debugQueryEmbed != null ? $"{debugQueryEmbed.Length}d" : "NULL")} | subqueries: {subqueryEmbeddings?.Count ?? 0}[/]");
                    if (withEmbed > 0 && debugQueryEmbed != null)
                        // Show actual cosine similarities for first 3 items to verify embedding discrimination
                        foreach (var diagItem in uniqueItems.Take(5))
                            if (diagItem.Embedding != null)
                            {
                                var rawCos = VectorMath.CosineSimilarity(diagItem.Embedding, debugQueryEmbed);
                                var sqInfo = "";
                                if (subqueryEmbeddings?.Count > 0)
                                {
                                    var sqSims = subqueryEmbeddings
                                        .Select(sq => VectorMath.CosineSimilarity(diagItem.Embedding, sq)).ToList();
                                    sqInfo = $", subq=({string.Join(", ", sqSims.Select(s => $"{s:F3}"))})";
                                }

                                var diagMsg =
                                    $"  {diagItem.Source}: \"{diagItem.Title[..Math.Min(40, diagItem.Title.Length)]}\" primary={rawCos:F4}{sqInfo} max={ComputeMaxQuerySimilarity(diagItem.Embedding, debugQueryEmbed, subqueryEmbeddings):F3}";
                                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(diagMsg)}[/]");
                            }
                            else
                            {
                                AnsiConsole.MarkupLine(
                                    $"[grey]  {Markup.Escape(diagItem.Source)}: \"{Markup.Escape(diagItem.Title[..Math.Min(40, diagItem.Title.Length)])}\" NO EMBEDDING[/]");
                            }

                    foreach (var item in uniqueItems.Take(25))
                    {
                        var fresh = RelevanceScorer.ComputeFreshness(item);
                        var auth = authLookup2.GetValueOrDefault(item.Id, 0.3);
                        var qSim = ComputeMaxQuerySimilarity(item.Embedding, debugQueryEmbed, subqueryEmbeddings);
                        var qual = item.Embedding != null
                            ? RelevanceScorer.ComputeQualityScore(item.Embedding, debugHighQ, debugLowQ)
                            : 0.5;
                        var vSim = debugVibeEmbed != null && item.Embedding != null
                            ? VectorMath.CosineSimilarity(item.Embedding, debugVibeEmbed)
                            : 0f;

                        table.AddRow(
                            $"{rank++}",
                            Markup.Escape(item.Source),
                            $"{fresh:F2}",
                            $"{auth:F2}",
                            $"{qSim:F3}",
                            $"{qual:F2}",
                            $"{vSim:F3}",
                            $"[bold]{item.RelevanceScore:F3}[/]",
                            Markup.Escape(item.Title.Length > 50 ? item.Title[..47] + "..." : item.Title));
                    }

                    AnsiConsole.Write(table);

                    var topScore = uniqueItems.FirstOrDefault()?.RelevanceScore ?? 0;
                    var botScore = uniqueItems.LastOrDefault()?.RelevanceScore ?? 0;
                    AnsiConsole.MarkupLine(
                        $"[grey]RRF ranked {uniqueItems.Count} items (top={topScore:F3}, bot={botScore:F3})[/]");
                }
            }

            // Stage 2.5a: Apply source reliability weights (RRF score multipliers)
            if (boot.Config.SourceFilter.Weights.Count > 0)
            {
                var weightedCount = ApplySourceWeights(uniqueItems, boot.Config.SourceFilter);
                if (weightedCount > 0)
                    fetchTask.Description = $"[cyan]Src weights: {weightedCount} adjusted[/]";
                if (settings.DebugPipeline && weightedCount > 0)
                    AnsiConsole.MarkupLine($"[grey]Source weights: {weightedCount} items adjusted[/]");

                // Re-sort after weight adjustment
                var weighted = uniqueItems.OrderByDescending(i => i.RelevanceScore).ToList();
                uniqueItems.Clear();
                uniqueItems.AddRange(weighted);
            }

            // Stage 2.5b: LFU diversity decay — penalize items returned too often
            if (uniqueItems.Count > 0)
            {
                var itemIds = uniqueItems.Select(i => i.Id).ToList();
                var usageStats = await boot.Storage.GetItemUsageAsync(itemIds);
                if (usageStats.Count > 0)
                {
                    var lfuAdjusted = 0;
                    foreach (var item in uniqueItems)
                        if (usageStats.TryGetValue(item.Id, out var usage) && usage.accessCount > 1)
                        {
                            // Mild decay: 1/(1 + 0.1 * log2(accessCount))
                            // 2 accesses → 0.91x, 4 → 0.83x, 8 → 0.77x, 16 → 0.71x
                            var decay = 1.0 / (1.0 + 0.1 * Math.Log2(usage.accessCount));
                            item.RelevanceScore *= decay;
                            lfuAdjusted++;
                        }

                    if (lfuAdjusted > 0)
                    {
                        // Re-sort after LFU decay
                        var lfuSorted = uniqueItems.OrderByDescending(i => i.RelevanceScore).ToList();
                        uniqueItems.Clear();
                        uniqueItems.AddRange(lfuSorted);

                        fetchTask.Description = $"[cyan]LFU: {lfuAdjusted} items decayed[/]";
                        if (settings.DebugPipeline)
                            AnsiConsole.MarkupLine(
                                $"[grey]LFU diversity: {lfuAdjusted} frequently-seen items decayed[/]");
                    }
                }
            }

            // Stage 2.5c: One-hop link following for richer context
            linkCacheHits = 0;
            linksSkippedByRelevance = 0;
            if (boot.Config.LinkFollowing.Enabled && !settings.NoLinks)
            {
                var itemsToFollow = uniqueItems.Take(settings.Limit).ToList();
                var linkTask = ctx.AddTask("[cyan]Following links[/]", maxValue: itemsToFollow.Count);

                var linkService = new LinkFollowingService(
                    httpClient, boot.Config.LinkFollowing, boot.Storage,
                    text => boot.Embedding.EmbedAsync(text).GetAwaiter().GetResult(),
                    queryEmbedding);
                var activityLog = new List<string>();

                await linkService.FollowLinksAsync(
                    itemsToFollow,
                    new Progress<(int current, int total)>(p => linkTask.Value = p.current),
                    activity =>
                    {
                        activityLog.Add(activity);
                        // Show last activity in progress description (strip markup for non-markup-safe display)
                        linkTask.Description = $"[cyan]{FormattingHelpers.SafeStripMarkup(activity)}[/]";
                    });

                var totalLinked = itemsToFollow.Sum(i => i.LinkedPages.Count);
                var enrichedCount = itemsToFollow.Count(i => i.IsEnriched);
                var structuredCount = itemsToFollow.Count(i => i.ContentStructure != null);
                linkTask.Value = itemsToFollow.Count;
                var cacheInfo = linkService.CacheHits > 0 ? $", {linkService.CacheHits} cached" : "";
                var relevanceInfo = linkService.LinksSkippedByRelevance > 0
                    ? $", {linkService.LinksSkippedByRelevance} irrelevant skipped"
                    : "";
                linkTask.Description =
                    $"[green]Enriched {enrichedCount} articles ({structuredCount} with structure), {totalLinked} linked pages{cacheInfo}{relevanceInfo}[/]";

                if (settings.DebugPipeline)
                    AnsiConsole.MarkupLine(
                        $"[grey]Links: {enrichedCount} enriched, {totalLinked} linked, {linkService.CacheHits} cache hits, {linkService.LinksSkippedByRelevance} irrelevant skipped[/]");

                // Re-embed items that were enriched with full article content
                // (original embeddings were computed on short RSS descriptions)
                if (enrichedCount > 0)
                    foreach (var item in itemsToFollow.Where(i => i.IsEnriched))
                    {
                        var textToEmbed = $"{item.Title} {item.Content ?? ""}".Trim();
                        item.Embedding = await boot.Embedding.EmbedAsync(textToEmbed, cancellationToken);
                    }

                // Capture stats for JSON output
                linkCacheHits = linkService.CacheHits;
                linksSkippedByRelevance = linkService.LinksSkippedByRelevance;
            }

            // Stage 2.5d: Second-pass content enrichment for top items still lacking content.
            // Catches items from search APIs (Brave, Google News) that only had short snippets.
            {
                var needsContent = uniqueItems
                    .Take(settings.Limit)
                    .Where(i => string.IsNullOrEmpty(i.Content) && !string.IsNullOrEmpty(i.Url))
                    .ToList();

                if (needsContent.Count > 0)
                {
                    var enrichTask = ctx.AddTask("[cyan]Enriching content[/]", maxValue: needsContent.Count);
                    var enriched = 0;

                    // Fetch in parallel with a concurrency limit
                    using var semaphore = new SemaphoreSlim(4);
                    var tasks = needsContent.Select(async item =>
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        try
                        {
                            var content = await ContentItemHelpers.FetchLinkContentAsync(
                                httpClient, item.Url!, cancellationToken);
                            if (!string.IsNullOrEmpty(content))
                            {
                                item.Content = content;
                                item.IsEnriched = true;
                                Interlocked.Increment(ref enriched);
                                // Re-embed with full content
                                var textToEmbed = $"{item.Title} {content}".Trim();
                                item.Embedding = await boot.Embedding.EmbedAsync(textToEmbed, cancellationToken);
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch
                        {
                            // Non-fatal: item keeps its existing snippet
                        }
                        finally
                        {
                            semaphore.Release();
                            enrichTask.Increment(1);
                        }
                    }).ToList(); // Materialize to start all tasks before awaiting

                    try
                    {
                        await Task.WhenAll(tasks);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    enrichTask.Description = enriched > 0
                        ? $"[green]Enriched {enriched}/{needsContent.Count} items with full content[/]"
                        : "[grey]No additional content found[/]";
                }
            }

            // In-corpus link authority ("silly PageRank"):
            // Articles that are linked by other articles in our corpus get an authority boost.
            var inLinkCounts = ComputeInCorpusLinkAuthority(uniqueItems);
            if (inLinkCounts.Count > 0)
            {
                foreach (var item in uniqueItems)
                {
                    var normalizedUrl = item.Url?.Split('?')[0].TrimEnd('/').ToLowerInvariant() ?? "";
                    if (inLinkCounts.TryGetValue(normalizedUrl, out var linkCount) && linkCount > 0)
                    {
                        // Boost: log scale so 2 in-links = +0.05, 5 = +0.08, 10 = +0.10
                        var boost = Math.Min(0.10, Math.Log2(1 + linkCount) * 0.035);
                        item.RelevanceScore = Math.Min(1.0, item.RelevanceScore + boost);
                    }
                }

                // Re-sort after boost
                var boostedItems = uniqueItems.OrderByDescending(i => i.RelevanceScore).ToList();
                uniqueItems.Clear();
                uniqueItems.AddRange(boostedItems);

                if (inLinkCounts.Values.Any(c => c > 0))
                {
                    var boostedCount = inLinkCounts.Count(kv => kv.Value > 0);
                    fetchTask.Description = $"[cyan]PageRank: {boostedCount} boosted[/]";
                    if (settings.DebugPipeline)
                        AnsiConsole.MarkupLine(
                            $"[grey]In-corpus PageRank: {boostedCount} items boosted by cross-references[/]");
                }
            }

            // Evidence sufficiency check: if top items are irrelevant, re-search with focused queries
            if (queryEmbedding != null && uniqueItems.Count > 0 && !isLocalMode)
            {
                var topItems = uniqueItems.Take(5).Where(i => i.Embedding != null).ToList();
                if (topItems.Count > 0)
                {
                    // Multi-query: use max similarity across subqueries for composite queries
                    var avgRelevance = topItems
                        .Select(i => (double)ComputeMaxQuerySimilarity(i.Embedding, queryEmbedding, subqueryEmbeddings))
                        .Average();

                    if (avgRelevance < 0.15)
                    {
                        if (settings.Full)
                            AnsiConsole.MarkupLine(
                                $"[yellow]Evidence gap detected (top-5 relevance: {avgRelevance:F2}) — running targeted re-search[/]");

                        // Re-search with the raw query through at most 2 search APIs (priority order)
                        var reSearchQuery = queryText;
                        var reSearchResults = new List<ContentItem>();
                        var reSearchTasks = new List<Task<List<ContentItem>>>();
                        var reSearchLimit = Math.Min(5, settings.Limit);
                        const int maxReSearchApis = 2;

                        if (reSearchTasks.Count < maxReSearchApis && boot.ApiKeys!.IsAvailable("brave_search"))
                            reSearchTasks.Add(Task.Run(async () =>
                                await new BraveSearchService(httpClient, boot.ApiKeys!, boot.ApiBudget!, circuitBreaker)
                                    .SearchAsync(reSearchQuery, reSearchLimit)));
                        if (reSearchTasks.Count < maxReSearchApis && boot.ApiKeys!.IsAvailable("serper"))
                            reSearchTasks.Add(Task.Run(async () =>
                                await new SerperSearchService(httpClient, boot.ApiKeys!, boot.ApiBudget!,
                                        circuitBreaker)
                                    .SearchAsync(reSearchQuery, reSearchLimit)));
                        if (reSearchTasks.Count < maxReSearchApis && boot.ApiKeys!.IsAvailable("tavily"))
                            reSearchTasks.Add(Task.Run(async () =>
                                await new TavilySearchService(httpClient, boot.ApiKeys!, boot.ApiBudget!,
                                        circuitBreaker)
                                    .SearchAsync(reSearchQuery, reSearchLimit)));
                        if (reSearchTasks.Count < maxReSearchApis && boot.ApiKeys!.IsAvailable("jina"))
                            reSearchTasks.Add(Task.Run(async () =>
                                await new JinaSearchService(httpClient, boot.ApiKeys!, boot.ApiBudget!, circuitBreaker)
                                    .SearchAsync(reSearchQuery, reSearchLimit)));
                        if (reSearchTasks.Count == 0)
                            reSearchTasks.Add(Task.Run(async () =>
                                await new DuckDuckGoSearch(httpClient, circuitBreaker)
                                    .SearchAsync(reSearchQuery, reSearchLimit)));

                        var reSearchBatches = await Task.WhenAll(reSearchTasks);
                        foreach (var batch in reSearchBatches)
                            reSearchResults.AddRange(batch);

                        if (reSearchResults.Count > 0)
                        {
                            // Embed and deduplicate new results
                            var existingIds = new HashSet<string>(uniqueItems.Select(i => i.Id));
                            var existingUrls = new HashSet<string>(
                                uniqueItems.Where(i => !string.IsNullOrEmpty(i.Url))
                                    .Select(i => i.Url!.Split('?')[0].TrimEnd('/').ToLowerInvariant()),
                                StringComparer.OrdinalIgnoreCase);
                            var existingTitles = new HashSet<string>(
                                uniqueItems.Select(i => i.Title.ToLowerInvariant().Trim()),
                                StringComparer.OrdinalIgnoreCase);
                            var newItems = reSearchResults.Where(i =>
                                    !existingIds.Contains(i.Id) &&
                                    (string.IsNullOrEmpty(i.Url) ||
                                     !existingUrls.Contains(i.Url.Split('?')[0].TrimEnd('/').ToLowerInvariant())) &&
                                    !existingTitles.Contains(i.Title.ToLowerInvariant().Trim()))
                                .ToList();

                            // Compute embeddings for new items
                            var newTexts = newItems
                                .Select(item => $"{item.Title} {item.Content ?? ""}".Trim())
                                .ToList();
                            var newEmbeddings = await boot.Embedding.EmbedBatchAsync(newTexts, cancellationToken);
                            for (var ei = 0; ei < newItems.Count; ei++)
                                newItems[ei].Embedding = newEmbeddings[ei];

                            // Merge and re-score through the unified pipeline
                            uniqueItems.AddRange(newItems);
                            if (scoringOpts != null)
                            {
                                var reScored =
                                    await scoringPipeline.ScoreItemsAsync(uniqueItems, scoringOpts, cancellationToken);
                                uniqueItems = reScored.Items;
                            }

                            fetchTask.Description = $"[cyan]Re-search: +{newItems.Count} items[/]";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine(
                                    $"[grey]Re-search: {newItems.Count} new items merged, {uniqueItems.Count} total[/]");
                        }
                    }
                }
            }

            // Stage 3: Deterministic signal analysis — no LLM
            // Segments, sentiment, topic all computed via ONNX embeddings and article processing.
            // The LLM is reserved for Stage 4 (synthesis) only.
            analyzedItems =
                new List<(string title, string summary, string topic, float sentiment, string url, double relevance)>();

            // Pre-compute anchor embeddings once for sentiment and topic inference
            using var processor = await ItemProcessor.CreateAsync(boot.Embedding, boot.Storage, boot.EntityStore,
                ct: cancellationToken);

            {
                var itemsToAnalyze = uniqueItems.Take(settings.Limit).ToList();

                // Split: items with existing summaries skip re-analysis
                var alreadyAnalyzed = itemsToAnalyze
                    .Where(i => !string.IsNullOrEmpty(i.Summary) && i.Summary != i.Title)
                    .ToList();
                var needsAnalysis = itemsToAnalyze
                    .Where(i => string.IsNullOrEmpty(i.Summary) || i.Summary == i.Title)
                    .ToList();

                if (alreadyAnalyzed.Count > 0)
                    fetchTask.Description = $"[cyan]Cached: {alreadyAnalyzed.Count} items[/]";
                if (alreadyAnalyzed.Count > 0 && settings.DebugPipeline)
                    AnsiConsole.MarkupLine(
                        $"[grey]Using cached analyses for {alreadyAnalyzed.Count} previously processed items[/]");

                foreach (var item in alreadyAnalyzed)
                    analyzedItems.Add((item.Title, item.Summary ?? item.Title, item.DetectedTopic ?? "general",
                        item.SentimentScore, item.Url ?? "", item.RelevanceScore));

                var analyzeTask = ctx.AddTask("[cyan]Analyzing content[/]", maxValue: Math.Max(1, needsAnalysis.Count));
                if (needsAnalysis.Count == 0)
                {
                    analyzeTask.Value = 1;
                    analyzeTask.Description =
                        $"[green]Analyzed {analyzedItems.Count} items ({alreadyAnalyzed.Count} cached)[/]";
                }
                else
                {
                    // Phase 1: Segment extraction via ArticleProcessor (CPU-bound)
                    // ONNX InferenceSession.Run() is thread-safe — parallelize article processing
                    using var articleProcessor = new ArticleProcessor(
                        EmbeddingFactory.BuildOnnxConfig(boot.Config.Embedding));

                    var parallelOpts = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
                    };

                    await Parallel.ForEachAsync(needsAnalysis, parallelOpts, async (item, ct) =>
                    {
                        try
                        {
                            var processed = await articleProcessor.ProcessAsync(item, ct);

                            // Summary from top salience segments (deterministic, no LLM)
                            var topSegments = processed.TopSegments
                                .OrderByDescending(s => s.SalienceScore)
                                .Take(5)
                                .ToList();
                            item.Summary = topSegments.Count > 0
                                ? string.Join(" ", topSegments.Select(s =>
                                    s.Text.Length > 500 ? s.Text[..500] : s.Text))
                                : item.Content?.Length > 500
                                    ? item.Content[..500] + "..."
                                    : item.Content ?? item.Title;

                            // Structural analysis
                            if (item.Content != null)
                                item.ContentStructure = MarkdownContentAnalyzer.Analyze(item.Content);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Segmentation failed: {ex.Message}");
                            var content = item.Content ?? "";
                            item.Summary = content.Length > 500
                                ? content[..500] + "..."
                                : content.Length > 0
                                    ? content
                                    : item.Title;
                        }

                        // Phase 2: Embedding-based sentiment + topic (pure math, thread-safe)
                        processor.ScoreSentimentAndTopic(item);

                        analyzeTask.Increment(1);
                    });

                    // Build analyzedItems after parallel completion (preserves order)
                    foreach (var item in needsAnalysis)
                        analyzedItems.Add((item.Title, item.Summary ?? item.Title, item.DetectedTopic ?? "general",
                            item.SentimentScore, item.Url ?? "", item.RelevanceScore));
                }

                // Save to storage + index into FTS5 for keyword pre-filtering
                // Batch all writes in a single SQLite transaction for performance
                await processor.IndexBatchAsync(itemsToAnalyze);

                analyzeTask.Description = $"[green]Analyzed {analyzedItems.Count} items (deterministic)[/]";
            }

            // Debug: Show enriched signals
            if (settings.DebugPipeline && analyzedItems.Count > 0)
            {
                AnsiConsole.WriteLine();
                var table = new Table()
                    .Title("[bold yellow]Signal Enrichment: Sentiment + Topic + Relevance[/]")
                    .Border(TableBorder.Rounded)
                    .AddColumn("[cyan]#[/]")
                    .AddColumn("[cyan]Source[/]")
                    .AddColumn("[cyan]Topic[/]")
                    .AddColumn("[cyan]Sent[/]")
                    .AddColumn("[cyan]RRF[/]")
                    .AddColumn("[cyan]Title[/]")
                    .AddColumn("[cyan]Snippet[/]");

                var rank = 1;
                foreach (var item in analyzedItems.OrderByDescending(i => i.relevance).Take(20))
                {
                    var sentColor = item.sentiment > 0.1f ? "green" : item.sentiment < -0.1f ? "red" : "grey";
                    var snippet = item.summary.Length > 60
                        ? item.summary[..57] + "..."
                        : item.summary;
                    // Remove newlines from snippet
                    snippet = snippet.Replace("\n", " ").Replace("\r", "");

                    table.AddRow(
                        $"{rank++}",
                        Markup.Escape(GetSourceFromUrl(item.url)),
                        $"[bold]{Markup.Escape(item.topic)}[/]",
                        $"[{sentColor}]{item.sentiment:F2}[/]",
                        $"{item.relevance:F3}",
                        Markup.Escape(item.title.Length > 40 ? item.title[..37] + "..." : item.title),
                        Markup.Escape(snippet));
                }

                AnsiConsole.Write(table);

                // Show structural analysis for enriched items
                var enrichedWithStructure = uniqueItems
                    .Where(i => i.IsEnriched && i.ContentStructure != null)
                    .OrderByDescending(i => i.RelevanceScore)
                    .Take(10)
                    .ToList();

                if (enrichedWithStructure.Count > 0)
                {
                    AnsiConsole.WriteLine();
                    var structTable = new Table()
                        .Title("[bold yellow]Structural Analysis (Enriched Articles)[/]")
                        .Border(TableBorder.Rounded)
                        .AddColumn("[cyan]Source[/]")
                        .AddColumn("[cyan]Type[/]")
                        .AddColumn("[cyan]Quality[/]")
                        .AddColumn("[cyan]Structure[/]")
                        .AddColumn("[cyan]Title[/]");

                    foreach (var item in enrichedWithStructure)
                    {
                        var s = item.ContentStructure!;
                        var qColor = s.QualityScore > 0.5 ? "green" : s.QualityScore > 0.25 ? "yellow" : "red";
                        structTable.AddRow(
                            Markup.Escape(GetSourceFromUrl(item.Url ?? "")),
                            $"[bold]{Markup.Escape(s.ContentType)}[/]",
                            $"[{qColor}]{s.QualityScore:F2}[/]",
                            Markup.Escape(s.ToSummary()),
                            Markup.Escape(item.Title.Length > 50 ? item.Title[..47] + "..." : item.Title));
                    }

                    AnsiConsole.Write(structTable);
                }
            }

            // NER entity extraction (--entities or --graph)
            allEntities = new List<NerEntity>();
            articleEntityMap = new List<(ContentItem item, List<NerEntity> entities)>();
            // Auto-enable entity extraction when GraphScope is Global or Connective (GraphRAG scope detection)
            extractEntities = !settings.NoEntities
                              || interpreted?.GraphScope is GraphScope.Global or GraphScope.Connective;

            if (extractEntities)
            {
                var nerTask = ctx.AddTask("[cyan]Extracting entities[/]", maxValue: analyzedItems.Count);
                using var nerService = new NerService();

                if (nerService.IsAvailable)
                {
                    await nerService.InitializeAsync();
                    foreach (var item in analyzedItems)
                    {
                        var textForNer = $"{item.title} {item.summary}";
                        var entities = await nerService.ExtractEntitiesAsync(textForNer);
                        allEntities.AddRange(entities);

                        // Track per-article entities for knowledge graph
                        var matchingContentItem = uniqueItems.FirstOrDefault(u =>
                            string.Equals(u.Title, item.title, StringComparison.Ordinal));
                        if (matchingContentItem != null && entities.Count > 0)
                            articleEntityMap.Add((matchingContentItem, entities));

                        nerTask.Increment(1);
                    }

                    // Dedupe entities for display
                    allEntities = allEntities
                        .GroupBy(e => e.Text.ToLowerInvariant())
                        .Select(g => g.MaxBy(e => e.Confidence)!)
                        .OrderByDescending(e => e.Confidence)
                        .ToList();
                }

                nerTask.Description = $"[green]Found {allEntities.Count} entities[/]";

                // Persist entities to SQLite for future runs (enriches theme briefing without --entities)
                if (articleEntityMap.Count > 0)
                    foreach (var (ci, ents) in articleEntityMap)
                        await processor.PersistEntitiesAsync(ci, ents);
            }

            // Layer 3: Graph enrichment — discover related documents via entity similarity
            // Uses entity profile HNSW when available (semantic entity matching),
            // falls back to SQL entity count when entity profiles don't exist yet.
            // Enabled when: (a) --entities flag is set, OR (b) entity profiles exist in KB
            var hasEntityProfiles = boot.EntityStore != null && await boot.EntityStore.HasEntityProfilesAsync();
            if ((extractEntities || hasEntityProfiles) && uniqueItems.Count >= 3)
            {
                var topItemIds = uniqueItems
                    .OrderByDescending(i => i.RelevanceScore)
                    .Take(5)
                    .Select(i => i.Id)
                    .ToList();

                var existingIds = new HashSet<string>(uniqueItems.Select(i => i.Id));
                List<string> relatedIds;
                var enrichmentMethod = "entities";

                // Prefer entity profile HNSW when available (O(log N) semantic matching)
                if (hasEntityProfiles && boot.VectorStore != null && boot.EntityStore != null)
                {
                    var entityProfileService = new EntityProfileService(boot.Embedding, boot.EntityStore!);
                    var graphService =
                        new KnowledgeGraphService(boot.VectorStore, boot.EntityStore, entityProfileService);
                    var related = await graphService.FindRelatedByEntityProfileAsync(
                        topItemIds, 3);
                    relatedIds = related
                        .Where(r => !existingIds.Contains(r.itemId))
                        .Select(r => r.itemId)
                        .ToList();
                    enrichmentMethod = "entity profile HNSW";

                    if (settings.DebugPipeline && related.Count > 0)
                    {
                        AnsiConsole.MarkupLine($"[grey]Entity profile HNSW: found {related.Count} candidates[/]");
                        foreach (var (itemId, title, sim) in related.Take(5))
                        {
                            var truncTitle = title.Length > 40 ? title[..37] + "..." : title;
                            AnsiConsole.MarkupLine($"[grey]  ⤷ {Markup.Escape(truncTitle)}: {sim:F3}[/]");
                        }
                    }
                }
                else
                {
                    // Fallback: SQL-based shared entity count (legacy O(N²) approach)
                    relatedIds = await boot.Storage.FindRelatedByEntitiesAsync(
                        topItemIds, existingIds.ToList());
                }

                if (relatedIds.Count > 0)
                {
                    var relatedItems = await boot.Storage.LoadItemsByIdsAsync(relatedIds);
                    // Assign relevance scaled by position in the entity-related list
                    // First items are more relevant (more shared entities), later ones less so
                    var lowestScore = uniqueItems.Count > 0
                        ? uniqueItems.Min(i => i.RelevanceScore)
                        : 0.1;
                    var addedCount = 0;
                    foreach (var item in relatedItems)
                        if (!existingIds.Contains(item.Id))
                        {
                            var enriched = item with { Source = item.Source + " (via entities)" };
                            // Scale: first entity-related item gets 0.95x lowest, last gets 0.7x
                            var positionFactor = relatedItems.Count > 1
                                ? 0.95 - 0.25 * ((double)addedCount / (relatedItems.Count - 1))
                                : 0.85;
                            enriched.RelevanceScore = lowestScore * positionFactor;
                            uniqueItems.Add(enriched);
                            existingIds.Add(item.Id);
                            addedCount++;
                        }

                    if (relatedItems.Count > 0)
                        fetchTask.Description = $"[cyan]Graph: +{relatedItems.Count} related[/]";
                    if (settings.DebugPipeline && relatedItems.Count > 0)
                        AnsiConsole.MarkupLine(
                            $"[grey]Graph enrichment ({enrichmentMethod}): +{relatedItems.Count} items[/]");
                }
            }

            // Index item embeddings into DuckDB for HNSW similarity search (skip in --no-llm fast mode)
            if (settings.Graph && boot.VectorStore != null && boot.EntityStore != null)
            {
                var indexTask = ctx.AddTask("[cyan]Indexing embeddings[/]", maxValue: 100);
                var graphService = new KnowledgeGraphService(boot.VectorStore, boot.EntityStore);
                var itemsWithEmbeddings = uniqueItems
                    .Where(i => i.Embedding != null)
                    .Take(settings.Limit)
                    .ToList();
                await graphService.IndexItemEmbeddingsAsync(itemsWithEmbeddings);
                indexTask.Value = 100;
                indexTask.Description = $"[green]Indexed {itemsWithEmbeddings.Count} embeddings[/]";
            }

            // Ingest entities into knowledge graph (with entity profiles for HNSW search)
            if (settings.Graph && boot.VectorStore != null && boot.EntityStore != null && articleEntityMap.Count > 0)
            {
                var graphTask = ctx.AddTask("[cyan]Building knowledge graph[/]", maxValue: 100);
                var entityProfileService = new EntityProfileService(boot.Embedding, boot.EntityStore!);
                var graphService = new KnowledgeGraphService(boot.VectorStore, boot.EntityStore, entityProfileService);
                await graphService.IngestEntitiesAsync(articleEntityMap);

                // Ingest linked page entities with lower confidence
                foreach (var (item, _) in articleEntityMap)
                    if (item.LinkedPages.Count > 0)
                    {
                        using var linkedNer = new NerService();
                        if (linkedNer.IsAvailable)
                        {
                            await linkedNer.InitializeAsync();
                            foreach (var linked in item.LinkedPages)
                            {
                                var linkedEntities = await linkedNer.ExtractEntitiesAsync(
                                    $"{linked.Title} {linked.Content}");
                                if (linkedEntities.Count > 0)
                                    await graphService.IngestLinkedPageEntitiesAsync(
                                        item, linkedEntities, linked.Url);
                            }
                        }
                    }

                graphTask.Value = 100;
                var (ec, rc, mc, ic) = await boot.EntityStore!.GetStatsAsync();
                graphTask.Description = $"[green]Graph: {ec} entities, {rc} relationships, {ic} items[/]";
            }

            // Stage 4: Generate summary
            var summaryTask = ctx.AddTask("[cyan]Generating summary[/]", maxValue: 100);
            template = settings.Template.ToLowerInvariant();

            // Auto-select template for file collections based on document type.
            // Only override if user hasn't explicitly chosen a template.
            // Uses config output.default_template (default: "default" = concise summary).
            if (template == "default" && ingestedDocType is not IngestDocumentType.Unknown)
                template = boot.Config.Output.DefaultTemplate.ToLowerInvariant();

            isBlogTemplate = template is "blog-article" or "blog-timeline"
                or "blog-newsletter" or "blog-newsletter-html";

            // finalSummary and templateData already declared outside lambda
            templateData = null;

            if (ollamaAvailable && analyzedItems.Count > 0)
            {
                summaryTask.Value = 10;
                var userQuery = interpreted?.RawPrompt ?? settings.Prompt;

                // For file collections with detected document type, enhance the query
                // with narrative/structural guidance for better LLM synthesis
                if (ingestedDocType != IngestDocumentType.Unknown
                    && string.IsNullOrWhiteSpace(userQuery))
                    userQuery = ingestedDocType switch
                    {
                        IngestDocumentType.Fiction =>
                            "Analyze this work of fiction: identify the main characters and their roles, the setting, plot summary, key themes, and significant events",
                        IngestDocumentType.NonFiction =>
                            "Summarize this book: identify the main arguments, key concepts, evidence, and conclusions",
                        IngestDocumentType.Academic =>
                            "Summarize this paper: research questions, methodology, key findings, and conclusions",
                        IngestDocumentType.Technical =>
                            "Summarize the key concepts, architecture, and important features",
                        _ => userQuery
                    };

                // Composite query handling: enhance userQuery to explicitly address each subquery
                // This ensures the summarizer answers each part of the composite question
                if (interpreted?.SentinelIntent?.HasSubqueries == true)
                {
                    var subqs = interpreted.SentinelIntent.Subqueries!;
                    var subqList = string.Join("\n", subqs.Select((sq, i) => $"  {i + 1}. {sq}"));
                    userQuery = $"""
                                 {userQuery}

                                 IMPORTANT: This is a composite question. Please answer EACH of these sub-questions:
                                 {subqList}

                                 Structure your response to clearly address each question.
                                 """;
                }

                // Detect query type for source quality weighting and template auto-selection.
                // Use sentinel intent when available — the LLM is better at distinguishing
                // QA from roundup (e.g., "What's the SNL host this week?" is QA, not roundup).
                var detectedQueryType = QueryTypeDetector.Detect(userQuery, interpreted?.SentinelIntent);

                // Apply source quality multipliers based on query type
                if (detectedQueryType is QueryType.Timeline or QueryType.Explainer
                    or QueryType.Roundup)
                {
                    var qualityAdjusted = 0;
                    foreach (var item in uniqueItems)
                    {
                        var multiplier = QueryTypeDetector.GetSourceQualityMultiplier(
                            detectedQueryType, item.Url);
                        if (Math.Abs(multiplier - 1.0) > 0.01)
                        {
                            item.RelevanceScore *= multiplier;
                            qualityAdjusted++;
                        }
                    }

                    if (qualityAdjusted > 0)
                    {
                        var sorted = uniqueItems.OrderByDescending(i => i.RelevanceScore).ToList();
                        uniqueItems.Clear();
                        uniqueItems.AddRange(sorted);
                        // Also re-sort analyzedItems to match
                        analyzedItems = analyzedItems
                            .OrderByDescending(a => a.relevance)
                            .ToList();

                        summaryTask.Description = $"[cyan]Quality: {qualityAdjusted} adjusted[/]";
                        if (settings.DebugPipeline)
                            AnsiConsole.MarkupLine(
                                $"[grey]Source quality ({detectedQueryType}): {qualityAdjusted} items adjusted[/]");
                    }
                }

                summaryTask.Value = 20;

                // Resolve YAML template definition (if any)
                // For file collections with detected document type, look up the matching
                // embedded YAML template definition to guide the LLM synthesis
                var docTypeTemplateName = ingestedDocType switch
                {
                    IngestDocumentType.Fiction => "book-report-fiction",
                    IngestDocumentType.NonFiction => "book-report-nonfiction",
                    IngestDocumentType.Academic => "paper-summary",
                    IngestDocumentType.Technical => "technical-overview",
                    _ => null
                };
                var templateDef = outputTemplates.GetDefinition(template)
                                  ?? (docTypeTemplateName != null
                                      ? outputTemplates.GetDefinition(docTypeTemplateName)
                                      : null);
                var effectiveBase = templateDef?.BaseTemplate ?? template;
                var isBlogArticle = effectiveBase is "blog-article" or "blog-timeline"
                                    || template is "blog-article" or "blog-timeline";

                // Route to appropriate synthesis based on template
                if (isBlogArticle)
                {
                    // Force timeline for blog-timeline, otherwise auto-detect
                    var articleQueryType = effectiveBase == "blog-timeline" || template == "blog-timeline"
                        ? QueryType.Timeline
                        : detectedQueryType;

                    BlogArticleResult blogResult;
                    using (var articleProcessor = new ArticleProcessor(
                               EmbeddingFactory.BuildOnnxConfig(boot.Config.Embedding)))
                    {
                        var generator = new LongFormDocumentGenerator(
                            ollama, articleProcessor);
                        blogResult = await generator.GenerateAsync(
                            analyzedItems, uniqueItems,
                            userQuery ?? "topic overview",
                            vibe, vibePrompt, articleQueryType,
                            templateDef, cancellationToken,
                            settings.Parallel);
                    }

                    // Build template data
                    templateData = new DigestData
                    {
                        Date = DateTimeOffset.Now,
                        Vibe = vibe,
                        Query = userQuery,
                        ArticleTitle = blogResult.Title,
                        Introduction = blogResult.Introduction,
                        Sections = blogResult.Sections
                            .Select(s => new DigestSection(s.Heading, s.Content, s.SourceUrls))
                            .ToList(),
                        Conclusion = blogResult.Conclusion,
                        SourceUrls = blogResult.SourceUrls,
                        Items = analyzedItems.Select(a => new DigestItem
                        {
                            Title = a.title,
                            Url = a.url,
                            Summary = a.summary,
                            Topic = a.topic,
                            Sentiment = a.sentiment
                        }).ToList()
                    };

                    // Use YAML template's own Liquid template if registered, else base template
                    var renderTemplate = templateDef?.Template != null ? template
                        : effectiveBase == "blog-timeline" ? "blog-timeline"
                        : "blog-article";
                    finalSummary = outputTemplates.Render(templateData, renderTemplate);
                }
                else if (template is "blog-newsletter" or "blog-newsletter-html")
                {
                    var newsletterResult = await ollama.SynthesizeNewsletterAsync(
                        analyzedItems, vibe, vibePrompt,
                        userQuery,
                        uniqueItems, text => boot.Embedding.EmbedAsync(text).GetAwaiter().GetResult(),
                        cancellationToken);

                    templateData = new DigestData
                    {
                        Date = DateTimeOffset.Now,
                        Vibe = vibe,
                        Query = userQuery,
                        Introduction = newsletterResult.Introduction,
                        TopPicks = newsletterResult.TopPicks
                            .Select(p => new DigestPick(p.Title, p.Url, p.Commentary, p.Source))
                            .ToList(),
                        QuickHits = newsletterResult.QuickHits
                            .Select(q => new DigestQuickHit(q.Title, q.Url, q.OneLiner))
                            .ToList(),
                        SignOff = newsletterResult.SignOff,
                        Items = analyzedItems.Select(a => new DigestItem
                        {
                            Title = a.title,
                            Url = a.url,
                            Summary = a.summary,
                            Topic = a.topic,
                            Sentiment = a.sentiment
                        }).ToList()
                    };

                    finalSummary = outputTemplates.Render(templateData,
                        template == "blog-newsletter-html" ? "blog-newsletter-html" : "blog-newsletter");
                }
                else
                {
                    // Standard synthesis path
                    summaryTask.Value = 50;

                    // Entity disambiguation: detect ambiguous entities in top items
                    // Only apply for research/qa queries (entity lookups), not news/roundups
                    var sentinelIntent = interpreted?.SentinelIntent?.Intent ?? "";
                    var isEntityQuery = sentinelIntent is "research" or "qa";
                    if (isEntityQuery && !string.IsNullOrWhiteSpace(userQuery)
                                      && detectedQueryType != QueryType.Roundup)
                    {
                        var topForDisambig = uniqueItems
                            .OrderByDescending(i => i.RelevanceScore)
                            .Take(settings.Limit)
                            .ToList();

                        var disambiguator = new EntityDisambiguationService();
                        var disambiguation = await disambiguator.DisambiguateFastAsync(
                            topForDisambig, userQuery, boot.Embedding, boot.Storage);

                        // Filter out clusters irrelevant to the query — prevents e.g.
                        // "Artificial Intelligence" clusters appearing in "strawberry prices" queries
                        if (disambiguation.IsAmbiguous && disambiguation.Clusters.Count >= 2 && queryEmbedding != null)
                        {
                            var relevantClusters = disambiguation.Clusters
                                .Where(c =>
                                {
                                    if (c.Items.Count == 0) return false;
                                    var topItem = c.Items.MaxBy(i => i.RelevanceScore)!;
                                    if (topItem.Embedding == null) return true; // can't filter without embedding
                                    // Multi-query: use max similarity across subqueries for composite queries
                                    var sim = ComputeMaxQuerySimilarity(topItem.Embedding, queryEmbedding,
                                        subqueryEmbeddings);
                                    return sim >= 0.35f; // minimum topical relevance to query
                                })
                                .ToList();

                            if (relevantClusters.Count >= 2)
                            {
                                var entityLines = relevantClusters
                                    .Select(c => $"- Entity: \"{c.Label}\"")
                                    .ToList();

                                userQuery = $"""
                                             IMPORTANT: Evidence contains distinct entities with similar names.
                                             Summarize EACH entity separately under its own heading:
                                             {string.Join("\n", entityLines)}
                                             Do NOT conflate these into one entity.

                                             ORIGINAL QUERY: {userQuery}
                                             """;
                            }
                        }
                    }

                    // Determine if we should use streaming output (console, non-blog, non-JSON)
                    var canStream = !settings.Json && string.IsNullOrEmpty(settings.Output) && !isBlogTemplate;

                    if (canStream)
                        // Build prompt only — streaming happens after progress block
                        streamingPrompt = await ollama.SynthesizeSummaryAsync(
                            analyzedItems, vibe, vibePrompt, userQuery, uniqueItems,
                            text => boot.Embedding.EmbedAsync(text).GetAwaiter().GetResult(),
                            texts => boot.Embedding.EmbedBatchAsync(texts).GetAwaiter().GetResult(),
                            sentinelIntent: interpreted?.SentinelIntent,
                            missingTerms: missingTerms,
                            returnPromptOnly: true);
                    else
                        finalSummary = await ollama.SynthesizeSummaryAsync(
                            analyzedItems, vibe, vibePrompt, userQuery, uniqueItems,
                            text => boot.Embedding.EmbedAsync(text).GetAwaiter().GetResult(),
                            texts => boot.Embedding.EmbedBatchAsync(texts).GetAwaiter().GetResult(),
                            sentinelIntent: interpreted?.SentinelIntent,
                            missingTerms: missingTerms);
                }
            }
            else
            {
                finalSummary = GenerateFallbackSummary(analyzedItems, vibe, ingestedDocType, uniqueItems);
            }

            summaryTask.Value = 100;
            summaryTask.Description = streamingPrompt != null
                ? "[cyan]Ready to stream[/]"
                : isBlogTemplate
                    ? $"[green]{Markup.Escape(template)} generated[/]"
                    : "[green]Summary generated[/]";

            // Save summary (skip for streaming — saved after streaming completes)
            if (streamingPrompt == null)
                await boot.Storage.SaveSummaryAsync(vibe, finalSummary, analyzedItems.Count);

            // Log query for segment reuse (LFU tracking + similar query matching)
            if (!string.IsNullOrWhiteSpace(queryText))
            {
                var returnedIds = uniqueItems.Take(settings.Limit).Select(i => i.Id).ToList();
                var logEmbedding = earlyQueryEmbedding ?? (queryText.Length > 0
                    ? await boot.Embedding.EmbedAsync(queryText, cancellationToken)
                    : null);
                await boot.Storage.LogQueryAsync(queryText, logEmbedding, vibe, returnedIds);
            }

            // Cleanup old data (before Progress ends)
            await boot.Storage.CleanupOldDataAsync(boot.Config.Storage.RetentionDays);
            if (boot.VectorStore != null)
                await boot.VectorStore.CleanupAsync(boot.Config.Storage.RetentionDays);
            if (boot.EntityStore != null)
                await boot.EntityStore.CleanupAsync(boot.Config.Storage.RetentionDays);
        });

        // === Rendering: each step independently safe so streaming always proceeds ===
        if (streamingPrompt != null)
        {
            // Streaming synthesis path: decorative panels first, then LLM tokens
            var maxContentWidth = Math.Min(AnsiConsole.Profile.Width - 6, 94);

            // Evidence briefing — non-fatal (decorative)
            if ((settings.Full || settings.Briefing) && analyzedItems.Count > 0)
                try
                {
                    await RenderEvidenceBriefingAsync(boot, uniqueItems, analyzedItems, articleEntityMap,
                        maxContentWidth);
                }
                catch (Exception ex) when (ex.Message.Contains("color or style") || ex.Message.Contains("markup"))
                {
                    AnsiConsole.MarkupLine($"[dim](evidence briefing skipped: {FormattingHelpers.Esc(ex.Message)})[/]");
                }

            // Sources panel — non-fatal (decorative, uses SafeWrite internally)
            try
            {
                RenderSourcesUsed(analyzedItems, uniqueItems, maxContentWidth);
            }
            catch (Exception ex) when (ex.Message.Contains("color or style") || ex.Message.Contains("markup"))
            {
                AnsiConsole.MarkupLine($"[dim](sources panel skipped: {FormattingHelpers.Esc(ex.Message)})[/]");
            }

            // Stream LLM synthesis tokens — this is the main output
            var title = $"Doom Scroll Digest ({vibe})";
            var synthesisSystemPrompt = ollama.BuildSynthesisSystemPrompt(vibe, vibePrompt);
            var tokens =
                ollama.SynthesizeSummaryStreamingAsync(streamingPrompt, synthesisSystemPrompt, cancellationToken);
            finalSummary = await RenderStreamingOutputAsync(tokens, title);

            // Save the streamed summary
            await boot.Storage.SaveSummaryAsync(vibe, finalSummary, analyzedItems.Count);
        }
        else
        {
            // Non-streaming output (delegated to Display partial)
            try
            {
                await RenderOutputAsync(settings, boot, vibe, finalSummary, template, isBlogTemplate,
                    templateData, uniqueItems, analyzedItems, allEntities, articleEntityMap,
                    extractEntities, ollamaAvailable, interpreted, linkCacheHits, linksSkippedByRelevance,
                    outputTemplates, httpClient, isImageSource, cancellationToken);
            }
            catch (Exception ex) when (ex.Message.Contains("color or style") || ex.Message.Contains("markup"))
            {
                AnsiConsole.MarkupLine($"[yellow]Rendering warning: {FormattingHelpers.Esc(ex.Message)}[/]");
                if (!string.IsNullOrEmpty(finalSummary))
                    Console.WriteLine(finalSummary);
            }
        }

        return 0;
    }

    public sealed class Settings : ContentProcessingSettings
    {
        [CommandArgument(0, "[prompt]")]
        [Description("Natural language prompt (e.g., 'summarize bbc and hacker news') or URL")]
        public string? Prompt { get; init; }

        [CommandOption("-s|--source")]
        [Description("Sources: hn, reddit, search:query, URL, or local file/directory path")]
        public string[]? Sources { get; init; }

        [CommandOption("-l|--limit")]
        [Description("Maximum items to fetch")]
        [DefaultValue(30)]
        public int Limit { get; init; } = 30;

        [CommandOption("--json")]
        [Description("Output as JSON (for LLM tool consumption)")]
        public bool Json { get; init; }

        [CommandOption("--graph")]
        [Description("Enable knowledge graph build and display")]
        public bool Graph { get; init; }

        [CommandOption("--no-links")]
        [Description("Skip one-hop link following")]
        public bool NoLinks { get; init; }

        [CommandOption("--images")]
        [Description("Display inline images for important items")]
        public bool ShowImages { get; init; }

        [CommandOption("--local")]
        [Description("Query ONLY the local knowledge base — no fetching, uses previously stored articles")]
        public bool LocalOnly { get; init; }

        [CommandOption("--debug-pipeline|--debug")]
        [Description("Show detailed pipeline diagnostics: RRF component scores, discards, salience breakdown")]
        public bool DebugPipeline { get; init; }

        [CommandOption("--list-templates")]
        [Description("List available output templates")]
        public bool ListTemplates { get; init; }

        [CommandOption("--email")]
        [Description("Send digest via email (configure with email section in config)")]
        public bool SendEmail { get; init; }

        [CommandOption("--email-to")]
        [Description("Override email recipient(s), comma-separated")]
        public string? EmailTo { get; init; }

        [CommandOption("--full")]
        [Description("Show full diagnostic output: startup panel, status lines, NER, decomposer, evidence briefing")]
        public bool Full { get; init; }

        [CommandOption("--briefing")]
        [Description("Show evidence briefing panel with themes, entities, and coverage metrics")]
        public bool Briefing { get; init; }

        [CommandOption("--clear-storage")]
        [Description("Delete all cached data (segments, queries, entities) and exit")]
        public bool ClearStorage { get; init; }

        [CommandOption("--backfill-entity-profiles")]
        [Description("Backfill entity profiles for existing KB items (one-time migration) and exit")]
        public bool BackfillEntityProfiles { get; init; }

        [CommandOption("--model")]
        [Description("Override LLM model for generation (e.g., qwen3:8b, llama3.2:8b)")]
        public string? Model { get; init; }

        [CommandOption("--sentinel-model")]
        [Description("Override sentinel LLM model for planning/analysis (default: smaller/faster model)")]
        public string? SentinelModel { get; init; }

        [CommandOption("--parallel")]
        [Description(
            "Enable parallel section generation for long-form articles (faster, less cross-section coherence)")]
        [DefaultValue(true)]
        public bool Parallel { get; init; } = true;

        [CommandOption("--locale")]
        [Description("Locale for date/number parsing (e.g., en-us, en-gb, de-de, fr-fr)")]
        [DefaultValue("en-us")]
        public string Locale { get; init; } = "en-us";

        [CommandOption("--ee|--easter-egg")]
        [Description("Show the DoomSummarizer animation")]
        public bool EasterEgg { get; init; }
    }
}