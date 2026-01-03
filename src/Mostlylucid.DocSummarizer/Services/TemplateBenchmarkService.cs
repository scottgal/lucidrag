using System.Diagnostics;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Spectre.Console;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Service for benchmarking multiple templates on the same document.
/// Reuses expensive extraction and retrieval phases, only varying synthesis per template.
/// </summary>
public class TemplateBenchmarkService : IAsyncDisposable
{
    private readonly OnnxConfig _onnxConfig;
    private readonly OllamaService _ollama;
    private readonly BertRagConfig _bertRagConfig;
    private readonly ExtractionConfig _extractionConfig;
    private readonly RetrievalConfig _retrievalConfig;
    private readonly bool _verbose;
    
    public TemplateBenchmarkService(
        OnnxConfig onnxConfig,
        OllamaService ollama,
        BertRagConfig? bertRagConfig = null,
        ExtractionConfig? extractionConfig = null,
        RetrievalConfig? retrievalConfig = null,
        bool verbose = false)
    {
        _onnxConfig = onnxConfig;
        _ollama = ollama;
        _bertRagConfig = bertRagConfig ?? new BertRagConfig();
        _extractionConfig = extractionConfig ?? new ExtractionConfig();
        _retrievalConfig = retrievalConfig ?? new RetrievalConfig();
        _verbose = verbose;
    }
    
    /// <summary>
    /// Benchmark result for a single template
    /// </summary>
    public record TemplateBenchmarkResult(
        string TemplateName,
        string TemplateDescription,
        int TargetWords,
        DocumentSummary Summary,
        TimeSpan ExtractionTime,
        TimeSpan SynthesisTime,
        int ActualWordCount,
        bool Success,
        string? Error = null);
    
    /// <summary>
    /// Complete benchmark results for all templates
    /// </summary>
    public record BenchmarkResults(
        string DocumentId,
        string DocumentPath,
        int TotalSegments,
        int RetrievedSegments,
        TimeSpan TotalExtractionTime,
        List<TemplateBenchmarkResult> TemplateResults);
    
    /// <summary>
    /// Run benchmark across multiple templates on a single document.
    /// Extraction and retrieval are done once; synthesis is run per template.
    /// </summary>
    public async Task<BenchmarkResults> BenchmarkTemplatesAsync(
        string docId,
        string markdown,
        IEnumerable<string> templateNames,
        string? focusQuery = null,
        ContentType contentType = ContentType.Unknown,
        CancellationToken ct = default)
    {
        var results = new List<TemplateBenchmarkResult>();
        var templates = templateNames.Select(SummaryTemplate.Presets.GetByName).ToList();
        
        if (templates.Count == 0)
        {
            throw new ArgumentException("At least one template must be specified");
        }
        
        // Create the BertRag summarizer with the first template (will switch per iteration)
        using var bertRag = new BertRagSummarizer(
            _onnxConfig,
            _ollama,
            extractionConfig: _extractionConfig,
            retrievalConfig: _retrievalConfig,
            template: templates[0],
            verbose: _verbose,
            vectorStore: null,
            bertRagConfig: _bertRagConfig);
        
        // === Phase 1 & 2: Extract and Retrieve ONCE ===
        var extractionStopwatch = Stopwatch.StartNew();
        
        if (_verbose)
        {
            AnsiConsole.MarkupLine("[cyan]Running extraction and retrieval (shared across all templates)...[/]");
        }
        
        var (extraction, retrieved) = await bertRag.ExtractAndRetrieveAsync(
            docId, markdown, focusQuery, contentType, ct);
        
        extractionStopwatch.Stop();
        var extractionTime = extractionStopwatch.Elapsed;
        
        if (_verbose)
        {
            AnsiConsole.MarkupLine($"[green]Extraction complete:[/] {extraction.AllSegments.Count} segments, {retrieved.Count} retrieved");
            AnsiConsole.MarkupLine($"[dim]Extraction time: {extractionTime.TotalSeconds:F2}s[/]");
            AnsiConsole.WriteLine();
        }
        
        // === Phase 3: Synthesize with each template ===
        foreach (var template in templates)
        {
            if (_verbose)
            {
                AnsiConsole.MarkupLine($"[cyan]Synthesizing with template:[/] [yellow]{template.Name}[/] ({template.Description})");
            }
            
            var synthesisStopwatch = Stopwatch.StartNew();
            
            try
            {
                bertRag.SetTemplate(template);
                var summary = await bertRag.SynthesizeFromRetrievedAsync(
                    docId, extraction, retrieved, focusQuery, ct);
                
                synthesisStopwatch.Stop();
                
                var wordCount = CountWords(summary.ExecutiveSummary);
                
                results.Add(new TemplateBenchmarkResult(
                    template.Name,
                    template.Description,
                    template.TargetWords,
                    summary,
                    extractionTime,
                    synthesisStopwatch.Elapsed,
                    wordCount,
                    Success: true));
                
                if (_verbose)
                {
                    AnsiConsole.MarkupLine($"  [green]Done:[/] {wordCount} words in {synthesisStopwatch.Elapsed.TotalSeconds:F2}s");
                }
            }
            catch (Exception ex)
            {
                synthesisStopwatch.Stop();
                
                results.Add(new TemplateBenchmarkResult(
                    template.Name,
                    template.Description,
                    template.TargetWords,
                    Summary: null!,
                    extractionTime,
                    synthesisStopwatch.Elapsed,
                    ActualWordCount: 0,
                    Success: false,
                    Error: ex.Message));
                
                if (_verbose)
                {
                    AnsiConsole.MarkupLine($"  [red]Failed:[/] {ex.Message}");
                }
            }
        }
        
        return new BenchmarkResults(
            docId,
            docId, // Path not available here
            extraction.AllSegments.Count,
            retrieved.Count,
            extractionTime,
            results);
    }
    
    /// <summary>
    /// Save benchmark results to individual summary files
    /// </summary>
    public async Task SaveResultsAsync(
        BenchmarkResults results,
        string outputDirectory,
        string baseFileName)
    {
        Directory.CreateDirectory(outputDirectory);
        
        foreach (var result in results.TemplateResults.Where(r => r.Success))
        {
            var fileName = $"{baseFileName}_{result.TemplateName}_summary.md";
            var filePath = Path.Combine(outputDirectory, fileName);
            
            var content = FormatSummaryAsMarkdown(result, results);
            await File.WriteAllTextAsync(filePath, content);
            
            if (_verbose)
            {
                AnsiConsole.MarkupLine($"[dim]Saved: {filePath}[/]");
            }
        }
        
        // Save comparison report
        var reportPath = Path.Combine(outputDirectory, $"{baseFileName}_benchmark_report.md");
        var report = FormatBenchmarkReport(results);
        await File.WriteAllTextAsync(reportPath, report);
        
        if (_verbose)
        {
            AnsiConsole.MarkupLine($"[green]Saved benchmark report: {reportPath}[/]");
        }
    }
    
    private static string FormatSummaryAsMarkdown(TemplateBenchmarkResult result, BenchmarkResults overall)
    {
        return $"""
            # Summary: {overall.DocumentId}
            
            **Template:** {result.TemplateName}
            **Target Words:** {result.TargetWords}
            **Actual Words:** {result.ActualWordCount}
            **Synthesis Time:** {result.SynthesisTime.TotalSeconds:F2}s
            
            ---
            
            {result.Summary.ExecutiveSummary}
            
            ---
            
            *Generated by DocSummarizer template benchmark*
            *Extraction: {overall.TotalSegments} segments, {overall.RetrievedSegments} retrieved in {overall.TotalExtractionTime.TotalSeconds:F2}s*
            """;
    }
    
    private static string FormatBenchmarkReport(BenchmarkResults results)
    {
        var sb = new System.Text.StringBuilder();
        
        sb.AppendLine($"# Template Benchmark Report: {results.DocumentId}");
        sb.AppendLine();
        sb.AppendLine($"**Document:** {results.DocumentPath}");
        sb.AppendLine($"**Total Segments:** {results.TotalSegments}");
        sb.AppendLine($"**Retrieved Segments:** {results.RetrievedSegments}");
        sb.AppendLine($"**Extraction Time:** {results.TotalExtractionTime.TotalSeconds:F2}s");
        sb.AppendLine();
        
        sb.AppendLine("## Results by Template");
        sb.AppendLine();
        sb.AppendLine("| Template | Target | Actual | Diff | Synthesis Time |");
        sb.AppendLine("|----------|--------|--------|------|----------------|");
        
        foreach (var r in results.TemplateResults)
        {
            if (r.Success)
            {
                var diff = r.ActualWordCount - r.TargetWords;
                var diffStr = r.TargetWords > 0 ? $"{diff:+#;-#;0}" : "n/a";
                sb.AppendLine($"| {r.TemplateName} | {r.TargetWords} | {r.ActualWordCount} | {diffStr} | {r.SynthesisTime.TotalSeconds:F2}s |");
            }
            else
            {
                sb.AppendLine($"| {r.TemplateName} | {r.TargetWords} | FAILED | - | - |");
            }
        }
        
        sb.AppendLine();
        sb.AppendLine("## Summary Previews");
        sb.AppendLine();
        
        foreach (var r in results.TemplateResults.Where(r => r.Success))
        {
            sb.AppendLine($"### {r.TemplateName}");
            sb.AppendLine();
            var preview = r.Summary.ExecutiveSummary.Length > 500 
                ? r.Summary.ExecutiveSummary[..500] + "..." 
                : r.Summary.ExecutiveSummary;
            sb.AppendLine(preview);
            sb.AppendLine();
        }
        
        return sb.ToString();
    }
    
    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }
    
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
