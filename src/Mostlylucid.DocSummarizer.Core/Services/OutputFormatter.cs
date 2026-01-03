using System.Text;
using System.Text.Json;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Formats output in various formats
/// </summary>
public static class OutputFormatter
{
    /// <summary>
    ///     Format a document summary
    /// </summary>
    public static string Format(DocumentSummary summary, OutputConfig config, string fileName,
        SummaryTemplate? template = null)
    {
        // Apply template settings to config if provided
        if (template != null) config = ApplyTemplateToConfig(config, template);

        return config.Format switch
        {
            OutputFormat.Console => FormatConsole(summary, config),
            OutputFormat.Text => FormatText(summary, config, fileName),
            OutputFormat.Markdown => FormatMarkdown(summary, config, fileName),
            OutputFormat.Json => FormatJson(summary),
            _ => FormatConsole(summary, config)
        };
    }

    /// <summary>
    ///     Apply template settings to output config
    /// </summary>
    private static OutputConfig ApplyTemplateToConfig(OutputConfig config, SummaryTemplate template)
    {
        return new OutputConfig
        {
            Format = config.Format,
            OutputDirectory = config.OutputDirectory,
            Verbose = config.Verbose,
            IncludeTopics = template.IncludeTopics,
            IncludeOpenQuestions = template.IncludeQuestions,
            IncludeTrace = template.IncludeTrace,
            IncludeChunkIndex = config.IncludeChunkIndex // Preserve from original config
        };
    }

    /// <summary>
    ///     Format batch summary
    /// </summary>
    public static string FormatBatch(BatchSummary batch, OutputConfig config)
    {
        return config.Format switch
        {
            OutputFormat.Console => FormatBatchConsole(batch, config),
            OutputFormat.Text => FormatBatchText(batch, config),
            OutputFormat.Markdown => FormatBatchMarkdown(batch, config),
            OutputFormat.Json => FormatBatchJson(batch),
            _ => FormatBatchConsole(batch, config)
        };
    }

    private static string FormatConsole(DocumentSummary summary, OutputConfig config)
    {
        var sb = new StringBuilder();

        sb.AppendLine("═══════════════════════════════════════════════════════════════");
        sb.AppendLine(summary.ExecutiveSummary);
        sb.AppendLine("═══════════════════════════════════════════════════════════════");

        // Extracted entities section - show for fiction/bookreport style summaries
        if (summary.Entities != null && summary.Entities.HasAny)
        {
            sb.AppendLine();
            sb.AppendLine("### Extracted Entities");
            sb.AppendLine();
            
            if (summary.Entities.Characters.Count > 0)
            {
                sb.AppendLine($"**Characters**: {string.Join(", ", summary.Entities.Characters.Take(15))}");
                if (summary.Entities.Characters.Count > 15)
                    sb.AppendLine($"  ...and {summary.Entities.Characters.Count - 15} more");
            }
            
            if (summary.Entities.Locations.Count > 0)
                sb.AppendLine($"**Locations**: {string.Join(", ", summary.Entities.Locations.Take(10))}");
            
            if (summary.Entities.Events.Count > 0)
                sb.AppendLine($"**Key Events**: {string.Join(", ", summary.Entities.Events.Take(8))}");
            
            if (summary.Entities.Organizations.Count > 0)
                sb.AppendLine($"**Organizations**: {string.Join(", ", summary.Entities.Organizations.Take(8))}");
            
            if (summary.Entities.Dates.Count > 0)
                sb.AppendLine($"**Dates**: {string.Join(", ", summary.Entities.Dates.Take(8))}");
        }

        if (config.IncludeTopics && summary.TopicSummaries.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Topic Summaries");
            sb.AppendLine();
            foreach (var topic in summary.TopicSummaries)
            {
                sb.AppendLine($"**{topic.Topic}** [{string.Join(", ", topic.SourceChunks)}]");
                sb.AppendLine(topic.Summary);
                sb.AppendLine();
            }
        }

        if (config.IncludeOpenQuestions && summary.OpenQuestions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Open Questions");
            sb.AppendLine();
            foreach (var q in summary.OpenQuestions) sb.AppendLine($"- {q}");
        }

        if (config.IncludeTrace)
        {
            sb.AppendLine();
            sb.AppendLine("### Trace");
            sb.AppendLine();
            sb.AppendLine($"- Document: {summary.Trace.DocumentId}");
            sb.AppendLine($"- Chunks: {summary.Trace.TotalChunks} total, {summary.Trace.ChunksProcessed} processed");
            sb.AppendLine($"- Topics: {summary.Trace.Topics.Count}");
            sb.AppendLine($"- Time: {summary.Trace.TotalTime.TotalSeconds:F1}s");
            sb.AppendLine($"- Coverage: {summary.Trace.CoverageScore:P0}");
            sb.AppendLine($"- Citation rate: {summary.Trace.CitationRate:F2}");
        }

        if (config.IncludeChunkIndex && summary.Trace.ChunkIndex is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("### Document Structure");
            sb.AppendLine();
            foreach (var chunk in summary.Trace.ChunkIndex)
            {
                var indent = new string(' ', (chunk.HeadingLevel - 1) * 2);
                sb.AppendLine($"{indent}[{chunk.Id}] {chunk.Heading} (~{chunk.TokenEstimate} tokens)");
                sb.AppendLine($"{indent}  {chunk.Preview}");
            }
        }

        return sb.ToString();
    }

    private static string FormatText(DocumentSummary summary, OutputConfig config, string fileName)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"DOCUMENT SUMMARY: {fileName}");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("EXECUTIVE SUMMARY");
        sb.AppendLine(new string('-', 80));
        sb.AppendLine(summary.ExecutiveSummary);
        sb.AppendLine();

        if (config.IncludeTopics && summary.TopicSummaries.Count > 0)
        {
            sb.AppendLine("TOPIC SUMMARIES");
            sb.AppendLine(new string('-', 80));
            foreach (var topic in summary.TopicSummaries)
            {
                sb.AppendLine($"{topic.Topic} [{string.Join(", ", topic.SourceChunks)}]");
                sb.AppendLine(topic.Summary);
                sb.AppendLine();
            }
        }

        if (config.IncludeOpenQuestions && summary.OpenQuestions.Count > 0)
        {
            sb.AppendLine("OPEN QUESTIONS");
            sb.AppendLine(new string('-', 80));
            foreach (var q in summary.OpenQuestions) sb.AppendLine($"- {q}");
            sb.AppendLine();
        }

        if (config.IncludeTrace)
        {
            sb.AppendLine("PROCESSING TRACE");
            sb.AppendLine(new string('-', 80));
            sb.AppendLine($"Document: {summary.Trace.DocumentId}");
            sb.AppendLine($"Chunks: {summary.Trace.TotalChunks} total, {summary.Trace.ChunksProcessed} processed");
            sb.AppendLine($"Topics: {summary.Trace.Topics.Count}");
            sb.AppendLine($"Time: {summary.Trace.TotalTime.TotalSeconds:F1}s");
            sb.AppendLine($"Coverage: {summary.Trace.CoverageScore:P0}");
            sb.AppendLine($"Citation rate: {summary.Trace.CitationRate:F2}");
        }

        if (config.IncludeChunkIndex && summary.Trace.ChunkIndex is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("DOCUMENT STRUCTURE");
            sb.AppendLine(new string('-', 80));
            foreach (var chunk in summary.Trace.ChunkIndex)
            {
                var indent = new string(' ', (chunk.HeadingLevel - 1) * 2);
                sb.AppendLine($"{indent}[{chunk.Id}] {chunk.Heading} (~{chunk.TokenEstimate} tokens)");
                sb.AppendLine($"{indent}  {chunk.Preview}");
            }
        }

        return sb.ToString();
    }

    private static string FormatMarkdown(DocumentSummary summary, OutputConfig config, string fileName)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# Document Summary: {fileName}");
        sb.AppendLine();
        sb.AppendLine($"*Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}*");
        sb.AppendLine();
        sb.AppendLine("## Executive Summary");
        sb.AppendLine();
        sb.AppendLine(summary.ExecutiveSummary);
        sb.AppendLine();
        
        // Extracted entities section
        if (summary.Entities != null && summary.Entities.HasAny)
        {
            sb.AppendLine("## Extracted Entities");
            sb.AppendLine();
            
            if (summary.Entities.Characters.Count > 0)
            {
                sb.AppendLine("### Characters/People");
                sb.AppendLine();
                foreach (var character in summary.Entities.Characters.Take(30))
                    sb.AppendLine($"- {character}");
                if (summary.Entities.Characters.Count > 30)
                    sb.AppendLine($"- *...and {summary.Entities.Characters.Count - 30} more*");
                sb.AppendLine();
            }
            
            if (summary.Entities.Locations.Count > 0)
            {
                sb.AppendLine("### Locations");
                sb.AppendLine();
                foreach (var location in summary.Entities.Locations.Take(20))
                    sb.AppendLine($"- {location}");
                if (summary.Entities.Locations.Count > 20)
                    sb.AppendLine($"- *...and {summary.Entities.Locations.Count - 20} more*");
                sb.AppendLine();
            }
            
            if (summary.Entities.Dates.Count > 0)
            {
                sb.AppendLine("### Dates/Time Periods");
                sb.AppendLine();
                foreach (var date in summary.Entities.Dates.Take(15))
                    sb.AppendLine($"- {date}");
                if (summary.Entities.Dates.Count > 15)
                    sb.AppendLine($"- *...and {summary.Entities.Dates.Count - 15} more*");
                sb.AppendLine();
            }
            
            if (summary.Entities.Events.Count > 0)
            {
                sb.AppendLine("### Key Events");
                sb.AppendLine();
                foreach (var evt in summary.Entities.Events.Take(20))
                    sb.AppendLine($"- {evt}");
                if (summary.Entities.Events.Count > 20)
                    sb.AppendLine($"- *...and {summary.Entities.Events.Count - 20} more*");
                sb.AppendLine();
            }
            
            if (summary.Entities.Organizations.Count > 0)
            {
                sb.AppendLine("### Organizations/Groups");
                sb.AppendLine();
                foreach (var org in summary.Entities.Organizations.Take(15))
                    sb.AppendLine($"- {org}");
                if (summary.Entities.Organizations.Count > 15)
                    sb.AppendLine($"- *...and {summary.Entities.Organizations.Count - 15} more*");
                sb.AppendLine();
            }
        }

        if (config.IncludeTopics && summary.TopicSummaries.Count > 0)
        {
            sb.AppendLine("## Topic Summaries");
            sb.AppendLine();
            foreach (var topic in summary.TopicSummaries)
            {
                sb.AppendLine($"### {topic.Topic}");
                sb.AppendLine();
                sb.AppendLine($"*Sources: {string.Join(", ", topic.SourceChunks)}*");
                sb.AppendLine();
                sb.AppendLine(topic.Summary);
                sb.AppendLine();
            }
        }

        if (config.IncludeOpenQuestions && summary.OpenQuestions.Count > 0)
        {
            sb.AppendLine("## Open Questions");
            sb.AppendLine();
            foreach (var q in summary.OpenQuestions) sb.AppendLine($"- {q}");
            sb.AppendLine();
        }

        if (config.IncludeTrace)
        {
            sb.AppendLine("## Processing Trace");
            sb.AppendLine();
            sb.AppendLine("| Metric | Value |");
            sb.AppendLine("|--------|-------|");
            sb.AppendLine($"| Document | {summary.Trace.DocumentId} |");
            sb.AppendLine($"| Chunks | {summary.Trace.TotalChunks} total, {summary.Trace.ChunksProcessed} processed |");
            sb.AppendLine($"| Topics | {summary.Trace.Topics.Count} |");
            sb.AppendLine($"| Time | {summary.Trace.TotalTime.TotalSeconds:F1}s |");
            sb.AppendLine($"| Coverage | {summary.Trace.CoverageScore:P0} |");
            sb.AppendLine($"| Citation rate | {summary.Trace.CitationRate:F2} |");
        }

        if (config.IncludeChunkIndex && summary.Trace.ChunkIndex is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Document Structure");
            sb.AppendLine();
            sb.AppendLine("| Chunk | Heading | Preview | ~Tokens |");
            sb.AppendLine("|-------|---------|---------|---------|");
            foreach (var chunk in summary.Trace.ChunkIndex)
            {
                // Escape pipe characters in heading and preview for markdown table
                var safeHeading = chunk.Heading.Replace("|", "\\|");
                var safePreview = chunk.Preview.Replace("|", "\\|");
                sb.AppendLine($"| {chunk.Id} | {safeHeading} | {safePreview} | {chunk.TokenEstimate} |");
            }
        }

        return sb.ToString();
    }

    private static string FormatJson(DocumentSummary summary)
    {
        return JsonSerializer.Serialize(summary, DocSummarizerJsonContext.Default.DocumentSummary);
    }

    private static string FormatBatchConsole(BatchSummary batch, OutputConfig config)
    {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("═══════════════════════════════════════════════════════════════");
        sb.AppendLine("BATCH PROCESSING COMPLETE");
        sb.AppendLine("═══════════════════════════════════════════════════════════════");
        sb.AppendLine($"Total files: {batch.TotalFiles}");
        sb.AppendLine($"Success: {batch.SuccessCount} ({batch.SuccessRate:P0})");
        sb.AppendLine($"Failed: {batch.FailureCount}");
        sb.AppendLine($"Total time: {batch.TotalTime.TotalSeconds:F1}s");
        sb.AppendLine(
            $"Average time: {(batch.SuccessCount > 0 ? batch.TotalTime.TotalSeconds / batch.SuccessCount : 0):F1}s/file");
        sb.AppendLine();

        if (batch.FailureCount > 0 && config.Verbose)
        {
            sb.AppendLine("FAILED FILES:");
            foreach (var result in batch.Results.Where(r => !r.Success))
                sb.AppendLine($"- {Path.GetFileName(result.FilePath)}: {result.Error}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatBatchText(BatchSummary batch, OutputConfig config)
    {
        var sb = new StringBuilder();

        sb.AppendLine("BATCH PROCESSING SUMMARY");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 80));
        sb.AppendLine($"Total files: {batch.TotalFiles}");
        sb.AppendLine($"Success: {batch.SuccessCount} ({batch.SuccessRate:P0})");
        sb.AppendLine($"Failed: {batch.FailureCount}");
        sb.AppendLine($"Total time: {batch.TotalTime.TotalSeconds:F1}s");
        sb.AppendLine(
            $"Average time: {(batch.SuccessCount > 0 ? batch.TotalTime.TotalSeconds / batch.SuccessCount : 0):F1}s/file");
        sb.AppendLine();

        sb.AppendLine("RESULTS:");
        foreach (var result in batch.Results)
        {
            var status = result.Success ? "OK" : "FAILED";
            sb.AppendLine($"[{status}] {Path.GetFileName(result.FilePath)} ({result.ProcessingTime.TotalSeconds:F1}s)");
            if (!result.Success && result.Error != null) sb.AppendLine($"  Error: {result.Error}");
        }

        return sb.ToString();
    }

    private static string FormatBatchMarkdown(BatchSummary batch, OutputConfig config)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Batch Processing Summary");
        sb.AppendLine();
        sb.AppendLine($"*Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}*");
        sb.AppendLine();
        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|--------|-------|");
        sb.AppendLine($"| Total files | {batch.TotalFiles} |");
        sb.AppendLine($"| Success | {batch.SuccessCount} ({batch.SuccessRate:P0}) |");
        sb.AppendLine($"| Failed | {batch.FailureCount} |");
        sb.AppendLine($"| Total time | {batch.TotalTime.TotalSeconds:F1}s |");
        sb.AppendLine(
            $"| Average time | {(batch.SuccessCount > 0 ? batch.TotalTime.TotalSeconds / batch.SuccessCount : 0):F1}s/file |");
        sb.AppendLine();

        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| File | Status | Time |");
        sb.AppendLine("|------|--------|------|");
        foreach (var result in batch.Results)
        {
            var status = result.Success ? "✓" : "✗";
            sb.AppendLine(
                $"| {Path.GetFileName(result.FilePath)} | {status} | {result.ProcessingTime.TotalSeconds:F1}s |");
        }

        if (batch.FailureCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Errors");
            sb.AppendLine();
            foreach (var result in batch.Results.Where(r => !r.Success))
            {
                sb.AppendLine($"### {Path.GetFileName(result.FilePath)}");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(result.Error);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string FormatBatchJson(BatchSummary batch)
    {
        return JsonSerializer.Serialize(batch, DocSummarizerJsonContext.Default.BatchSummary);
    }

    /// <summary>
    ///     Write output to appropriate destination
    /// </summary>
    public static async Task WriteOutputAsync(string content, OutputConfig config, string fileName,
        string? outputDir = null)
    {
        if (config.Format == OutputFormat.Console)
        {
            Console.WriteLine(content);
            return;
        }

        // Determine output directory
        var directory = outputDir ?? config.OutputDirectory ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

        // Determine file extension
        var extension = config.Format switch
        {
            OutputFormat.Text => ".txt",
            OutputFormat.Markdown => ".md",
            OutputFormat.Json => ".json",
            _ => ".txt"
        };

        // Create output file path - avoid double _summary suffix
        var baseFileName = Path.GetFileNameWithoutExtension(fileName);
        if (baseFileName.EndsWith("_summary", StringComparison.OrdinalIgnoreCase))
        {
            // Already has _summary suffix, don't add another
            baseFileName = baseFileName[..^8]; // Remove existing _summary
        }
        var outputPath = Path.Combine(directory, $"{baseFileName}_summary{extension}");

        // Write file
        await File.WriteAllTextAsync(outputPath, content);

        // Always show output path so user knows where the file was saved
        Console.WriteLine($"Saved: {outputPath}");
    }
}