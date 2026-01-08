using System.CommandLine;

namespace Mostlylucid.DataSummarizer.Services;

public static class CliHelpers
{
    public static IEnumerable<string> ExpandPatternsHelper(IEnumerable<string?> patterns, string? directory, string[]? supported = null)
    {
        supported ??= new[] { ".csv", ".xlsx", ".xls", ".parquet", ".json", ".log" };
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList();
        if (!string.IsNullOrWhiteSpace(directory)) list.Add(directory!);

        foreach (var entry in list)
        {
            if (Directory.Exists(entry))
            {
                foreach (var f in Directory.GetFiles(entry, "*", SearchOption.AllDirectories))
                {
                    if (supported.Contains(Path.GetExtension(f).ToLowerInvariant())) set.Add(f);
                }
                continue;
            }

            if (entry.Contains("*") || entry.Contains("?"))
            {
                var dir = Path.GetDirectoryName(entry);
                var pattern = Path.GetFileName(entry);
                var baseDir = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : dir;
                foreach (var f in Directory.GetFiles(baseDir, pattern, SearchOption.AllDirectories))
                {
                    if (supported.Contains(Path.GetExtension(f).ToLowerInvariant())) set.Add(f);
                }
                continue;
            }

            if (File.Exists(entry) && supported.Contains(Path.GetExtension(entry).ToLowerInvariant())) set.Add(entry);
        }
        return set;
    }
}
