using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.GraphRag;
using LucidRAG.Config;
using LucidRAG.Models;

namespace LucidRAG.Controllers.Api;

/// <summary>
/// Configuration and capabilities API.
/// Returns available modes and services for dynamic UI configuration.
/// </summary>
[ApiController]
[Route("api/config")]
public class ConfigController(
    IOptions<RagDocumentsConfig> ragConfig,
    IOptions<DocSummarizerConfig> summarizerConfig,
    IMemoryCache cache,
    ILogger<ConfigController> logger) : ControllerBase
{
    private const string ServicesCacheKey = "DetectedServices";

    /// <summary>
    /// Get available capabilities and modes based on detected services.
    /// </summary>
    [HttpGet("capabilities")]
    public async Task<IActionResult> GetCapabilities(CancellationToken ct = default)
    {
        var services = await GetDetectedServicesAsync();

        // Determine available extraction modes
        var extractionModes = new List<ExtractionModeInfo>
        {
            new("heuristic", "Heuristic (Fast)", "IDF + structural signals, no LLM calls", true, true)
        };

        if (services.OllamaAvailable)
        {
            extractionModes.Add(new("hybrid", "Hybrid", "Heuristic candidates + LLM enhancement per document", true, false));
            extractionModes.Add(new("llm", "Full LLM", "MSFT GraphRAG style - 2 LLM calls per chunk (slow, thorough)", true, false));
        }
        else
        {
            extractionModes.Add(new("hybrid", "Hybrid", "Requires Ollama - not available", false, false));
            extractionModes.Add(new("llm", "Full LLM", "Requires Ollama - not available", false, false));
        }

        // Determine available LLM models
        var llmModels = new List<LlmModelInfo>();
        if (services.OllamaAvailable)
        {
            foreach (var model in services.AvailableModels)
            {
                var isDefault = model == summarizerConfig.Value.Ollama.Model;
                llmModels.Add(new(model, model, "ollama", isDefault));
            }
        }

        // Current configuration
        var currentConfig = new CurrentConfig(
            ExtractionMode: ragConfig.Value.ExtractionMode.ToString().ToLowerInvariant(),
            LlmModel: summarizerConfig.Value.Ollama.Model,
            DemoMode: ragConfig.Value.DemoMode.Enabled
        );

        return Ok(new
        {
            services = new
            {
                ollama = new { available = services.OllamaAvailable, model = services.OllamaModel },
                docling = new { available = services.DoclingAvailable, hasGpu = services.DoclingHasGpu },
                qdrant = new { available = services.QdrantAvailable },
                onnx = new { available = services.OnnxAvailable }
            },
            extractionModes,
            llmModels,
            currentConfig,
            features = new
            {
                pdfConversion = services.DoclingAvailable,
                llmSummarization = services.OllamaAvailable,
                vectorPersistence = services.QdrantAvailable,
                graphVisualization = true,
                streamingResponses = services.OllamaAvailable
            }
        });
    }

    /// <summary>
    /// Get just the available extraction modes (lightweight endpoint for UI dropdown).
    /// </summary>
    [HttpGet("extraction-modes")]
    public async Task<IActionResult> GetExtractionModes(CancellationToken ct = default)
    {
        var services = await GetDetectedServicesAsync();

        var modes = new List<object>
        {
            new { value = "heuristic", label = "Heuristic (Fast)", available = true, isDefault = true }
        };

        if (services.OllamaAvailable)
        {
            modes.Add(new { value = "hybrid", label = "Hybrid", available = true, isDefault = false });
            modes.Add(new { value = "llm", label = "Full LLM", available = true, isDefault = false });
        }

        return Ok(new { modes, ollamaAvailable = services.OllamaAvailable });
    }

    /// <summary>
    /// Set the extraction mode for new document processing.
    /// </summary>
    [HttpPut("extraction-mode")]
    public IActionResult SetExtractionMode([FromBody] SetExtractionModeRequest request)
    {
        if (ragConfig.Value.DemoMode.Enabled)
        {
            return StatusCode(403, new { error = "Configuration changes disabled in demo mode" });
        }

        if (!Enum.TryParse<ExtractionMode>(request.Mode, true, out var mode))
        {
            return BadRequest(new { error = $"Invalid mode. Valid values: heuristic, hybrid, llm" });
        }

        // Note: This only affects runtime configuration. For persistence, update appsettings.json
        ragConfig.Value.ExtractionMode = mode;

        logger.LogInformation("Extraction mode changed to {Mode}", mode);

        return Ok(new { mode = mode.ToString().ToLowerInvariant(), message = "Extraction mode updated for new documents" });
    }

    private async Task<DetectedServices> GetDetectedServicesAsync()
    {
        if (cache.TryGetValue(ServicesCacheKey, out DetectedServices? cached) && cached is not null)
        {
            return cached;
        }

        logger.LogDebug("Detecting available services...");
        var services = await ServiceDetector.DetectSilentAsync(summarizerConfig.Value);

        cache.Set(ServicesCacheKey, services, TimeSpan.FromMinutes(5));
        return services;
    }
}
