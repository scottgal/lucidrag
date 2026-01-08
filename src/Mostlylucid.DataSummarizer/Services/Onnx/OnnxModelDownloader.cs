using Mostlylucid.DataSummarizer.Configuration;
using Spectre.Console;

namespace Mostlylucid.DataSummarizer.Services.Onnx;

/// <summary>
/// Downloads and caches ONNX models from HuggingFace
/// </summary>
public class OnnxModelDownloader
{
    private readonly HttpClient _httpClient;
    private readonly string _modelDirectory;
    private readonly bool _verbose;

    public OnnxModelDownloader(OnnxConfig config, bool verbose = false)
    {
        _modelDirectory = config.ModelDirectory;
        _verbose = verbose;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DataSummarizer/1.0");
    }

    /// <summary>
    /// Ensure embedding model is downloaded and return local path
    /// </summary>
    public async Task<EmbeddingModelPaths> EnsureEmbeddingModelAsync(
        EmbeddingModelInfo model, 
        CancellationToken ct = default)
    {
        var modelDir = Path.Combine(_modelDirectory, "embeddings", SanitizeName(model.Name));
        Directory.CreateDirectory(modelDir);

        var modelPath = Path.Combine(modelDir, "model.onnx");
        var tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        var vocabPath = Path.Combine(modelDir, "vocab.txt");

        var tasks = new List<Task>();

        if (!File.Exists(modelPath))
            tasks.Add(DownloadFileAsync(model.GetModelUrl(), modelPath, $"Downloading {model.Name} model", model.SizeBytes, ct));
        
        if (!File.Exists(tokenizerPath))
            tasks.Add(DownloadFileAsync(model.GetTokenizerUrl(), tokenizerPath, $"Downloading tokenizer", null, ct));
        
        if (!File.Exists(vocabPath))
            tasks.Add(DownloadFileAsync(model.GetVocabUrl(), vocabPath, $"Downloading vocab", null, ct));

        if (tasks.Count > 0)
        {
            // Always show download message - this is a one-time operation
            AnsiConsole.MarkupLine($"[yellow]First run: downloading ONNX embedding model {model.Name} (~{model.SizeBytes / 1_000_000}MB)...[/]");
            AnsiConsole.MarkupLine($"[dim]Models are cached at: {modelDir}[/]");
            
            await Task.WhenAll(tasks);
            
            AnsiConsole.MarkupLine($"[green]Model downloaded successfully![/]");
        }

        return new EmbeddingModelPaths(modelPath, tokenizerPath, vocabPath);
    }

    private async Task DownloadFileAsync(
        string url, 
        string localPath, 
        string description,
        long? expectedSize,
        CancellationToken ct)
    {
        var tempPath = localPath + ".tmp";
        
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? expectedSize ?? 0;
            
            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                // Stream the download to disk
                await contentStream.CopyToAsync(fileStream, ct);
            }
            
            // Move after streams are closed
            File.Move(tempPath, localPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    private static string SanitizeName(string name)
    {
        return name.Replace("/", "_").Replace("\\", "_").Replace(":", "_");
    }
}

/// <summary>
/// Paths to downloaded embedding model files
/// </summary>
public record EmbeddingModelPaths(string ModelPath, string TokenizerPath, string VocabPath);
