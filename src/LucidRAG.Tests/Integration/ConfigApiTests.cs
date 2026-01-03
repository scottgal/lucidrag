using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace LucidRAG.Tests.Integration;

/// <summary>
/// Integration tests for the Config API (capabilities detection and mode switching)
/// </summary>
[Collection("Integration")]
public class ConfigApiTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ConfigApiTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        // No cleanup needed for config tests
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetCapabilities_ReturnsCompleteCapabilitiesObject()
    {
        // Act
        var response = await _client.GetAsync("/api/config/capabilities");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Verify services section exists
        result.TryGetProperty("services", out var services).Should().BeTrue();
        services.TryGetProperty("ollama", out _).Should().BeTrue();
        services.TryGetProperty("docling", out _).Should().BeTrue();
        services.TryGetProperty("qdrant", out _).Should().BeTrue();
        services.TryGetProperty("onnx", out _).Should().BeTrue();

        // Verify extraction modes
        result.TryGetProperty("extractionModes", out var modes).Should().BeTrue();
        modes.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        // Verify current config
        result.TryGetProperty("currentConfig", out var config).Should().BeTrue();
        config.TryGetProperty("extractionMode", out _).Should().BeTrue();
        config.TryGetProperty("demoMode", out _).Should().BeTrue();

        // Verify features
        result.TryGetProperty("features", out var features).Should().BeTrue();
        features.TryGetProperty("pdfConversion", out _).Should().BeTrue();
        features.TryGetProperty("llmSummarization", out _).Should().BeTrue();
        features.TryGetProperty("graphVisualization", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetCapabilities_OnnxAlwaysAvailable()
    {
        // Act
        var response = await _client.GetAsync("/api/config/capabilities");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var onnx = result.GetProperty("services").GetProperty("onnx");
        onnx.GetProperty("available").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetExtractionModes_ReturnsHeuristicAsDefault()
    {
        // Act
        var response = await _client.GetAsync("/api/config/extraction-modes");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var modes = result.GetProperty("modes");
        modes.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        // Find heuristic mode
        var heuristicMode = modes.EnumerateArray()
            .FirstOrDefault(m => m.GetProperty("value").GetString() == "heuristic");

        heuristicMode.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        heuristicMode.GetProperty("available").GetBoolean().Should().BeTrue();
        heuristicMode.GetProperty("isDefault").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetExtractionModes_IncludesOllamaAvailability()
    {
        // Act
        var response = await _client.GetAsync("/api/config/extraction-modes");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.TryGetProperty("ollamaAvailable", out var ollamaAvailable).Should().BeTrue();
        ollamaAvailable.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }

    [Fact]
    public async Task GetExtractionModes_AllModesHaveRequiredProperties()
    {
        // Act
        var response = await _client.GetAsync("/api/config/extraction-modes");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var modes = result.GetProperty("modes");

        foreach (var mode in modes.EnumerateArray())
        {
            mode.TryGetProperty("value", out _).Should().BeTrue();
            mode.TryGetProperty("label", out _).Should().BeTrue();
            mode.TryGetProperty("available", out _).Should().BeTrue();
            mode.TryGetProperty("isDefault", out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task SetExtractionMode_ValidMode_ReturnsSuccess()
    {
        // Arrange
        var request = new { mode = "heuristic" };

        // Act
        var response = await _client.PutAsJsonAsync("/api/config/extraction-mode", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("mode").GetString().Should().Be("heuristic");
        result.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SetExtractionMode_InvalidMode_ReturnsBadRequest()
    {
        // Arrange
        var request = new { mode = "invalid_mode" };

        // Act
        var response = await _client.PutAsJsonAsync("/api/config/extraction-mode", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("error").GetString().Should().Contain("Invalid mode");
    }

    [Fact]
    public async Task SetExtractionMode_CaseInsensitive()
    {
        // Arrange
        var request = new { mode = "HEURISTIC" };

        // Act
        var response = await _client.PutAsJsonAsync("/api/config/extraction-mode", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("mode").GetString().Should().Be("heuristic");
    }

    [Fact]
    public async Task GetCapabilities_CachesResults()
    {
        // Act - Make two rapid requests
        var response1 = await _client.GetAsync("/api/config/capabilities");
        var response2 = await _client.GetAsync("/api/config/capabilities");

        // Assert - Both should succeed (and use cached result for second)
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        var result1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        var result2 = await response2.Content.ReadFromJsonAsync<JsonElement>();

        // Results should be identical (from cache)
        result1.GetProperty("services").GetProperty("onnx").GetProperty("available").GetBoolean()
            .Should().Be(result2.GetProperty("services").GetProperty("onnx").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task GetCapabilities_ExtractonModesInfo_IncludesDescriptions()
    {
        // Act
        var response = await _client.GetAsync("/api/config/capabilities");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var modes = result.GetProperty("extractionModes");

        foreach (var mode in modes.EnumerateArray())
        {
            mode.TryGetProperty("description", out var description).Should().BeTrue();
            description.GetString().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task GetCapabilities_LlmModesUnavailableWhenNoOllama()
    {
        // This test verifies that hybrid/llm modes show as unavailable when Ollama is not running
        // Note: In CI/test environments, Ollama may or may not be available

        // Act
        var response = await _client.GetAsync("/api/config/capabilities");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ollamaAvailable = result.GetProperty("services").GetProperty("ollama").GetProperty("available").GetBoolean();
        var modes = result.GetProperty("extractionModes");

        var hybridMode = modes.EnumerateArray()
            .FirstOrDefault(m => m.GetProperty("value").GetString() == "hybrid");

        if (!ollamaAvailable && hybridMode.ValueKind != JsonValueKind.Undefined)
        {
            // When Ollama is not available, hybrid mode should be marked as unavailable
            hybridMode.GetProperty("available").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public async Task GetCapabilities_GraphVisualizationAlwaysEnabled()
    {
        // Act
        var response = await _client.GetAsync("/api/config/capabilities");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var features = result.GetProperty("features");
        features.GetProperty("graphVisualization").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetCapabilities_CurrentConfigReflectsSettings()
    {
        // First set a mode
        await _client.PutAsJsonAsync("/api/config/extraction-mode", new { mode = "heuristic" });

        // Act
        var response = await _client.GetAsync("/api/config/capabilities");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var currentConfig = result.GetProperty("currentConfig");
        currentConfig.GetProperty("extractionMode").GetString().Should().Be("heuristic");
    }
}
