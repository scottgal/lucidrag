using System.Text;
using System.Text.RegularExpressions;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Converts markdown tables to CSV format for profiling.
/// Useful for analyzing tables embedded in documentation.
/// </summary>
public static class MarkdownTableConverter
{
    /// <summary>
    /// Extract all markdown tables from a markdown file and convert to CSV.
    /// Returns list of CSV strings (one per table found).
    /// </summary>
    public static List<string> ExtractTablesToCsv(string markdownContent)
    {
        var tables = new List<string>();
        var lines = markdownContent.Split('\n');

        List<string>? currentTable = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            // Detect table start (line with pipes)
            if (line.StartsWith('|') && line.EndsWith('|'))
            {
                if (currentTable == null)
                {
                    currentTable = new List<string>();
                }

                // Skip separator lines (e.g., |---|---|)
                if (IsSeparatorLine(line))
                {
                    continue;
                }

                currentTable.Add(line);
            }
            else if (currentTable != null && currentTable.Count > 0)
            {
                // End of table - convert to CSV
                var csv = MarkdownTableToCsv(currentTable);
                if (!string.IsNullOrWhiteSpace(csv))
                {
                    tables.Add(csv);
                }
                currentTable = null;
            }
        }

        // Handle table at end of file
        if (currentTable != null && currentTable.Count > 0)
        {
            var csv = MarkdownTableToCsv(currentTable);
            if (!string.IsNullOrWhiteSpace(csv))
            {
                tables.Add(csv);
            }
        }

        return tables;
    }

    /// <summary>
    /// Convert a single markdown table to CSV.
    /// </summary>
    /// <param name="markdownTable">Raw markdown table string (with pipes).</param>
    public static string MarkdownTableToCsv(string markdownTable)
    {
        var lines = markdownTable.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && !IsSeparatorLine(l))
            .ToList();

        return MarkdownTableToCsv(lines);
    }

    /// <summary>
    /// Convert markdown table lines to CSV.
    /// </summary>
    private static string MarkdownTableToCsv(List<string> tableLines)
    {
        if (tableLines.Count == 0) return string.Empty;

        var csvLines = new List<string>();

        foreach (var line in tableLines)
        {
            var cells = ParseTableRow(line);
            if (cells.Count == 0) continue;

            var csvLine = string.Join(",", cells.Select(CsvEscape));
            csvLines.Add(csvLine);
        }

        return string.Join("\n", csvLines);
    }

    /// <summary>
    /// Parse a markdown table row into cells.
    /// Example: "| Name | Age | City |" → ["Name", "Age", "City"]
    /// </summary>
    private static List<string> ParseTableRow(string line)
    {
        // Remove leading/trailing pipes and whitespace
        line = line.Trim();
        if (line.StartsWith('|')) line = line[1..];
        if (line.EndsWith('|')) line = line[..^1];

        // Split by pipe, handling escaped pipes
        var cells = line.Split('|')
            .Select(cell => cell.Trim())
            .Where(cell => !string.IsNullOrWhiteSpace(cell))
            .ToList();

        return cells;
    }

    /// <summary>
    /// Check if line is a markdown table separator (e.g., |---|---|).
    /// </summary>
    private static bool IsSeparatorLine(string line)
    {
        // Remove pipes and whitespace
        var cleaned = line.Replace("|", "").Replace(" ", "").Replace("\t", "");

        // Check if only dashes and colons remain (alignment markers)
        return !string.IsNullOrEmpty(cleaned) &&
               cleaned.All(c => c == '-' || c == ':');
    }

    /// <summary>
    /// Escape a CSV cell value.
    /// </summary>
    private static string CsvEscape(string value)
    {
        // Remove markdown formatting
        value = RemoveMarkdownFormatting(value);

        // Escape if contains special chars
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    /// <summary>
    /// Remove common markdown formatting from cell content.
    /// </summary>
    private static string RemoveMarkdownFormatting(string text)
    {
        // Remove bold/italic markers
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");  // **bold**
        text = Regex.Replace(text, @"\*(.+?)\*", "$1");      // *italic*
        text = Regex.Replace(text, @"__(.+?)__", "$1");      // __bold__
        text = Regex.Replace(text, @"_(.+?)_", "$1");        // _italic_

        // Remove inline code markers
        text = Regex.Replace(text, @"`(.+?)`", "$1");        // `code`

        // Remove links but keep text
        text = Regex.Replace(text, @"\[(.+?)\]\(.+?\)", "$1"); // [text](url)

        return text.Trim();
    }

    /// <summary>
    /// Convert markdown file to multiple CSV files (one per table).
    /// Saves to output directory with numbered filenames.
    /// </summary>
    public static async Task<List<string>> ConvertFileAsync(
        string markdownFilePath,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var content = await File.ReadAllTextAsync(markdownFilePath, ct);
        var tables = ExtractTablesToCsv(content);

        if (tables.Count == 0)
        {
            throw new InvalidOperationException($"No markdown tables found in {markdownFilePath}");
        }

        Directory.CreateDirectory(outputDirectory);

        var csvPaths = new List<string>();
        var baseName = Path.GetFileNameWithoutExtension(markdownFilePath);

        for (int i = 0; i < tables.Count; i++)
        {
            var csvFileName = tables.Count == 1
                ? $"{baseName}.csv"
                : $"{baseName}_table_{i + 1}.csv";

            var csvPath = Path.Combine(outputDirectory, csvFileName);
            await File.WriteAllTextAsync(csvPath, tables[i], ct);
            csvPaths.Add(csvPath);
        }

        return csvPaths;
    }

    /// <summary>
    /// Detect if a file contains markdown tables.
    /// </summary>
    public static async Task<bool> ContainsTablesAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath, ct);
            var lines = content.Split('\n');

            // Look for table-like patterns
            return lines.Any(line =>
            {
                var trimmed = line.Trim();
                return trimmed.StartsWith('|') &&
                       trimmed.EndsWith('|') &&
                       trimmed.Count(c => c == '|') >= 3; // At least 2 columns
            });
        }
        catch
        {
            return false;
        }
    }
}
