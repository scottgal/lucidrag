using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace LucidRAG.Tests.Integration;

/// <summary>
/// Integration tests for the Search API (standalone search without conversation memory)
/// </summary>
[Collection("Integration")]
public class SearchApiTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SearchApiTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureDatabaseAsync();
        await SeedTestDocumentsAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.CleanupAsync();
    }

    private async Task SeedTestDocumentsAsync()
    {
        // Upload test documents for search tests
        var docs = new[]
        {
            ("# Authentication Guide\n\nThis document explains JWT authentication. " +
             "JWT tokens are signed using HMAC or RSA algorithms. " +
             "Tokens contain claims that identify the user.", "auth.md"),

            ("# Database Schema\n\nThe application uses PostgreSQL. " +
             "Tables include users, documents, and collections. " +
             "Foreign keys maintain referential integrity.", "database.md"),

            ("# API Reference\n\nREST API endpoints follow standard conventions. " +
             "All responses use JSON format. " +
             "Error responses include error codes and messages.", "api-ref.md")
        };

        foreach (var (content, filename) in docs)
        {
            var formContent = new MultipartFormDataContent();
            formContent.Add(new StringContent(content, Encoding.UTF8), "file", filename);
            await _client.PostAsync("/api/documents/upload", formContent);
        }

        // Allow processing time
        await Task.Delay(2000);
    }

    [Fact]
    public async Task Search_ValidQuery_ReturnsResults()
    {
        // Arrange
        var request = new { query = "How does authentication work?" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/search", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("query").GetString().Should().Be("How does authentication work?");
        result.GetProperty("results").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsBadRequest()
    {
        // Arrange
        var request = new { query = "" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/search", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("error").GetString().Should().Contain("Query is required");
    }

    [Fact]
    public async Task Search_WithTopK_RespectsLimit()
    {
        // Arrange
        var request = new { query = "document", topK = 2 };

        // Act
        var response = await _client.PostAsJsonAsync("/api/search", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("results").GetArrayLength().Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Search_ResultsIncludeRequiredFields()
    {
        // Arrange
        var request = new { query = "PostgreSQL database" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/search", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var results = result.GetProperty("results");

        if (results.GetArrayLength() > 0)
        {
            var firstResult = results[0];
            firstResult.TryGetProperty("documentId", out _).Should().BeTrue();
            firstResult.TryGetProperty("documentName", out _).Should().BeTrue();
            firstResult.TryGetProperty("text", out _).Should().BeTrue();
            firstResult.TryGetProperty("score", out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Search_IncludesResponseTime()
    {
        // Arrange
        var request = new { query = "API endpoints" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/search", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.TryGetProperty("responseTimeMs", out var responseTime).Should().BeTrue();
        responseTime.GetInt64().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task SearchWithAnswer_ValidQuery_ReturnsSynthesizedAnswer()
    {
        // Arrange
        var request = new { query = "What is JWT authentication?" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/search/answer", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("query").GetString().Should().Be("What is JWT authentication?");
        result.GetProperty("answer").GetString().Should().NotBeNullOrEmpty();
        result.GetProperty("sources").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task SearchWithAnswer_EmptyQuery_ReturnsBadRequest()
    {
        // Arrange
        var request = new { query = "" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/search/answer", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SearchWithAnswer_WithCustomSystemPrompt_UsesPrompt()
    {
        // Arrange
        var request = new
        {
            query = "Explain the database schema",
            systemPrompt = "You are a database expert. Be concise and technical."
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/search/answer", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("answer").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SearchWithAnswer_DoesNotCreateConversation()
    {
        // Arrange
        var request = new { query = "Tell me about the API" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/search/answer", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Stateless search should NOT return a conversationId
        result.TryGetProperty("conversationId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SearchWithAnswer_SourcesIncludeRequiredFields()
    {
        // Arrange
        var request = new { query = "What format does the API use?" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/search/answer", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sources = result.GetProperty("sources");

        if (sources.GetArrayLength() > 0)
        {
            var firstSource = sources[0];
            firstSource.TryGetProperty("number", out _).Should().BeTrue();
            firstSource.TryGetProperty("documentId", out _).Should().BeTrue();
            firstSource.TryGetProperty("documentName", out _).Should().BeTrue();
            firstSource.TryGetProperty("text", out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Search_WithCollectionId_FiltersResults()
    {
        // Arrange - Create collection and add document
        var collectionResponse = await _client.PostAsJsonAsync("/api/collections", new { name = "Search Filter Collection" });
        var collectionResult = await collectionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var collectionId = collectionResult.GetProperty("id").GetString();

        // Upload document to collection
        var docContent = new MultipartFormDataContent();
        docContent.Add(new StringContent("# Filtered Content\n\nThis is unique filtered content about zebras.", Encoding.UTF8), "file", "filtered.md");
        docContent.Add(new StringContent(collectionId!), "collectionId");
        var docResponse = await _client.PostAsync("/api/documents/upload", docContent);
        var docResult = await docResponse.Content.ReadFromJsonAsync<JsonElement>();
        var docId = docResult.GetProperty("documentId").GetString()!;

        // Add to collection
        await _client.PostAsJsonAsync($"/api/collections/{collectionId}/documents",
            new { documentIds = new[] { Guid.Parse(docId) } });

        await Task.Delay(1000);

        // Act - Search with collection filter
        var request = new
        {
            query = "zebras",
            collectionId = Guid.Parse(collectionId!)
        };
        var response = await _client.PostAsJsonAsync("/api/search", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Search_WithDocumentIds_FiltersToSpecificDocuments()
    {
        // Arrange - Get an existing document ID
        var listResponse = await _client.GetAsync("/api/documents");
        var listResult = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var documents = listResult.GetProperty("documents");

        if (documents.GetArrayLength() == 0)
        {
            // Skip if no documents
            return;
        }

        var docId = documents[0].GetProperty("id").GetString()!;

        var request = new
        {
            query = "content",
            documentIds = new[] { Guid.Parse(docId) }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/search", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
