using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;


namespace Mostlylucid.DocSummarizer.Services;

public class MapReduceSummarizer
{
    /// <summary>
    ///     Default max parallelism for LLM calls. Ollama processes one request at a time per model,
    ///     so high values just queue requests. 8 is a good balance for throughput vs memory.
    /// </summary>
    public const int DefaultMaxParallelism = 8;

    /// <summary>
    ///     Target percentage of context window to use for reduce phase input.
    ///     Leave room for the prompt template and output generation.
    /// </summary>
    private const double ContextWindowTargetPercent = 0.6;

    /// <summary>
    ///     Approximate characters per token for estimation (conservative estimate)
    /// </summary>
    private const double CharsPerToken = 4.0;

    private readonly int _contextWindow;
    private readonly int _maxParallelism;
    private readonly OllamaService _ollama;
    private readonly ProgressService _progress;
    private readonly bool _verbose;

    public MapReduceSummarizer(OllamaService ollama, bool verbose = false, int maxParallelism = DefaultMaxParallelism,
        int contextWindow = 8192, SummaryTemplate? template = null)
    {
        _ollama = ollama;
        _verbose = verbose;
        _progress = new ProgressService(verbose);
        _maxParallelism = maxParallelism > 0 ? maxParallelism : DefaultMaxParallelism;
        _contextWindow = contextWindow;
        Template = template ?? SummaryTemplate.Presets.Default;
    }

    /// <summary>
    ///     Current template being used
    /// </summary>
    public SummaryTemplate Template { get; private set; }

    /// <summary>
    ///     Set the template for summarization
    /// </summary>
    public void SetTemplate(SummaryTemplate template)
    {
        Template = template;
    }

    /// <summary>
    ///     Create a MapReduceSummarizer with auto-detected context window from the model
    /// </summary>
    public static async Task<MapReduceSummarizer> CreateAsync(OllamaService ollama, bool verbose = false,
        int maxParallelism = DefaultMaxParallelism, SummaryTemplate? template = null)
    {
        var contextWindow = await ollama.GetContextWindowAsync();
        return new MapReduceSummarizer(ollama, verbose, maxParallelism, contextWindow, template);
    }

    public async Task<DocumentSummary> SummarizeAsync(string docId, List<DocumentChunk> chunks)
    {
        var sw = Stopwatch.StartNew();

        // Always show basic progress
        var parallelDesc = _maxParallelism <= 0 ? "unlimited" : _maxParallelism.ToString();
        Console.WriteLine($"Map Phase: Summarizing {chunks.Count} chunks ({parallelDesc} parallel)...");
        Console.Out.Flush();

        // Map phase: summarize each chunk in parallel with controlled concurrency
        List<ChunkSummary> chunkSummaries;

        if (_verbose)
        {
            _progress.WriteDivider("Map Phase");
            _progress.Info(
                $"Summarizing {chunks.Count} chunks ({parallelDesc} parallel, timeout: {OllamaService.DefaultTimeout.TotalMinutes:F0} min/chunk)");
        }

        // Use controlled parallelism to avoid resource exhaustion on large documents
        chunkSummaries = await ProcessChunksWithLimitedParallelismAsync(chunks);

        // Reduce phase: merge into final summary
        Console.WriteLine($"Reduce Phase: Merging {chunkSummaries.Count} summaries...");
        Console.Out.Flush();

        if (_verbose)
        {
            _progress.WriteDivider("Reduce Phase");
            _progress.Info($"Merging {chunkSummaries.Count} summaries into final document...");
        }

        var result = await _progress.WithStatusAsync(
            "Generating final summary...",
            async () => await ReduceAsync(chunkSummaries));

        sw.Stop();

        var headings = chunks.Select(c => c.Heading).Where(h => !string.IsNullOrEmpty(h)).ToList();
        var coverage = CalculateCoverage(chunkSummaries, headings);
        var citationRate = CalculateCitationRate(result.ExecutiveSummary);
        
        // Build chunk index for output
        var chunkIndex = chunks.Select(ChunkIndexEntry.FromChunk).ToList();

        // Clear chunk summaries to free memory
        chunkSummaries.Clear();

        Console.WriteLine($"Completed in {sw.Elapsed.TotalSeconds:F1}s");

        if (_verbose) _progress.Success($"Completed in {sw.Elapsed.TotalSeconds:F1}s");

        return result with
        {
            Trace = new SummarizationTrace(
                docId, chunks.Count, chunks.Count,
                headings, sw.Elapsed, coverage, citationRate, chunkIndex)
        };
    }

    private async Task<List<ChunkSummary>> ProcessChunksWithLimitedParallelismAsync(List<DocumentChunk> chunks)
    {
        var results = new ChunkSummary[chunks.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = _maxParallelism };
        var completed = 0;
        var startTime = DateTime.UtcNow;
        var lockObj = new object();

        await Parallel.ForEachAsync(
            chunks.Select((chunk, index) => (chunk, index)),
            options,
            async (item, ct) =>
            {
                results[item.index] = await SummarizeChunkAsync(item.chunk);

                // Thread-safe progress update with visual bar
                int current;
                lock (lockObj)
                {
                    completed++;
                    current = completed;
                }

                var elapsed = DateTime.UtcNow - startTime;
                var avgPerChunk = current > 0 ? elapsed.TotalSeconds / current : 0;
                var remaining = (chunks.Count - current) * avgPerChunk;
                var eta = remaining > 0 ? $" ETA: {remaining:F0}s" : "";
                
                // Create a simple progress bar
                var barWidth = 30;
                var filledWidth = (int)((double)current / chunks.Count * barWidth);
                var bar = new string('█', filledWidth) + new string('░', barWidth - filledWidth);
                var percent = (double)current / chunks.Count * 100;
                
                Console.Write($"\r  [{bar}] {percent,5:F1}% ({current}/{chunks.Count}){eta}    ");
                Console.Out.Flush();
            });

        Console.WriteLine(); // New line after progress
        return results.ToList();
    }

    private async Task<ChunkSummary> SummarizeChunkAsync(DocumentChunk chunk)
    {
        // Truncate content based on context window
        // Use ~40% of context for content to leave room for prompt + response
        // Cap at 16000 chars (~4000 tokens) to keep extraction focused
        // 1 token ≈ 4 chars
        var maxContentTokens = Math.Min((int)(_contextWindow * 0.4), 4000);
        var maxContentLength = Math.Max(maxContentTokens * 4, 4000); // At least 4000 chars
        var content = chunk.Content.Length > maxContentLength
            ? chunk.Content[..maxContentLength] + "..."
            : chunk.Content;

        // Check for boilerplate/license content - skip summarizing it
        if (IsBoilerplate(content))
        {
            return new ChunkSummary(chunk.Id, chunk.Heading, "[SKIP: metadata/license]", chunk.Order);
        }

        // Use extraction-focused prompt instead of paraphrasing
        var prompt = BuildExtractionPrompt(chunk.Heading, content);

        var response = await _ollama.GenerateAsync(prompt);
        
        // Clean up LLM response preamble
        response = CleanExtractedResponse(response);

        return new ChunkSummary(chunk.Id, chunk.Heading, response, chunk.Order);
    }

    /// <summary>
    /// Remove common LLM preamble patterns from extraction responses
    /// </summary>
    private static string CleanExtractedResponse(string response)
    {
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var cleaned = new List<string>();
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            
            // Skip common preamble patterns
            if (trimmed.StartsWith("Here are", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Here is", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("The extracted", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Based on", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("From the text", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("I found", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Let me", StringComparison.OrdinalIgnoreCase))
                continue;
                
            // Skip empty bullets
            if (trimmed == "•" || trimmed == "-" || trimmed == "*")
                continue;
                
            cleaned.Add(line);
        }
        
        return string.Join("\n", cleaned).Trim();
    }

    /// <summary>
    /// Build ultra-tight extraction prompt - minimal tokens, strict grounding
    /// </summary>
    private string BuildExtractionPrompt(string heading, string content)
    {
        // For book reports, extract narrative elements - ONLY from this text
        if (Template.Name.Equals("bookreport", StringComparison.OrdinalIgnoreCase))
        {
            return $"""
                [{heading}]
                {content}
                ---
                3-4 sentences: WHO did WHAT WHERE (from text above only).
                Full names. Past tense. Third person. No lists. No interpretation.
                """;
        }

        // For technical docs
        if (Template.Tone == SummaryTone.Technical)
        {
            return $"""
                [{heading}]
                {content}
                ---
                3 bullets: component → function. Only facts stated above.
                """;
        }

        // Default: fact extraction with grounding
        return $"""
            [{heading}]
            {content}
            ---
            2-3 bullets: facts from text above only. No hedging. No outside knowledge.
            """;
    }

    /// <summary>
    /// Detect boilerplate content that shouldn't be summarized
    /// </summary>
    internal static bool IsBoilerplate(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length < 50)
            return false;
            
        var lower = content.ToLowerInvariant();
        
        // Project Gutenberg patterns (comprehensive)
        if (lower.Contains("project gutenberg") || 
            lower.Contains("gutenberg-tm") ||
            lower.Contains("gutenberg literary archive") ||
            lower.Contains("gutenberg ebook") ||
            lower.Contains("www.gutenberg.org") ||
            lower.Contains("start of the project gutenberg") ||
            lower.Contains("end of the project gutenberg") ||
            lower.Contains("end of this project gutenberg"))
            return true;
            
        // License/legal patterns
        if (lower.Contains("permission is granted") ||
            lower.Contains("public domain") ||
            lower.Contains("terms of use") ||
            lower.Contains("disclaimer of warranties") ||
            lower.Contains("limitation of liability") ||
            lower.Contains("warranties of merchantability"))
            return true;
        
        // Copyright + license together is a strong signal
        if (lower.Contains("copyright") && 
            (lower.Contains("license") || lower.Contains("permission") || lower.Contains("distribute")))
            return true;
            
        // Royalty/donation patterns
        if (lower.Contains("royalty fee") ||
            lower.Contains("donations are accepted") ||
            lower.Contains("tax treatment") ||
            lower.Contains("donate to") ||
            lower.Contains("support project gutenberg"))
            return true;
        
        // Table of contents (large ones are boilerplate)
        if (lower.Contains("table of contents") && content.Split('\n').Length > 10)
            return true;
        
        // Metadata blocks
        if ((lower.Contains("title:") || lower.Contains("author:")) && 
            (lower.Contains("release date") || lower.Contains("language:")))
            return true;
            
        return false;
    }

    private async Task<DocumentSummary> ReduceAsync(List<ChunkSummary> summaries, bool retry = false)
    {
        var ordered = summaries.OrderBy(s => s.Order).ToList();
        var validChunkIds = ordered.Select(s => s.ChunkId).ToHashSet();

        // Check if we need hierarchical reduction
        var estimatedTokens = EstimateTokens(ordered);
        var maxTokens = (int)(_contextWindow * ContextWindowTargetPercent);

        if (estimatedTokens > maxTokens && ordered.Count > 2)
            // Hierarchical reduction needed
            return await HierarchicalReduceAsync(ordered, validChunkIds, retry);

        // Single-pass reduction
        return await SingleReduceAsync(ordered, validChunkIds, retry, true);
    }

    /// <summary>
    ///     Hierarchical reduction: batch summaries, reduce each batch, then reduce the batch results
    /// </summary>
    private async Task<DocumentSummary> HierarchicalReduceAsync(List<ChunkSummary> summaries,
        HashSet<string> validChunkIds, bool retry)
    {
        var maxTokens = (int)(_contextWindow * ContextWindowTargetPercent);
        var batches = CreateBatches(summaries, maxTokens);

        if (_verbose)
        {
            _progress.Info(
                $"Document too large for single reduction ({summaries.Count} summaries, ~{EstimateTokens(summaries):N0} tokens)");
            _progress.Info(
                $"Using hierarchical reduction: {batches.Count} batches → intermediate summaries → final summary");
        }
        else
        {
            Console.WriteLine(
                $"  Hierarchical reduction: {batches.Count} batches (document too large for single pass)");
        }

        // Reduce each batch to an intermediate summary
        var intermediateSummaries = new List<ChunkSummary>();
        var batchNum = 0;

        foreach (var batch in batches)
        {
            batchNum++;
            if (_verbose)
            {
                _progress.Info($"Reducing batch {batchNum}/{batches.Count} ({batch.Count} summaries)...");
            }
            else
            {
                Console.Write($"\r  Reducing batch {batchNum}/{batches.Count}...");
                Console.Out.Flush();
            }

            var batchResult = await SingleReduceAsync(batch, validChunkIds, false, false);

            // Create an intermediate summary that preserves citations from this batch
            var batchChunkIds = batch.Select(s => s.ChunkId).ToList();
            var intermediateHeading = batch.Count == 1
                ? batch[0].Heading
                : $"Sections {batch.First().Order + 1}-{batch.Last().Order + 1}";

            intermediateSummaries.Add(new ChunkSummary(
                $"batch-{batchNum}",
                intermediateHeading,
                batchResult.ExecutiveSummary,
                batch.First().Order
            ));
        }

        if (!_verbose) Console.WriteLine(); // New line after batch progress

        // Check if we need another level of reduction
        var intermediateTokens = EstimateTokens(intermediateSummaries);
        if (intermediateTokens > maxTokens && intermediateSummaries.Count > 2)
        {
            if (_verbose)
                _progress.Info(
                    $"Intermediate summaries still too large (~{intermediateTokens:N0} tokens), adding another reduction level...");
            return await HierarchicalReduceAsync(intermediateSummaries, validChunkIds, retry);
        }

        // Final reduction
        if (_verbose)
            _progress.Info($"Final reduction of {intermediateSummaries.Count} intermediate summaries...");
        else
            Console.WriteLine($"  Final reduction of {intermediateSummaries.Count} intermediate summaries...");

        var result = await SingleReduceAsync(intermediateSummaries, validChunkIds, retry, true);
        
        // Clear intermediate summaries to free memory
        intermediateSummaries.Clear();
        
        return result;
    }

    /// <summary>
    ///     Create batches of summaries that fit within the token limit
    /// </summary>
    private List<List<ChunkSummary>> CreateBatches(List<ChunkSummary> summaries, int maxTokensPerBatch)
    {
        var batches = new List<List<ChunkSummary>>();
        var currentBatch = new List<ChunkSummary>();
        var currentTokens = 0;

        // Reserve some tokens for the prompt template
        var effectiveMax = (int)(maxTokensPerBatch * 0.85);

        foreach (var summary in summaries)
        {
            var summaryTokens = EstimateTokens(summary);

            if (currentBatch.Count > 0 && currentTokens + summaryTokens > effectiveMax)
            {
                // Start a new batch
                batches.Add(currentBatch);
                currentBatch = new List<ChunkSummary>();
                currentTokens = 0;
            }

            currentBatch.Add(summary);
            currentTokens += summaryTokens;
        }

        if (currentBatch.Count > 0) batches.Add(currentBatch);

        // If we ended up with just one batch, force split it
        if (batches.Count == 1 && summaries.Count > 2)
        {
            var midpoint = summaries.Count / 2;
            batches = new List<List<ChunkSummary>>
            {
                summaries.Take(midpoint).ToList(),
                summaries.Skip(midpoint).ToList()
            };
        }

        return batches;
    }

    /// <summary>
    ///     Single-pass reduction of summaries
    /// </summary>
    private async Task<DocumentSummary> SingleReduceAsync(List<ChunkSummary> summaries, HashSet<string> validChunkIds,
        bool retry, bool isFinal)
    {
        var ordered = summaries.OrderBy(s => s.Order).ToList();
        
        // Filter out skipped chunks (boilerplate)
        var validSummaries = ordered.Where(s => !s.Summary.StartsWith("[SKIP:")).ToList();

        // Truncate each summary for small models
        const int maxSummaryLength = 300;
        var sectionsText = string.Join("\n", validSummaries.Select(s =>
        {
            var truncated = s.Summary.Length > maxSummaryLength
                ? s.Summary[..maxSummaryLength] + "..."
                : s.Summary;
            return $"[{s.ChunkId}]: {truncated}";
        }));

        // Use different prompts for intermediate vs final reduction
        string prompt;
        if (isFinal)
        {
            prompt = BuildSynthesisPrompt(sectionsText);
        }
        else
        {
            // Intermediate reduction - deduplicate and compress
            prompt = $"""
                      Extracted facts from document sections:
                      {sectionsText}

                      TASK: Merge these into 3-5 consolidated points. Remove duplicates. Keep only the most important facts.
                      """;
        }

        var response = await _ollama.GenerateAsync(prompt);
        
        // Clean up redundant headers the LLM might add
        if (isFinal)
        {
            response = CleanFinalSummary(response);
        }

        // Build topic summaries only if template includes them
        var topicSummaries = Template.IncludeTopics
            ? validSummaries.Select(s => new TopicSummary(s.Heading, s.Summary, [s.ChunkId])).ToList()
            : new List<TopicSummary>();

        return new DocumentSummary(
            response,
            topicSummaries,
            isFinal && Template.IncludeQuestions ? ExtractOpenQuestions(response) : new List<string>(),
            new SummarizationTrace("", 0, 0, [], TimeSpan.Zero, 0, 0));
    }
    
    /// <summary>
    /// Clean up redundant headers and meta-commentary from final summary
    /// </summary>
    private static string CleanFinalSummary(string response)
    {
        var lines = response.Split('\n').ToList();
        var cleaned = new List<string>();
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            
            // Skip redundant headers the LLM adds
            if (Regex.IsMatch(trimmed, @"^\*{0,2}(Book Report|Executive Summary|Technical Summary|Summary|Abstract)\*{0,2}:?\s*$", RegexOptions.IgnoreCase))
                continue;
                
            // Skip meta-commentary
            if (trimmed.StartsWith("Here is", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Here's", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Below is", StringComparison.OrdinalIgnoreCase))
                continue;
                
            cleaned.Add(line);
        }
        
        return string.Join("\n", cleaned).Trim();
    }

    /// <summary>
    /// Build the final synthesis prompt based on template type
    /// </summary>
    private string BuildSynthesisPrompt(string extractedFacts)
    {
        var templateName = Template.Name.ToLowerInvariant();
        
        // Book report: synthesize into narrative sections
        if (templateName == "bookreport")
        {
            return $"""
                Extracted facts from document:
                {extractedFacts}

                Using ONLY the facts above, write a book report with these sections:

                **Overview**: Title, author (if known), what kind of story this is. 2 sentences.

                **Setting**: Where and when. 1-2 sentences.

                **Characters**: List 3-5 main characters with one-line descriptions. Use their full names as given in the facts.

                **Plot**: What happens—beginning, middle, end. 2 short paragraphs. Stick to events actually listed above.

                **Themes**: 2-3 themes or messages. 1 sentence each.

                RULES:
                - Write in third person
                - Only include information from the extracted facts
                - Do NOT add interpretation or emotional language
                - Keep total length under 400 words
                """;
        }

        // Executive: ultra-brief
        if (templateName == "executive")
        {
            return $"""
                Facts:
                {extractedFacts}

                Write an executive summary:
                1. One paragraph (50 words max) stating the main point
                2. Three bullet points with key takeaways
                3. One recommended action

                Be direct. No filler. Total under 100 words.
                """;
        }
        
        // Brief: 2-3 sentence summary
        if (templateName == "brief")
        {
            return $"""
                Facts:
                {extractedFacts}
                ---
                Write exactly 2 sentences (≤40 words total):
                Sentence 1: [Subject] does/is [specific action/thing].
                Sentence 2: Key detail or outcome.
                
                FORBIDDEN: "This guide", "This document", "provides", "covers", "comprehensive".
                """;
        }
        
        // One-liner: single sentence
        if (templateName is "oneliner" or "one-liner")
        {
            return $"""
                Facts:
                {extractedFacts}

                Write ONE sentence (25 words max) that captures the core message.
                Start with the main subject. Be specific. No hedging.
                """;
        }
        
        // Strict: exactly 3 bullets, hard constraints
        if (templateName == "strict")
        {
            return $"""
                Facts:
                {extractedFacts}

                OUTPUT EXACTLY:
                • [Bullet 1 - most important point, ≤20 words]
                • [Bullet 2 - second insight, ≤20 words]  
                • [Bullet 3 - third insight, ≤20 words]

                RULES:
                - EXACTLY 3 bullets, no more, no less
                - Each bullet is a DISTINCT insight (no overlap)
                - Include [chunk-N] citation after each bullet
                - NO intro text, NO closing text
                - Total ≤60 words
                """;
        }
        
        // Technical: preserve implementation details
        if (templateName is "technical" or "tech")
        {
            return $"""
                Technical facts:
                {extractedFacts}

                Write a technical summary:

                **Purpose**: What this is and what it does. 1-2 sentences.

                **Components**:
                - List key classes, functions, or modules mentioned
                - Include their purpose (one line each)

                **Requirements**: Dependencies, configuration, or prerequisites.

                **Notes**: Limitations, gotchas, or implementation considerations.

                Use precise technical terminology. Be concise. Total under 250 words.
                """;
        }
        
        // Academic: abstract format
        if (templateName == "academic")
        {
            return $"""
                Facts:
                {extractedFacts}

                Write an academic abstract in one paragraph (200-250 words):

                Structure:
                1. Background/Context (1-2 sentences)
                2. Purpose/Objective (1 sentence)  
                3. Methods/Approach (1-2 sentences)
                4. Key Findings (2-3 sentences)
                5. Conclusions/Implications (1-2 sentences)

                Use formal academic language. Be precise and objective.
                """;
        }
        
        // Meeting notes: decisions and action items
        if (templateName is "meeting" or "meetingnotes")
        {
            return $"""
                Facts:
                {extractedFacts}

                Format as meeting notes:

                **Summary**: One paragraph (50 words) of what was discussed.

                **Decisions**:
                - List conclusions or agreements reached

                **Action Items**:
                - List tasks with owners if mentioned

                **Open Questions**:
                - List unresolved issues

                Be concise. Skip sections if no relevant content.
                """;
        }

        // Bullets: just the key points
        if (Template.OutputStyle == OutputStyle.Bullets)
        {
            return $"""
                Facts:
                {extractedFacts}

                Synthesize into 5-7 bullet points. Each bullet should be:
                - A distinct insight (no overlap)
                - Under 20 words
                - Based on facts above, not interpretation

                Start each bullet with a verb.
                """;
        }

        // Default: balanced synthesis
        return $"""
            Extracted facts:
            {extractedFacts}

            Write a summary that:
            1. States the main topic/purpose (1-2 sentences)
            2. Lists key points (3-5 bullets)
            3. Notes any limitations or open questions

            Use only information from the facts above. Be concise. Total under 200 words.
            """;
    }

    /// <summary>
    ///     Estimate token count for a list of summaries
    /// </summary>
    private static int EstimateTokens(List<ChunkSummary> summaries)
    {
        return summaries.Sum(s => EstimateTokens(s));
    }

    /// <summary>
    ///     Estimate token count for a single summary
    /// </summary>
    private static int EstimateTokens(ChunkSummary summary)
    {
        var text = $"## {summary.Heading} [{summary.ChunkId}]\n{summary.Summary}";
        return (int)(text.Length / CharsPerToken);
    }

    private static List<string> ExtractOpenQuestions(string response)
    {
        var lines = response.Split('\n');
        var inQuestions = false;
        var questions = new List<string>();

        foreach (var line in lines)
        {
            if (line.Contains("Open Questions", StringComparison.OrdinalIgnoreCase))
            {
                inQuestions = true;
                continue;
            }

            if (inQuestions && line.TrimStart().StartsWith('-')) questions.Add(line.TrimStart('-', ' '));
            if (inQuestions && line.StartsWith("##")) break;
        }

        return questions;
    }

    private static double CalculateCoverage(List<ChunkSummary> summaries, List<string> headings)
    {
        // Coverage = % of top-level headings that have a non-empty summary
        // For MapReduce, all chunks are processed so coverage is always 1.0
        // unless some chunks produced empty summaries
        if (headings.Count == 0) return 1.0;
        var coveredHeadings = summaries
            .Where(s => !string.IsNullOrWhiteSpace(s.Summary) &&
                        !s.Summary.Contains("Limited coverage", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Heading)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var covered = headings.Count(h => coveredHeadings.Contains(h));
        return (double)covered / headings.Count;
    }

    private static double CalculateCitationRate(string summary)
    {
        var bullets = summary.Split('\n').Count(l => l.TrimStart().StartsWith('-'));
        if (bullets == 0) return 0;
        var citations = Regex.Matches(summary, @"\[chunk-\d+\]").Count;
        return (double)citations / bullets;
    }
}