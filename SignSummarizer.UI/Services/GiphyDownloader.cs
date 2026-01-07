using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using SignSummarizer.UI.Models;
using System.Text.Json;

namespace SignSummarizer.UI.Services;

public interface IGiphyDownloader
{
    Task<List<GiphyGif>> SearchAsync(
        string query,
        int limit = 25,
        int offset = 0,
        string rating = "g",
        string lang = "en",
        CancellationToken cancellationToken = default);
    
    Task<string> DownloadGifAsync(
        GiphyGif gif,
        string outputDirectory,
        CancellationToken cancellationToken = default);
    
    Task<List<string>> DownloadSearchResultsAsync(
        string query,
        string outputDirectory,
        int limit = 25,
        int offset = 0,
        CancellationToken cancellationToken = default);
}

public sealed class GiphyDownloader : IGiphyDownloader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GiphyDownloader> _logger;
    private const string BaseUrl = "https://api.giphy.com/v1/gifs/search";
    private const string ApiKey = "9f48Pmll4owK9wOV77mDiQ4Na2sFwCbD";
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    
    public GiphyDownloader(
        HttpClient httpClient,
        ILogger<GiphyDownloader> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }
    
    public async Task<List<GiphyGif>> SearchAsync(
        string query,
        int limit = 25,
        int offset = 0,
        string rating = "g",
        string lang = "en",
        CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}?api_key={ApiKey}&q={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}&rating={rating}&lang=en&bundle=messaging_non_clips";
        
        _logger.LogInformation("Searching Giphy for: {Query} (limit: {Limit}, offset: {Offset})", query, limit, offset);
        
        var response = await _httpClient.GetAsync(url, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Giphy API error: {StatusCode} - {ReasonPhrase}", response.StatusCode, response.ReasonPhrase);
            return new List<GiphyGif>();
        }
        
        var giphyResponse = await response.Content.ReadFromJsonAsync<GiphyResponse>(_jsonOptions, cancellationToken);
        
        if (giphyResponse?.Meta?.Status != 200)
        {
            _logger.LogError("Giphy API returned error: {Status} - {Message}", giphyResponse?.Meta?.Status, giphyResponse?.Meta?.Msg);
            return new List<GiphyGif>();
        }
        
        _logger.LogInformation("Found {Count} GIFs for query: {Query}", giphyResponse?.Data?.Count ?? 0, query);
        
        return giphyResponse?.Data ?? new List<GiphyGif>();
    }
    
    public async Task<string> DownloadGifAsync(
        GiphyGif gif,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        
        var gifUrl = gif.Images.Original.Url;
        var fileName = SanitizeFileName($"{gif.Slug}_{gif.Id}.gif");
        var outputPath = Path.Combine(outputDirectory, fileName);
        
        if (File.Exists(outputPath))
        {
            _logger.LogInformation("GIF already exists: {FileName}", fileName);
            return outputPath;
        }
        
        _logger.LogInformation("Downloading GIF: {Title} from {Url}", gif.Title, gifUrl);
        
        var response = await _httpClient.GetAsync(gifUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        await File.WriteAllBytesAsync(outputPath, await response.Content.ReadAsByteArrayAsync(cancellationToken), cancellationToken);
        
        _logger.LogInformation("Downloaded GIF to: {OutputPath}", outputPath);
        
        return outputPath;
    }
    
    public async Task<List<string>> DownloadSearchResultsAsync(
        string query,
        string outputDirectory,
        int limit = 25,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var gifs = await SearchAsync(query, limit, offset, cancellationToken: cancellationToken);
        var downloadedPaths = new List<string>();
        
        foreach (var gif in gifs)
        {
            try
            {
                var path = await DownloadGifAsync(gif, outputDirectory, cancellationToken);
                downloadedPaths.Add(path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download GIF: {Title}", gif.Title);
            }
        }
        
        return downloadedPaths;
    }
    
    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", fileName.Split(invalidChars));
    }
}