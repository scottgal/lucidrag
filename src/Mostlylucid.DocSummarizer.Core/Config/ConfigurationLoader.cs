using System.Text.Json;

namespace Mostlylucid.DocSummarizer.Config;

/// <summary>
///     Loads configuration from files with sensible defaults
/// </summary>
public static class ConfigurationLoader
{
    /// <summary>
    ///     Load configuration from file or use defaults
    /// </summary>
    public static DocSummarizerConfig Load(string? configPath = null)
    {
        var config = new DocSummarizerConfig();

        // Try to load from specified path
        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            var loaded = JsonSerializer.Deserialize(json, DocSummarizerJsonContext.Default.DocSummarizerConfig);
            if (loaded != null) config = loaded;
        }
        else
        {
            // Try default locations - including appsettings.json for convention
            var defaultPaths = new[]
            {
                "appsettings.json",
                "docsummarizer.json",
                ".docsummarizer.json",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docsummarizer.json")
            };

            foreach (var path in defaultPaths)
                if (File.Exists(path))
                    try
                    {
                        var json = File.ReadAllText(path);
                        var loaded =
                            JsonSerializer.Deserialize(json, DocSummarizerJsonContext.Default.DocSummarizerConfig);
                        if (loaded != null)
                        {
                            config = loaded;
                            break;
                        }
                    }
                    catch
                    {
                        // Continue to next file
                    }
        }

        return config;
    }

    /// <summary>
    ///     Save configuration to file
    /// </summary>
    public static void Save(DocSummarizerConfig config, string path)
    {
        var json = JsonSerializer.Serialize(config, DocSummarizerJsonContext.Default.DocSummarizerConfig);
        File.WriteAllText(path, json);
    }

    /// <summary>
    ///     Create a default configuration file
    /// </summary>
    public static void CreateDefault(string path = "docsummarizer.json")
    {
        Save(new DocSummarizerConfig(), path);
    }
}