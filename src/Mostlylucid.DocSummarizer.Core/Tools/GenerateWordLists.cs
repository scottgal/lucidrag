// Run with: dotnet run --project Mostlylucid.DocSummarizer.Core -- --generate-wordlists
// Or execute the static method directly from a test

using System.Globalization;
using System.Text;

namespace Mostlylucid.DocSummarizer.Tools;

/// <summary>
/// Utility to generate word list files from .NET's CultureInfo data.
/// This uses the built-in globalization data for all supported cultures.
/// </summary>
public static class WordListGenerator
{
    /// <summary>
    /// Generate all word list files.
    /// </summary>
    /// <param name="outputDirectory">Directory to write the files to.</param>
    public static void GenerateAll(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        
        GenerateDayNames(Path.Combine(outputDirectory, "day-names.txt"));
        GenerateMonthNames(Path.Combine(outputDirectory, "month-names.txt"));
        
        Console.WriteLine($"Word lists generated in: {outputDirectory}");
    }

    /// <summary>
    /// Generate day names from all cultures.
    /// </summary>
    public static void GenerateDayNames(string outputPath)
    {
        var dayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
        {
            try
            {
                var dateFormat = culture.DateTimeFormat;
                
                // Full day names
                foreach (var day in dateFormat.DayNames)
                {
                    if (!string.IsNullOrWhiteSpace(day))
                        dayNames.Add(day.Trim());
                }
                
                // Abbreviated day names
                foreach (var day in dateFormat.AbbreviatedDayNames)
                {
                    if (!string.IsNullOrWhiteSpace(day))
                        dayNames.Add(day.Trim().TrimEnd('.'));
                }
                
                // Shortest day names (if available)
                foreach (var day in dateFormat.ShortestDayNames)
                {
                    if (!string.IsNullOrWhiteSpace(day))
                        dayNames.Add(day.Trim().TrimEnd('.'));
                }
            }
            catch
            {
                // Skip cultures that fail
            }
        }
        
        WriteWordList(outputPath, "Day names from .NET CultureInfo (all cultures)", dayNames);
        Console.WriteLine($"Generated {dayNames.Count} day names");
    }

    /// <summary>
    /// Generate month names from all cultures.
    /// </summary>
    public static void GenerateMonthNames(string outputPath)
    {
        var monthNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
        {
            try
            {
                var dateFormat = culture.DateTimeFormat;
                
                // Full month names
                foreach (var month in dateFormat.MonthNames)
                {
                    if (!string.IsNullOrWhiteSpace(month))
                        monthNames.Add(month.Trim());
                }
                
                // Abbreviated month names
                foreach (var month in dateFormat.AbbreviatedMonthNames)
                {
                    if (!string.IsNullOrWhiteSpace(month))
                        monthNames.Add(month.Trim().TrimEnd('.'));
                }
                
                // Genitive month names (used in some languages)
                foreach (var month in dateFormat.MonthGenitiveNames)
                {
                    if (!string.IsNullOrWhiteSpace(month))
                        monthNames.Add(month.Trim());
                }
                
                // Abbreviated genitive month names
                foreach (var month in dateFormat.AbbreviatedMonthGenitiveNames)
                {
                    if (!string.IsNullOrWhiteSpace(month))
                        monthNames.Add(month.Trim().TrimEnd('.'));
                }
            }
            catch
            {
                // Skip cultures that fail
            }
        }
        
        WriteWordList(outputPath, "Month names from .NET CultureInfo (all cultures)", monthNames);
        Console.WriteLine($"Generated {monthNames.Count} month names");
    }

    private static void WriteWordList(string path, string description, HashSet<string> words)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {description}");
        sb.AppendLine("# Auto-generated from .NET's globalization data");
        sb.AppendLine($"# Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"# Total: {words.Count} entries");
        sb.AppendLine();
        
        // Sort alphabetically for easier reading
        foreach (var word in words.OrderBy(w => w, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(word);
        }
        
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>
    /// Main entry point when run as a utility.
    /// </summary>
    public static void Main(string[] args)
    {
        var outputDir = args.Length > 0 ? args[0] : "Resources";
        GenerateAll(outputDir);
    }
}
