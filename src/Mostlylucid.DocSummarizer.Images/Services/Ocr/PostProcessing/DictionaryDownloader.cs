using Microsoft.Extensions.Logging;
using System.IO.Compression;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr.PostProcessing;

/// <summary>
/// Auto-downloads Hunspell dictionaries from LibreOffice repository
/// Zero-friction setup - downloads on first use
/// </summary>
public class DictionaryDownloader
{
    private readonly string _dictionaryDirectory;
    private readonly ILogger<DictionaryDownloader>? _logger;
    private readonly HttpClient _httpClient;

    // Dictionary sources from LibreOffice GitHub
    private static readonly Dictionary<string, DictionarySource> KnownDictionaries = new()
    {
        ["en_US"] = new("en", "American English"),
        ["en_GB"] = new("en", "British English"),
        ["es_ES"] = new("es_ES", "Spanish"),
        ["fr_FR"] = new("fr_FR", "French"),
        ["de_DE"] = new("de_DE", "German"),
        ["it_IT"] = new("it_IT", "Italian"),
        ["pt_BR"] = new("pt_BR", "Portuguese (Brazil)"),
        ["ru_RU"] = new("ru_RU", "Russian"),
        ["zh_CN"] = new("zh_CN", "Chinese (Simplified)"),
        ["ja_JP"] = new("ja_JP", "Japanese"),
    };

    private const string LibreOfficeBaseUrl = "https://raw.githubusercontent.com/LibreOffice/dictionaries/master";

    public DictionaryDownloader(string dictionaryDirectory, ILogger<DictionaryDownloader>? logger = null)
    {
        _dictionaryDirectory = dictionaryDirectory;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        // Ensure directory exists
        Directory.CreateDirectory(_dictionaryDirectory);
    }

    /// <summary>
    /// Check if dictionary is available locally
    /// </summary>
    public bool IsDictionaryAvailable(string language)
    {
        var affPath = Path.Combine(_dictionaryDirectory, $"{language}.aff");
        var dicPath = Path.Combine(_dictionaryDirectory, $"{language}.dic");
        return File.Exists(affPath) && File.Exists(dicPath);
    }

    /// <summary>
    /// Auto-download dictionary if not available
    /// Returns true if dictionary is ready (already exists or successfully downloaded)
    /// </summary>
    public async Task<bool> EnsureDictionaryAsync(string language, CancellationToken ct = default)
    {
        // Already available
        if (IsDictionaryAvailable(language))
        {
            _logger?.LogDebug("Dictionary already available: {Language}", language);
            return true;
        }

        // Check if we know how to download this language
        if (!KnownDictionaries.TryGetValue(language, out var source))
        {
            _logger?.LogWarning("Unknown language code: {Language}, cannot auto-download", language);
            return false;
        }

        _logger?.LogInformation("Downloading dictionary for {Language} ({Description})...", language, source.Description);

        try
        {
            // Download .aff file
            var affUrl = $"{LibreOfficeBaseUrl}/{source.Directory}/{language}.aff";
            var affPath = Path.Combine(_dictionaryDirectory, $"{language}.aff");
            var affDownloaded = await DownloadFileAsync(affUrl, affPath, ct);

            if (!affDownloaded)
            {
                _logger?.LogError("Failed to download .aff file for {Language}", language);
                return false;
            }

            // Download .dic file
            var dicUrl = $"{LibreOfficeBaseUrl}/{source.Directory}/{language}.dic";
            var dicPath = Path.Combine(_dictionaryDirectory, $"{language}.dic");
            var dicDownloaded = await DownloadFileAsync(dicUrl, dicPath, ct);

            if (!dicDownloaded)
            {
                _logger?.LogError("Failed to download .dic file for {Language}", language);
                // Clean up partial download
                if (File.Exists(affPath))
                    File.Delete(affPath);
                return false;
            }

            _logger?.LogInformation("Successfully downloaded dictionary for {Language}", language);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error downloading dictionary for {Language}", language);
            return false;
        }
    }

    /// <summary>
    /// Download a file with retry logic
    /// </summary>
    private async Task<bool> DownloadFileAsync(string url, string destinationPath, CancellationToken ct, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger?.LogDebug("Downloading {Url} (attempt {Attempt}/{MaxRetries})...", url, attempt, maxRetries);

                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("HTTP {StatusCode} when downloading {Url}", response.StatusCode, url);
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct); // Exponential backoff
                        continue;
                    }
                    return false;
                }

                var contentLength = response.Content.Headers.ContentLength ?? 0;
                _logger?.LogDebug("Downloading {Bytes} bytes to {Path}", contentLength, destinationPath);

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fileStream, ct);

                _logger?.LogDebug("Downloaded {Path} ({Bytes} bytes)", destinationPath, new FileInfo(destinationPath).Length);
                return true;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger?.LogWarning(ex, "Download attempt {Attempt} failed, retrying...", attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to download {Url} after {MaxRetries} attempts", url, maxRetries);
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Get list of available languages for download
    /// </summary>
    public IReadOnlyList<string> GetAvailableLanguages()
    {
        return KnownDictionaries.Keys.ToList();
    }

    /// <summary>
    /// Get description for a language code
    /// </summary>
    public string? GetLanguageDescription(string language)
    {
        return KnownDictionaries.TryGetValue(language, out var source) ? source.Description : null;
    }
}

/// <summary>
/// Dictionary source information
/// </summary>
internal record DictionarySource(string Directory, string Description);
