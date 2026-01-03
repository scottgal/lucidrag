using Xunit;
using Mostlylucid.DocSummarizer.Services;

namespace Mostlylucid.DocSummarizer.Tests.Services;

/// <summary>
/// Integration tests for OllamaService - requires Ollama to be running locally
/// These tests are marked with the IntegrationTest trait and can be skipped in CI
/// </summary>
public class OllamaServiceTests
{
    private const string TestCategory = "IntegrationTest";

    [Fact(Skip = "Integration test - requires Ollama running locally")]
    [Trait("Category", TestCategory)]
    public async Task GenerateAsync_WithValidPrompt_ReturnsResponse()
    {
        // Arrange
        var ollama = new OllamaService("llama3.2:3b");
        var prompt = "Say 'Hello, World!' and nothing else.";

        // Act
        var response = await ollama.GenerateAsync(prompt);

        // Assert
        Assert.NotNull(response);
        Assert.NotEmpty(response);
        Assert.Contains("Hello", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Skip = "Integration test - requires Ollama running locally")]
    [Trait("Category", TestCategory)]
    public async Task EmbedAsync_WithValidText_ReturnsEmbedding()
    {
        // Arrange
        var ollama = new OllamaService("llama3.2:3b", "nomic-embed-text");
        var text = "This is a test sentence for embedding.";

        // Act
        var embedding = await ollama.EmbedAsync(text);

        // Assert
        Assert.NotNull(embedding);
        Assert.NotEmpty(embedding);
        Assert.True(embedding.Length > 0);
    }

    [Fact(Skip = "Integration test - requires Ollama running locally")]
    [Trait("Category", TestCategory)]
    public async Task IsAvailableAsync_WithRunningServer_ReturnsTrue()
    {
        // Arrange
        var ollama = new OllamaService();

        // Act
        var available = await ollama.IsAvailableAsync();

        // Assert
        Assert.True(available);
    }

    [Fact]
    [Trait("Category", TestCategory)]
    public async Task IsAvailableAsync_WithUnavailableServer_ReturnsFalse()
    {
        // Arrange
        // Use a valid port that is unlikely to have a service running
        var ollama = new OllamaService(baseUrl: "http://localhost:11435");

        // Act
        var available = await ollama.IsAvailableAsync();

        // Assert
        Assert.False(available);
    }
}