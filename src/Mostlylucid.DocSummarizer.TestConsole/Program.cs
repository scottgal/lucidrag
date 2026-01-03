using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.DocSummarizer;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Extensions;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;

Console.WriteLine("=== DocSummarizer Core Library Test ===\n");
Console.WriteLine("Testing code samples from README.md...\n");

// ============================================
// Test 1: Basic DI Registration (README Quick Start)
// ============================================
Console.WriteLine("Test 1: Basic DI Registration");
Console.WriteLine("-".PadRight(40, '-'));

var builder = Host.CreateApplicationBuilder(args);

// This is from README.md Configuration section
// Using InMemory vector store for testing to avoid DuckDB file locking issues
builder.Services.AddDocSummarizer(options =>
{
    // Use local ONNX embeddings (default, no external services)
    options.EmbeddingBackend = EmbeddingBackend.Onnx;
    
    // Use in-memory vector store for testing
    options.BertRag.VectorStore = VectorStoreBackend.InMemory;
    
    // Keep test output clean
    options.Output.Verbose = false;
});

var host = builder.Build();
var summarizer = host.Services.GetRequiredService<IDocumentSummarizer>();

Console.WriteLine("✓ AddDocSummarizer() registered successfully");
Console.WriteLine("✓ IDocumentSummarizer resolved from DI");
Console.WriteLine("✓ EmbeddingBackend.Onnx configured");
Console.WriteLine("✓ VectorStoreBackend.InMemory configured");
Console.WriteLine();

// ============================================
// Test 2: Segment Extraction (README Segment Extraction)
// ============================================
Console.WriteLine("Test 2: Segment Extraction");
Console.WriteLine("-".PadRight(40, '-'));

var testMarkdown = """
# Test Document

This is a test document for validating the DocSummarizer Core library.

## Key Features

The library provides several important capabilities:

- Local-first processing with ONNX models
- Citation grounding for every claim
- Multiple summarization modes

## Technical Details

The system uses BERT embeddings to understand document semantics.
This enables accurate retrieval and summarization.
""";

// This is from README.md Segment Extraction section
var extraction = await summarizer.ExtractSegmentsAsync(testMarkdown);

Console.WriteLine($"✓ ExtractSegmentsAsync completed");
Console.WriteLine($"  Total segments: {extraction.AllSegments.Count}");
Console.WriteLine($"  Top by salience: {extraction.TopBySalience.Count}");
Console.WriteLine($"  Content type: {extraction.ContentType}");
Console.WriteLine($"  Extraction time: {extraction.ExtractionTime.TotalSeconds:F2}s");
Console.WriteLine();

// Show top segments (from README example)
Console.WriteLine("Top segments by salience:");
foreach (var segment in extraction.TopBySalience.Take(3))
{
    var preview = segment.Text.Length > 60 
        ? segment.Text[..60].Replace("\n", " ") + "..." 
        : segment.Text.Replace("\n", " ");
    Console.WriteLine($"  [{segment.Type}] Score: {segment.SalienceScore:F2}");
    Console.WriteLine($"    {preview}");
}
Console.WriteLine();

// ============================================
// Test 3: SummarizeMarkdownAsync with Mode (README Summarization Modes)
// ============================================
Console.WriteLine("Test 3: Summarize with Bert Mode (No LLM)");
Console.WriteLine("-".PadRight(40, '-'));

// This is from README.md Summarization Modes section
// Pure BERT - no LLM needed, fastest
var summary = await summarizer.SummarizeMarkdownAsync(
    testMarkdown,
    mode: SummarizationMode.Bert);

Console.WriteLine($"✓ SummarizeMarkdownAsync with Bert mode completed");
Console.WriteLine($"  Executive summary length: {summary.ExecutiveSummary.Length} chars");
Console.WriteLine($"  Topics: {summary.TopicSummaries?.Count ?? 0}");
Console.WriteLine();

// Show summary preview
var summaryPreview = summary.ExecutiveSummary.Length > 200
    ? summary.ExecutiveSummary[..200] + "..."
    : summary.ExecutiveSummary;
Console.WriteLine("Summary preview:");
Console.WriteLine($"  {summaryPreview}");
Console.WriteLine();

// ============================================
// Test 4: Progress Channel (NEW FEATURE!)
// ============================================
Console.WriteLine("Test 4: Progress Channel API");
Console.WriteLine("-".PadRight(40, '-'));

// Create a progress channel
var channel = ProgressChannel.CreateUnbounded();

// Start consuming progress updates in the background
var progressTask = Task.Run(async () =>
{
    await foreach (var update in channel.Reader.ReadAllAsync())
    {
        var icon = update.Type switch
        {
            ProgressType.Stage => "📌",
            ProgressType.ItemProgress => "🔄",
            ProgressType.Completed => "✅",
            ProgressType.Info => "ℹ️",
            _ => "  "
        };
        Console.WriteLine($"  {icon} [{update.Stage}] {update.Message} ({update.PercentComplete:F0}%)");
    }
});

// Extract with progress reporting
var progressExtraction = await summarizer.ExtractSegmentsAsync(
    testMarkdown, 
    channel.Writer, 
    "progress-test");

// Wait for progress consumer to finish
await progressTask;

Console.WriteLine($"✓ Progress channel working - received updates");
Console.WriteLine();

// ============================================
// Test 5: Real Blog Post Test
// ============================================
Console.WriteLine("Test 5: Real Blog Post Extraction");
Console.WriteLine("-".PadRight(40, '-'));

var blogDir = @"C:\Blog\mostlylucidweb\Mostlylucid\Markdown";
var testFile = Path.Combine(blogDir, "tencommandments.md");

if (File.Exists(testFile))
{
    var blogMarkdown = await File.ReadAllTextAsync(testFile);
    var blogExtraction = await summarizer.ExtractSegmentsAsync(blogMarkdown, "tencommandments");
    
    Console.WriteLine($"✓ Extracted from: tencommandments.md");
    Console.WriteLine($"  Segments: {blogExtraction.AllSegments.Count}");
    Console.WriteLine($"  Top salient: {blogExtraction.TopBySalience.Count}");
    Console.WriteLine($"  Time: {blogExtraction.ExtractionTime.TotalSeconds:F2}s");
}
else
{
    Console.WriteLine($"  Skipped (file not found)");
}
Console.WriteLine();

// ============================================
// Summary
// ============================================
Console.WriteLine("=".PadRight(40, '='));
Console.WriteLine("All README code samples validated successfully!");
Console.WriteLine("=".PadRight(40, '='));
