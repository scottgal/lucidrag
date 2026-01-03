using System.Text;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services.Utilities;


namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Hierarchical summarizer for document collections (anthologies, complete works, etc.)
/// 
/// Strategy (Map-Reduce with sampling):
/// 1. DETECT: Identify collection structure using CollectionDetector
/// 2. PARTITION: Split document into individual works using H1 boundaries
/// 3. SAMPLE: For large collections, sample representative works from each category
/// 4. MAP: Summarize each work independently (parallel)
/// 5. REDUCE: Synthesize work summaries into collection overview
/// 
/// This avoids the "only saw one play" problem by ensuring coverage across all works.
/// </summary>
public class HierarchicalCollectionSummarizer : IAsyncDisposable
{
    private readonly OllamaService _ollama;
    private readonly SegmentExtractor _extractor;
    private readonly bool _verbose;
    private readonly int _maxWorksToSummarize;
    private readonly int _targetWordsPerWork;
    private readonly int _targetWordsFinal;

    /// <summary>
    /// Create a hierarchical collection summarizer.
    /// </summary>
    /// <param name="ollama">Ollama service for LLM calls</param>
    /// <param name="extractor">Segment extractor for work-level summarization</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="maxWorksToSummarize">Maximum works to summarize (sample if more)</param>
    /// <param name="targetWordsPerWork">Target words for each work summary</param>
    /// <param name="targetWordsFinal">Target words for final collection summary</param>
    public HierarchicalCollectionSummarizer(
        OllamaService ollama,
        SegmentExtractor extractor,
        bool verbose = false,
        int maxWorksToSummarize = 15,
        int targetWordsPerWork = 150,
        int targetWordsFinal = 800)
    {
        _ollama = ollama;
        _extractor = extractor;
        _verbose = verbose;
        _maxWorksToSummarize = maxWorksToSummarize;
        _targetWordsPerWork = targetWordsPerWork;
        _targetWordsFinal = targetWordsFinal;
    }

    /// <summary>
    /// Summarize a document collection hierarchically.
    /// </summary>
    /// <param name="markdown">Full markdown content</param>
    /// <param name="collectionInfo">Pre-analyzed collection info (optional)</param>
    /// <param name="focusQuery">Optional focus query to guide summarization</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Hierarchical summary with work-level details</returns>
    public async Task<CollectionSummaryResult> SummarizeAsync(
        string markdown,
        CollectionInfo? collectionInfo = null,
        string? focusQuery = null,
        CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 1. DETECT: Analyze collection structure if not provided
        collectionInfo ??= CollectionDetector.AnalyzeMarkdown(markdown);
        
        if (_verbose)
        {
            VerboseHelper.Log($"[cyan]Collection Analysis:[/] {VerboseHelper.Escape(collectionInfo.ToString())}");
        }

        if (!collectionInfo.IsCollection)
        {
            // Not a collection - fall back to single-document summarization
            return new CollectionSummaryResult
            {
                IsCollection = false,
                CollectionTitle = "Single Document",
                ExecutiveSummary = "This document is not a collection. Use standard summarization.",
                WorkSummaries = new List<WorkSummaryResult>(),
                Strategy = CollectionStrategy.SingleDocument,
                ProcessingTime = stopwatch.Elapsed
            };
        }

        // 2. PARTITION: Split into individual works
        var works = PartitionIntoWorks(markdown, collectionInfo);
        
        if (_verbose)
        {
            VerboseHelper.Log(_verbose, $"[dim]Partitioned into {works.Count} works[/]");
        }

        // 3. SAMPLE: Select representative works if too many
        var selectedWorks = SelectRepresentativeWorks(works, collectionInfo);
        
        if (_verbose && selectedWorks.Count < works.Count)
        {
            VerboseHelper.Log(_verbose, $"[dim]Sampled {selectedWorks.Count} representative works from {works.Count}[/]");
        }

        // 4. MAP: Summarize each work (with progress)
        var workSummaries = await SummarizeWorksAsync(selectedWorks, focusQuery, ct);

        // 5. REDUCE: Synthesize into collection overview
        var collectionSummary = await SynthesizeCollectionSummaryAsync(
            collectionInfo, 
            workSummaries, 
            works.Count,
            focusQuery, 
            ct);

        stopwatch.Stop();

        return new CollectionSummaryResult
        {
            IsCollection = true,
            CollectionTitle = collectionInfo.CollectionTitle ?? "Untitled Collection",
            ExecutiveSummary = collectionSummary,
            WorkSummaries = workSummaries,
            TotalWorksInCollection = works.Count,
            WorksSummarized = selectedWorks.Count,
            Strategy = collectionInfo.GetRecommendedStrategy(),
            ProcessingTime = stopwatch.Elapsed,
            IsShakespeare = collectionInfo.IsShakespeare
        };
    }

    /// <summary>
    /// Partition markdown into individual works based on H1 headings.
    /// </summary>
    private List<WorkPartition> PartitionIntoWorks(string markdown, CollectionInfo collectionInfo)
    {
        var works = new List<WorkPartition>();
        var lines = markdown.Split('\n');
        
        WorkPartition? currentWork = null;
        var contentBuilder = new StringBuilder();
        var workIndex = 0;

        foreach (var line in lines)
        {
            // Check for H1 heading (work boundary)
            if (line.StartsWith("# ") && !line.StartsWith("## "))
            {
                // Save previous work
                if (currentWork != null && contentBuilder.Length > 100)
                {
                    currentWork.Content = contentBuilder.ToString();
                    currentWork.WordCount = CountWords(currentWork.Content);
                    works.Add(currentWork);
                }

                // Start new work
                var title = line.TrimStart('#', ' ').Trim();
                
                // Skip meta sections and chapters
                if (CollectionDetector.QuickIsCollection(title) || IsMeta(title))
                {
                    currentWork = null;
                    contentBuilder.Clear();
                    continue;
                }

                currentWork = new WorkPartition
                {
                    Title = title,
                    Index = workIndex++,
                    WorkInfo = collectionInfo.Works.FirstOrDefault(w => 
                        w.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                };
                contentBuilder.Clear();
                contentBuilder.AppendLine(line);
            }
            else if (currentWork != null)
            {
                contentBuilder.AppendLine(line);
            }
        }

        // Don't forget the last work
        if (currentWork != null && contentBuilder.Length > 100)
        {
            currentWork.Content = contentBuilder.ToString();
            currentWork.WordCount = CountWords(currentWork.Content);
            works.Add(currentWork);
        }

        return works;
    }

    /// <summary>
    /// Select representative works for summarization.
    /// Ensures diversity across categories (for Shakespeare: tragedies, comedies, histories, sonnets).
    /// </summary>
    private List<WorkPartition> SelectRepresentativeWorks(
        List<WorkPartition> allWorks, 
        CollectionInfo collectionInfo)
    {
        if (allWorks.Count <= _maxWorksToSummarize)
            return allWorks;

        var selected = new List<WorkPartition>();

        // Group by inferred type
        var byType = allWorks
            .GroupBy(w => w.WorkInfo?.InferredType ?? WorkType.Unknown)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Calculate quota per type (proportional representation)
        var totalTypes = byType.Count;
        var quotaPerType = Math.Max(2, _maxWorksToSummarize / totalTypes);

        foreach (var (type, works) in byType)
        {
            // Sample from each type: first, last, and middle (representative coverage)
            var toSelect = Math.Min(quotaPerType, works.Count);
            
            if (works.Count <= toSelect)
            {
                selected.AddRange(works);
            }
            else
            {
                // Strategic sampling: first, last, and evenly distributed middle
                selected.Add(works.First()); // First work of this type
                
                if (toSelect > 1)
                    selected.Add(works.Last()); // Last work of this type
                
                if (toSelect > 2)
                {
                    // Sample from middle
                    var middleCount = toSelect - 2;
                    var step = (works.Count - 2) / (middleCount + 1);
                    for (int i = 0; i < middleCount; i++)
                    {
                        var idx = 1 + (i + 1) * step;
                        if (idx < works.Count - 1 && !selected.Contains(works[idx]))
                            selected.Add(works[idx]);
                    }
                }
            }
        }

        // If still under quota, add more works by size (larger = more significant)
        if (selected.Count < _maxWorksToSummarize)
        {
            var remaining = allWorks
                .Except(selected)
                .OrderByDescending(w => w.WordCount)
                .Take(_maxWorksToSummarize - selected.Count);
            selected.AddRange(remaining);
        }

        return selected.OrderBy(w => w.Index).ToList();
    }

    /// <summary>
    /// Summarize each selected work independently (MAP phase).
    /// </summary>
    private async Task<List<WorkSummaryResult>> SummarizeWorksAsync(
        List<WorkPartition> works,
        string? focusQuery,
        CancellationToken ct)
    {
        var results = new List<WorkSummaryResult>();

        if (_verbose)
        {
            VerboseHelper.Log($"Summarizing {works.Count} works...");
        }

        var processed = 0;
        foreach (var work in works)
        {
            ct.ThrowIfCancellationRequested();
            processed++;
            
            if (_verbose)
            {
                VerboseHelper.Log($"[{processed}/{works.Count}] Summarizing: {Truncate(work.Title, 40)}");
            }

            try
            {
                var summary = await SummarizeSingleWorkAsync(work, focusQuery, ct);
                results.Add(summary);
            }
            catch (Exception ex)
            {
                // Log but continue with other works
                if (_verbose)
                {
                    VerboseHelper.Log($"Failed to summarize '{VerboseHelper.Escape(work.Title)}': {VerboseHelper.Escape(ex.Message)}");
                }
                
                results.Add(new WorkSummaryResult
                {
                    Title = work.Title,
                    Summary = $"(Failed to summarize: {ex.Message})",
                    WorkType = work.WorkInfo?.InferredType ?? WorkType.Unknown,
                    WordCount = work.WordCount
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Summarize a single work.
    /// </summary>
    private async Task<WorkSummaryResult> SummarizeSingleWorkAsync(
        WorkPartition work,
        string? focusQuery,
        CancellationToken ct)
    {
        // For short works, use direct summarization
        if (work.WordCount < 2000)
        {
            var shortSummary = await SummarizeShortWorkAsync(work, focusQuery, ct);
            return new WorkSummaryResult
            {
                Title = work.Title,
                Summary = shortSummary,
                WorkType = work.WorkInfo?.InferredType ?? WorkType.Unknown,
                WordCount = work.WordCount
            };
        }

        // For longer works, use extractive summarization first
        var extractedSummary = await SummarizeLongWorkAsync(work, focusQuery, ct);
        
        return new WorkSummaryResult
        {
            Title = work.Title,
            Summary = extractedSummary,
            WorkType = work.WorkInfo?.InferredType ?? WorkType.Unknown,
            WordCount = work.WordCount
        };
    }

    /// <summary>
    /// Summarize a short work directly with LLM.
    /// </summary>
    private async Task<string> SummarizeShortWorkAsync(
        WorkPartition work, 
        string? focusQuery, 
        CancellationToken ct)
    {
        var focusLine = string.IsNullOrEmpty(focusQuery) ? "" : $"\nFOCUS: {focusQuery}\n";
        
        var prompt = $"""
            Summarize this work in approximately {_targetWordsPerWork} words.
            
            TITLE: {work.Title}
            {focusLine}
            REQUIREMENTS:
            - Identify main characters and their relationships
            - Describe the central conflict or theme
            - Mention key plot points or arguments
            - Use specific details from the text (names, places, events)
            - Do NOT use vague academic language ("explores themes of...")
            
            CONTENT:
            {work.Content[..Math.Min(work.Content.Length, 8000)]}
            
            SUMMARY:
            """;

        var response = await _ollama.GenerateAsync(prompt, temperature: 0.3);
        return ResponseCleaner.CleanSynthesisResponse(response);
    }

    /// <summary>
    /// Summarize a long work using extractive + abstractive approach.
    /// </summary>
    private async Task<string> SummarizeLongWorkAsync(
        WorkPartition work,
        string? focusQuery,
        CancellationToken ct)
    {
        // Extract key segments using the segment extractor
        var parser = new MarkdigDocumentParser();
        var parsed = parser.Parse(work.Content);
        
        // Get first, middle, and last sections for coverage
        var sections = parsed.Sections;
        var keyText = new StringBuilder();
        
        // First section (setup)
        if (sections.Count > 0)
        {
            keyText.AppendLine("=== BEGINNING ===");
            keyText.AppendLine(sections[0].GetFullText()[..Math.Min(sections[0].GetFullText().Length, 2000)]);
        }
        
        // Middle section (development)
        if (sections.Count > 2)
        {
            var midIdx = sections.Count / 2;
            keyText.AppendLine("\n=== MIDDLE ===");
            keyText.AppendLine(sections[midIdx].GetFullText()[..Math.Min(sections[midIdx].GetFullText().Length, 2000)]);
        }
        
        // End section (resolution)
        if (sections.Count > 1)
        {
            keyText.AppendLine("\n=== END ===");
            var lastSection = sections[^1];
            keyText.AppendLine(lastSection.GetFullText()[..Math.Min(lastSection.GetFullText().Length, 2000)]);
        }

        var focusLine = string.IsNullOrEmpty(focusQuery) ? "" : $"\nFOCUS: {focusQuery}\n";

        var prompt = $"""
            Summarize this work in approximately {_targetWordsPerWork} words.
            
            TITLE: {work.Title}
            {focusLine}
            REQUIREMENTS:
            - Identify main characters and their relationships
            - Describe the central conflict or theme  
            - Cover beginning, middle, and end
            - Use specific names, places, and events from the text
            - Do NOT use vague language ("explores themes of...", "raises questions about...")
            
            KEY EXCERPTS:
            {keyText}
            
            SUMMARY:
            """;

        var response = await _ollama.GenerateAsync(prompt, temperature: 0.3);
        return ResponseCleaner.CleanSynthesisResponse(response);
    }

    /// <summary>
    /// Synthesize work summaries into a collection overview (REDUCE phase).
    /// </summary>
    private async Task<string> SynthesizeCollectionSummaryAsync(
        CollectionInfo collectionInfo,
        List<WorkSummaryResult> workSummaries,
        int totalWorks,
        string? focusQuery,
        CancellationToken ct)
    {
        // Build work summaries context
        var worksContext = new StringBuilder();
        
        // Group by type for better organization
        var byType = workSummaries
            .GroupBy(w => w.WorkType)
            .OrderByDescending(g => g.Count());

        foreach (var group in byType)
        {
            var typeName = group.Key == WorkType.Unknown ? "Other Works" : $"{group.Key}s";
            worksContext.AppendLine($"\n## {typeName}");
            
            foreach (var work in group)
            {
                worksContext.AppendLine($"\n### {work.Title}");
                worksContext.AppendLine(work.Summary);
            }
        }

        var focusLine = string.IsNullOrEmpty(focusQuery) ? "" : $"\nFOCUS: {focusQuery}\n";
        var coverageNote = workSummaries.Count < totalWorks 
            ? $"\nNote: This summary covers {workSummaries.Count} representative works from {totalWorks} total."
            : "";

        var shakespeareContext = collectionInfo.IsShakespeare
            ? """
              
              This is Shakespeare's works. Include:
              - Major tragedies (Hamlet, Macbeth, King Lear, Othello)
              - Major comedies (A Midsummer Night's Dream, Much Ado About Nothing)
              - Histories (Henry V, Richard III)
              - The Sonnets if present
              Organize by genre and highlight recurring themes across works.
              """
            : "";

        var prompt = $"""
            Create a comprehensive overview of this collection in approximately {_targetWordsFinal} words.
            
            COLLECTION: {collectionInfo.CollectionTitle ?? "Collected Works"}
            TOTAL WORKS: {totalWorks}
            WORKS SUMMARIZED: {workSummaries.Count}
            {focusLine}
            {shakespeareContext}
            
            REQUIREMENTS:
            1. Start with a brief introduction to the collection and its significance
            2. Organize by category/genre if applicable
            3. For each major work, include:
               - Main characters and their relationships
               - Central conflict or theme
               - Key plot points or notable features
            4. Identify common themes across works
            5. Use specific character names, not generic terms
            6. Do NOT use vague academic language
            
            WORK SUMMARIES:
            {worksContext}
            
            {coverageNote}
            
            COLLECTION OVERVIEW:
            """;

        var response = await _ollama.GenerateAsync(prompt, temperature: 0.4);
        var cleaned = ResponseCleaner.CleanSynthesisResponse(response);
        
        // Add coverage footer
        if (workSummaries.Count < totalWorks)
        {
            cleaned += $"\n\n---\n*Coverage: {workSummaries.Count} of {totalWorks} works summarized ({(double)workSummaries.Count / totalWorks:P0})*";
        }

        return cleaned;
    }

    private static bool IsMeta(string title)
    {
        var lower = title.ToLowerInvariant();
        var metaPatterns = new[]
        {
            "contents", "index", "preface", "introduction", "foreword",
            "copyright", "dedication", "acknowledgment", "about",
            "appendix", "notes", "bibliography", "glossary"
        };
        return metaPatterns.Any(p => lower.Contains(p));
    }

    private static int CountWords(string text) =>
        text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";

    public ValueTask DisposeAsync()
    {
        _extractor.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A partition representing a single work within a collection
/// </summary>
public class WorkPartition
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public int Index { get; set; }
    public int WordCount { get; set; }
    public WorkInfo? WorkInfo { get; set; }
}

/// <summary>
/// Summary result for a single work
/// </summary>
public class WorkSummaryResult
{
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public WorkType WorkType { get; set; }
    public int WordCount { get; set; }
    public List<string> Characters { get; set; } = new();
    public List<string> Themes { get; set; } = new();
}

/// <summary>
/// Complete result of hierarchical collection summarization
/// </summary>
public class CollectionSummaryResult
{
    public bool IsCollection { get; set; }
    public string CollectionTitle { get; set; } = "";
    public string ExecutiveSummary { get; set; } = "";
    public List<WorkSummaryResult> WorkSummaries { get; set; } = new();
    public int TotalWorksInCollection { get; set; }
    public int WorksSummarized { get; set; }
    public CollectionStrategy Strategy { get; set; }
    public TimeSpan ProcessingTime { get; set; }
    public bool IsShakespeare { get; set; }

    /// <summary>
    /// Get a formatted markdown output
    /// </summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        
        sb.AppendLine($"# {CollectionTitle}");
        sb.AppendLine();
        sb.AppendLine("## Executive Summary");
        sb.AppendLine();
        sb.AppendLine(ExecutiveSummary);
        sb.AppendLine();
        
        if (WorkSummaries.Count > 0)
        {
            sb.AppendLine("## Individual Works");
            sb.AppendLine();
            
            var byType = WorkSummaries.GroupBy(w => w.WorkType).OrderByDescending(g => g.Count());
            
            foreach (var group in byType)
            {
                var typeName = group.Key == WorkType.Unknown ? "Other Works" : $"{group.Key}s";
                sb.AppendLine($"### {typeName}");
                sb.AppendLine();
                
                foreach (var work in group)
                {
                    sb.AppendLine($"#### {work.Title}");
                    sb.AppendLine();
                    sb.AppendLine(work.Summary);
                    sb.AppendLine();
                }
            }
        }
        
        sb.AppendLine("---");
        sb.AppendLine($"*Generated in {ProcessingTime.TotalSeconds:F1}s using {Strategy} strategy*");
        sb.AppendLine($"*Coverage: {WorksSummarized} of {TotalWorksInCollection} works*");
        
        return sb.ToString();
    }
}
