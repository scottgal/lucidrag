using System.Net.Http.Json;
using System.Text.Json;

namespace Mostlylucid.GraphRag.Services;

/// <summary>
/// Simple Ollama HTTP client for LLM operations.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public OllamaClient(string url = "http://localhost:11434", string model = "llama3.2:3b")
    {
        _model = model;
        _http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<string> GenerateAsync(string prompt, double temperature = 0.7, CancellationToken ct = default)
    {
        var request = new OllamaRequest
        {
            Model = _model,
            Prompt = prompt,
            Stream = false,
            Options = new OllamaOptions { Temperature = temperature }
        };

        try
        {
            var response = await _http.PostAsJsonAsync("/api/generate", request, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(JsonOpts, ct);
            return result?.Response?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            return $"[LLM Error: {ex.Message}]";
        }
    }

    public async Task<T?> GenerateJsonAsync<T>(string prompt, CancellationToken ct = default) where T : class
    {
        var request = new OllamaRequest { Model = _model, Prompt = prompt, Stream = false, Format = "json" };
        
        try
        {
            var response = await _http.PostAsJsonAsync("/api/generate", request, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(JsonOpts, ct);
            return string.IsNullOrEmpty(result?.Response) ? null : JsonSerializer.Deserialize<T>(result.Response, JsonOpts);
        }
        catch { return null; }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try { return (await _http.GetAsync("/api/tags", ct)).IsSuccessStatusCode; }
        catch { return false; }
    }

    public void Dispose() => _http.Dispose();
}
