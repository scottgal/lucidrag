using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Services.Vision.Clients;

namespace LucidRAG.ImageCli.Services.OutputFormatters;

/// <summary>
/// Interface for formatting image analysis results in different output formats.
/// </summary>
public interface IOutputFormatter
{
    /// <summary>
    /// Format a single image analysis result.
    /// </summary>
    /// <param name="filePath">Path to the analyzed image</param>
    /// <param name="profile">Image analysis profile</param>
    /// <param name="llmCaption">Optional LLM-generated caption</param>
    /// <param name="extractedText">Optional OCR extracted text</param>
    /// <param name="gifMotion">Optional GIF motion analysis data</param>
    /// <param name="evidenceClaims">Optional evidence-backed claims from vision LLM</param>
    /// <returns>Formatted output string</returns>
    string FormatSingle(string filePath, ImageProfile profile, string? llmCaption = null, string? extractedText = null, GifMotionProfile? gifMotion = null, List<EvidenceClaim>? evidenceClaims = null);

    /// <summary>
    /// Format multiple image analysis results.
    /// </summary>
    /// <param name="results">Collection of analysis results</param>
    /// <returns>Formatted output string</returns>
    string FormatBatch(IEnumerable<ImageAnalysisResult> results);

    /// <summary>
    /// Write formatted output to console or file.
    /// </summary>
    /// <param name="content">Content to write</param>
    /// <param name="outputPath">Optional file path to write to</param>
    Task WriteAsync(string content, string? outputPath = null);
}

/// <summary>
/// Result of an image analysis operation.
/// </summary>
/// <param name="FilePath">Path to the analyzed image</param>
/// <param name="Profile">Image profile (null if failed)</param>
/// <param name="LlmCaption">Optional LLM-generated caption</param>
/// <param name="ExtractedText">Optional OCR text</param>
/// <param name="Error">Error message if analysis failed</param>
/// <param name="WasEscalated">Whether this analysis was escalated to LLM</param>
/// <param name="GifMotion">GIF motion analysis data (for animated images)</param>
/// <param name="EvidenceClaims">Optional evidence-backed claims from vision LLM</param>
public record ImageAnalysisResult(
    string FilePath,
    ImageProfile? Profile = null,
    string? LlmCaption = null,
    string? ExtractedText = null,
    string? Error = null,
    bool WasEscalated = false,
    GifMotionProfile? GifMotion = null,
    List<EvidenceClaim>? EvidenceClaims = null);
