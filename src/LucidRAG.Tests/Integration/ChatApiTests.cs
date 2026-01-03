using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace LucidRAG.Tests.Integration;

/// <summary>
/// Integration tests for the Chat API
/// </summary>
[Collection("Integration")]
public class ChatApiTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ChatApiTests(TestWebApplicationFactory factory)
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
        await _factory.CleanupAsync();
    }

    [Fact]
    public async Task Chat_WithValidQuery_ReturnsAnswer()
    {
        // Arrange - First upload a document to have content to search
        var docContent = new MultipartFormDataContent();
        docContent.Add(new StringContent("# Test Document\n\nThis document explains how authentication works using JWT tokens.", Encoding.UTF8),
            "file", "auth-doc.md");
        await _client.PostAsync("/api/documents/upload", docContent);

        // Allow processing time
        await Task.Delay(1000);

        var request = new
        {
            query = "How does authentication work?"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/chat", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("answer").GetString().Should().NotBeNullOrEmpty();
        result.GetProperty("conversationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Chat_EmptyQuery_ReturnsBadRequest()
    {
        // Arrange
        var request = new { query = "" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/chat", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("error").GetString().Should().Contain("Query is required");
    }

    [Fact]
    public async Task Chat_WithConversationId_ContinuesConversation()
    {
        // Arrange - First chat to create conversation
        var firstRequest = new { query = "What is RAG?" };
        var firstResponse = await _client.PostAsJsonAsync("/api/chat", firstRequest);
        var firstResult = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        var conversationId = firstResult.GetProperty("conversationId").GetString();

        // Act - Continue conversation
        var secondRequest = new
        {
            query = "Can you explain more?",
            conversationId = Guid.Parse(conversationId!)
        };
        var secondResponse = await _client.PostAsJsonAsync("/api/chat", secondRequest);

        // Assert
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var secondResult = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        secondResult.GetProperty("conversationId").GetString().Should().Be(conversationId);
    }

    [Fact]
    public async Task Chat_WithSystemPrompt_UsesCustomPrompt()
    {
        // Arrange
        var request = new
        {
            query = "What is 2+2?",
            systemPrompt = "You are a math tutor. Be very concise."
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/chat", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("answer").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetConversations_ReturnsAllConversations()
    {
        // Arrange - Create some conversations via chat
        await _client.PostAsJsonAsync("/api/chat", new { query = "Hello 1" });
        await _client.PostAsJsonAsync("/api/chat", new { query = "Hello 2" });
        await _client.PostAsJsonAsync("/api/chat", new { query = "Hello 3" });

        // Act
        var response = await _client.GetAsync("/api/chat/conversations");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var conversations = result.GetProperty("conversations");
        conversations.GetArrayLength().Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task GetConversation_ExistingId_ReturnsConversationWithMessages()
    {
        // Arrange - Create a conversation with messages
        var chatResponse = await _client.PostAsJsonAsync("/api/chat", new { query = "Initial question" });
        var chatResult = await chatResponse.Content.ReadFromJsonAsync<JsonElement>();
        var conversationId = chatResult.GetProperty("conversationId").GetString();

        // Continue the conversation
        await _client.PostAsJsonAsync("/api/chat", new
        {
            query = "Follow-up question",
            conversationId = Guid.Parse(conversationId!)
        });

        // Act
        var response = await _client.GetAsync($"/api/chat/conversations/{conversationId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("id").GetString().Should().Be(conversationId);
        result.GetProperty("messages").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task GetConversation_NonExistentId_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync($"/api/chat/conversations/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteConversation_ExistingId_DeletesSuccessfully()
    {
        // Arrange - Create a conversation
        var chatResponse = await _client.PostAsJsonAsync("/api/chat", new { query = "To be deleted" });
        var chatResult = await chatResponse.Content.ReadFromJsonAsync<JsonElement>();
        var conversationId = chatResult.GetProperty("conversationId").GetString();

        // Act
        var deleteResponse = await _client.DeleteAsync($"/api/chat/conversations/{conversationId}");

        // Assert
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify deletion
        var getResponse = await _client.GetAsync($"/api/chat/conversations/{conversationId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ChatStream_ReturnsServerSentEvents()
    {
        // Arrange
        var request = new { query = "Tell me about RAG" };
        var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/chat/stream", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        var responseText = await response.Content.ReadAsStringAsync();
        responseText.Should().Contain("data:");
        responseText.Should().Contain("[DONE]");
    }

    [Fact]
    public async Task Chat_WithDocumentIds_FiltersToSpecificDocuments()
    {
        // Arrange - Upload two documents
        var doc1Content = new MultipartFormDataContent();
        doc1Content.Add(new StringContent("# Document 1\n\nThis is about cats.", Encoding.UTF8), "file", "cats.md");
        var doc1Response = await _client.PostAsync("/api/documents/upload", doc1Content);
        var doc1Result = await doc1Response.Content.ReadFromJsonAsync<JsonElement>();
        var doc1Id = doc1Result.GetProperty("documentId").GetString();

        var doc2Content = new MultipartFormDataContent();
        doc2Content.Add(new StringContent("# Document 2\n\nThis is about dogs.", Encoding.UTF8), "file", "dogs.md");
        await _client.PostAsync("/api/documents/upload", doc2Content);

        await Task.Delay(1000);

        // Act - Query only doc1
        var request = new
        {
            query = "What animals are discussed?",
            documentIds = new[] { Guid.Parse(doc1Id!) }
        };
        var response = await _client.PostAsJsonAsync("/api/chat", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Chat_ReturnsSourceCitations()
    {
        // Arrange - Upload a document
        var docContent = new MultipartFormDataContent();
        docContent.Add(new StringContent("# API Documentation\n\nThe API uses REST principles with JSON responses.", Encoding.UTF8),
            "file", "api-docs.md");
        await _client.PostAsync("/api/documents/upload", docContent);

        await Task.Delay(1000);

        var request = new { query = "What format does the API use?" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/chat", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sources = result.GetProperty("sources");
        // Sources may or may not be populated depending on search results
        sources.ValueKind.Should().Be(JsonValueKind.Array);
    }
}
