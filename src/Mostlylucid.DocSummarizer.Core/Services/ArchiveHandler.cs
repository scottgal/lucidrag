using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Cross-platform archive handler with security guards against compression bombs.
/// Supports ZIP files containing text, HTML, or markdown content.
/// Off by default - must be explicitly enabled via config.
/// </summary>
public static partial class ArchiveHandler
{
    /// <summary>
    /// Maximum uncompressed size allowed (100 MB default - prevents zip bombs)
    /// </summary>
    public const long DefaultMaxUncompressedSize = 100 * 1024 * 1024;
    
    /// <summary>
    /// Maximum compression ratio allowed (suspicious if > 100:1)
    /// </summary>
    public const double MaxCompressionRatio = 100.0;
    
    /// <summary>
    /// Maximum number of entries to scan in a ZIP
    /// </summary>
    public const int MaxEntriesToScan = 1000;
    
    /// <summary>
    /// Supported text extensions within archives
    /// </summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".html", ".htm", ".xhtml"
    };

    /// <summary>
    /// Check if archive handling is safe for this file.
    /// Returns details about the archive contents without extracting.
    /// </summary>
    public static ArchiveInfo? InspectArchive(string filePath, long maxSize = DefaultMaxUncompressedSize)
    {
        if (!File.Exists(filePath))
            return null;
        
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext != ".zip")
            return null;
        
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            var compressedSize = new FileInfo(filePath).Length;
            
            if (zip.Entries.Count > MaxEntriesToScan)
            {
                return new ArchiveInfo
                {
                    IsValid = false,
                    Error = $"Too many entries ({zip.Entries.Count} > {MaxEntriesToScan})"
                };
            }
            
            // Find text files
            var textEntries = zip.Entries
                .Where(e => TextExtensions.Contains(Path.GetExtension(e.Name)))
                .OrderByDescending(e => e.Length)
                .Take(10)
                .ToList();
            
            if (textEntries.Count == 0)
            {
                return new ArchiveInfo
                {
                    IsValid = false,
                    Error = "No text files found in archive"
                };
            }
            
            // Check for compression bomb
            var totalUncompressed = textEntries.Sum(e => e.Length);
            var ratio = compressedSize > 0 ? (double)totalUncompressed / compressedSize : 0;
            
            if (totalUncompressed > maxSize)
            {
                return new ArchiveInfo
                {
                    IsValid = false,
                    Error = $"Uncompressed size too large ({totalUncompressed / (1024 * 1024):F1} MB > {maxSize / (1024 * 1024)} MB limit)"
                };
            }
            
            if (ratio > MaxCompressionRatio)
            {
                return new ArchiveInfo
                {
                    IsValid = false,
                    Error = $"Suspicious compression ratio ({ratio:F1}:1 > {MaxCompressionRatio}:1 limit)"
                };
            }
            
            // Find the best text file (prefer HTML for Gutenberg, then MD, then TXT)
            var mainEntry = textEntries
                .OrderByDescending(e => GetExtensionPriority(Path.GetExtension(e.Name)))
                .ThenByDescending(e => e.Length)
                .First();
            
            return new ArchiveInfo
            {
                IsValid = true,
                MainFileName = mainEntry.Name,
                MainFileSize = mainEntry.Length,
                TotalTextFiles = textEntries.Count,
                CompressionRatio = ratio,
                IsGutenberg = IsLikelyGutenberg(mainEntry.Name, zip.Entries)
            };
        }
        catch (Exception ex)
        {
            return new ArchiveInfo
            {
                IsValid = false,
                Error = $"Failed to read archive: {ex.Message}"
            };
        }
    }
    
    /// <summary>
    /// Extract text content from an archive file.
    /// </summary>
    public static async Task<string> ExtractTextAsync(
        string archivePath, 
        long maxSize = DefaultMaxUncompressedSize,
        CancellationToken ct = default)
    {
        var info = InspectArchive(archivePath, maxSize);
        if (info == null || !info.IsValid)
        {
            throw new InvalidOperationException(info?.Error ?? "Invalid or unsupported archive");
        }
        
        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.Entries.FirstOrDefault(e => e.Name == info.MainFileName);
        
        if (entry == null)
        {
            throw new InvalidOperationException($"Entry not found: {info.MainFileName}");
        }
        
        // Read with size limit guard
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        
        var content = await ReadWithLimitAsync(reader, maxSize, ct);
        
        // Convert to markdown if HTML
        var ext = Path.GetExtension(info.MainFileName ?? "").ToLowerInvariant();
        if (ext is ".html" or ".htm" or ".xhtml")
        {
            content = ConvertHtmlToMarkdown(content, info.IsGutenberg);
        }
        
        return content;
    }
    
    private static async Task<string> ReadWithLimitAsync(StreamReader reader, long maxSize, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        long totalRead = 0;
        int charsRead;
        
        while ((charsRead = await reader.ReadAsync(buffer, ct)) > 0)
        {
            totalRead += charsRead * 2; // Approximate byte count
            if (totalRead > maxSize)
            {
                throw new InvalidOperationException($"Content exceeds size limit ({maxSize / (1024 * 1024)} MB)");
            }
            sb.Append(buffer, 0, charsRead);
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Convert HTML to simple markdown text.
    /// Uses regex-based parsing (no external dependencies).
    /// </summary>
    private static string ConvertHtmlToMarkdown(string html, bool isGutenberg)
    {
        var sb = new StringBuilder();
        
        // Remove Gutenberg boilerplate if detected
        if (isGutenberg)
        {
            html = RemoveGutenbergBoilerplate(html);
        }
        
        // Remove scripts, styles, head
        html = ScriptStyleRegex().Replace(html, "");
        html = HeadRegex().Replace(html, "");
        
        // Convert headings
        html = H1Regex().Replace(html, "\n# $1\n");
        html = H2Regex().Replace(html, "\n## $1\n");
        html = H3Regex().Replace(html, "\n### $1\n");
        html = H4to6Regex().Replace(html, "\n#### $1\n");
        
        // Convert paragraphs
        html = ParagraphRegex().Replace(html, "\n$1\n");
        
        // Convert blockquotes
        html = BlockquoteRegex().Replace(html, m => 
            "\n> " + m.Groups[1].Value.Replace("\n", "\n> ") + "\n");
        
        // Convert breaks and rules
        html = BreakRegex().Replace(html, "\n");
        html = HrRegex().Replace(html, "\n---\n");
        
        // Remove remaining HTML tags
        html = AllTagsRegex().Replace(html, "");
        
        // Decode HTML entities
        html = System.Net.WebUtility.HtmlDecode(html);
        
        // Clean up whitespace
        html = MultipleSpacesRegex().Replace(html, " ");
        html = MultipleNewlinesRegex().Replace(html, "\n\n");
        
        // Trim lines
        var lines = html.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) || l == "");
        
        return string.Join("\n", lines).Trim();
    }
    
    private static string RemoveGutenbergBoilerplate(string html)
    {
        // Remove everything before "*** START OF" marker
        var startMatch = GutenbergStartRegex().Match(html);
        if (startMatch.Success)
        {
            html = html[(startMatch.Index + startMatch.Length)..];
        }
        
        // Remove everything after "*** END OF" marker
        var endMatch = GutenbergEndRegex().Match(html);
        if (endMatch.Success)
        {
            html = html[..endMatch.Index];
        }
        
        // Remove pg-header and pg-footer divs
        html = PgHeaderFooterRegex().Replace(html, "");
        
        return html;
    }
    
    private static int GetExtensionPriority(string ext) => ext.ToLowerInvariant() switch
    {
        ".html" or ".htm" => 3,  // Prefer HTML (usually has structure)
        ".md" or ".markdown" => 2,
        ".txt" or ".text" => 1,
        _ => 0
    };
    
    private static bool IsLikelyGutenberg(string fileName, IEnumerable<ZipArchiveEntry> entries)
    {
        // Gutenberg files typically named pg####.html or pg####-images.html
        if (GutenbergFileNameRegex().IsMatch(fileName))
            return true;
        
        // Or have images folder with specific structure
        return entries.Any(e => e.FullName.Contains("images/", StringComparison.OrdinalIgnoreCase));
    }
    
    // Regex patterns
    [GeneratedRegex(@"<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();
    
    [GeneratedRegex(@"<head[^>]*>.*?</head>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadRegex();
    
    [GeneratedRegex(@"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex H1Regex();
    
    [GeneratedRegex(@"<h2[^>]*>(.*?)</h2>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex H2Regex();
    
    [GeneratedRegex(@"<h3[^>]*>(.*?)</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex H3Regex();
    
    [GeneratedRegex(@"<h[4-6][^>]*>(.*?)</h[4-6]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex H4to6Regex();
    
    [GeneratedRegex(@"<p[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ParagraphRegex();
    
    [GeneratedRegex(@"<blockquote[^>]*>(.*?)</blockquote>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockquoteRegex();
    
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();
    
    [GeneratedRegex(@"<hr\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HrRegex();
    
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AllTagsRegex();
    
    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex MultipleSpacesRegex();
    
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleNewlinesRegex();
    
    [GeneratedRegex(@"\*\*\*\s*START OF.*?\*\*\*", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex GutenbergStartRegex();
    
    [GeneratedRegex(@"\*\*\*\s*END OF.*?\*\*\*", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex GutenbergEndRegex();
    
    [GeneratedRegex(@"<[^>]*(id|class)\s*=\s*[""']pg-(header|footer)[""'][^>]*>.*?</[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PgHeaderFooterRegex();
    
    [GeneratedRegex(@"^pg\d+(-\w+)?\.html?$", RegexOptions.IgnoreCase)]
    private static partial Regex GutenbergFileNameRegex();
}

/// <summary>
/// Information about an archive file
/// </summary>
public class ArchiveInfo
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public string? MainFileName { get; init; }
    public long MainFileSize { get; init; }
    public int TotalTextFiles { get; init; }
    public double CompressionRatio { get; init; }
    public bool IsGutenberg { get; init; }
}
