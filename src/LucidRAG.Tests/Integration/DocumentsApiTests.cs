using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace LucidRAG.Tests.Integration;

/// <summary>
/// Integration tests for the Documents API
/// </summary>
[Collection("Integration")]
public class DocumentsApiTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DocumentsApiTests(TestWebApplicationFactory factory)
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
    public async Task Upload_ValidMarkdownFile_ReturnsSuccess()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        var fileContent = new StringContent("# Test Document\n\nThis is a test document for RAG.", Encoding.UTF8);
        content.Add(fileContent, "file", "test.md");

        // Act
        var response = await _client.PostAsync("/api/documents/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("documentId").GetString().Should().NotBeNullOrEmpty();
        result.GetProperty("status").GetString().Should().Be("queued");
    }

    [Fact]
    public async Task Upload_InvalidExtension_ReturnsBadRequest()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        var fileContent = new StringContent("some binary data");
        content.Add(fileContent, "file", "test.exe");

        // Act
        var response = await _client.PostAsync("/api/documents/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("error").GetString().Should().Contain("not allowed");
    }

    [Fact]
    public async Task Upload_EmptyFile_ReturnsBadRequest()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        var fileContent = new StringContent("");
        content.Add(fileContent, "file", "empty.md");

        // Act
        var response = await _client.PostAsync("/api/documents/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_DuplicateContent_ReturnsSameDocumentId()
    {
        // Arrange
        var fileContent = "# Duplicate Test\n\nThis content is duplicated.";

        var content1 = new MultipartFormDataContent();
        content1.Add(new StringContent(fileContent, Encoding.UTF8), "file", "test1.md");

        var content2 = new MultipartFormDataContent();
        content2.Add(new StringContent(fileContent, Encoding.UTF8), "file", "test2.md");

        // Act
        var response1 = await _client.PostAsync("/api/documents/upload", content1);
        var response2 = await _client.PostAsync("/api/documents/upload", content2);

        // Assert
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        var result1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        var result2 = await response2.Content.ReadFromJsonAsync<JsonElement>();

        // Same content should return same document ID (deduplication)
        result1.GetProperty("documentId").GetString()
            .Should().Be(result2.GetProperty("documentId").GetString());
    }

    [Fact]
    public async Task GetDocument_ExistingDocument_ReturnsDetails()
    {
        // Arrange - Upload a document first
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("# Get Test\n\nContent here.", Encoding.UTF8), "file", "get-test.md");

        var uploadResponse = await _client.PostAsync("/api/documents/upload", content);
        var uploadResult = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = uploadResult.GetProperty("documentId").GetString();

        // Act
        var response = await _client.GetAsync($"/api/documents/{documentId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("id").GetString().Should().Be(documentId);
        result.GetProperty("name").GetString().Should().Be("get-test");
        result.GetProperty("originalFilename").GetString().Should().Be("get-test.md");
    }

    [Fact]
    public async Task GetDocument_NonExistent_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync($"/api/documents/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListDocuments_ReturnsAllDocuments()
    {
        // Arrange - Upload multiple documents
        for (int i = 0; i < 3; i++)
        {
            var content = new MultipartFormDataContent();
            content.Add(new StringContent($"# Document {i}\n\nContent for document {i}.", Encoding.UTF8),
                "file", $"list-test-{i}.md");
            await _client.PostAsync("/api/documents/upload", content);
        }

        // Act
        var response = await _client.GetAsync("/api/documents");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var documents = result.GetProperty("documents");
        documents.GetArrayLength().Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task DeleteDocument_ExistingDocument_ReturnsSuccess()
    {
        // Arrange - Upload a document first
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("# Delete Test\n\nTo be deleted.", Encoding.UTF8), "file", "delete-test.md");

        var uploadResponse = await _client.PostAsync("/api/documents/upload", content);
        var uploadResult = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = uploadResult.GetProperty("documentId").GetString();

        // Act
        var response = await _client.DeleteAsync($"/api/documents/{documentId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify it's deleted
        var getResponse = await _client.GetAsync($"/api/documents/{documentId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UploadBatch_MultipleFiles_ReturnsAllResults()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        for (int i = 0; i < 3; i++)
        {
            content.Add(new StringContent($"# Batch Doc {i}\n\nBatch content {i}.", Encoding.UTF8),
                "files", $"batch-{i}.md");
        }

        // Act
        var response = await _client.PostAsync("/api/documents/upload-batch", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var documents = result.GetProperty("documents");
        documents.GetArrayLength().Should().Be(3);

        foreach (var doc in documents.EnumerateArray())
        {
            doc.GetProperty("status").GetString().Should().Be("queued");
        }
    }

    [Fact]
    public async Task Upload_LargeMarkdownFile_ProcessesSuccessfully()
    {
        // Arrange - Create a large markdown file
        var sb = new StringBuilder();
        sb.AppendLine("# Large Document Test");
        sb.AppendLine();
        for (int i = 0; i < 100; i++)
        {
            sb.AppendLine($"## Section {i}");
            sb.AppendLine();
            sb.AppendLine($"This is section {i} with some content about topic {i}. " +
                          $"It contains multiple sentences to create meaningful segments. " +
                          $"The RAG system should be able to extract and index this content.");
            sb.AppendLine();
        }

        var content = new MultipartFormDataContent();
        content.Add(new StringContent(sb.ToString(), Encoding.UTF8), "file", "large-test.md");

        // Act
        var response = await _client.PostAsync("/api/documents/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("documentId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Upload_WithCollectionId_AssociatesWithCollection()
    {
        // Arrange - Create a collection first
        var collectionResponse = await _client.PostAsJsonAsync("/api/collections", new { name = "Upload Test Collection" });
        collectionResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var collectionResult = await collectionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var collectionId = collectionResult.GetProperty("id").GetString();

        var content = new MultipartFormDataContent();
        content.Add(new StringContent("# Collection Test\n\nContent.", Encoding.UTF8), "file", "collection-test.md");
        content.Add(new StringContent(collectionId!), "collectionId");

        // Act
        var response = await _client.PostAsync("/api/documents/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = result.GetProperty("documentId").GetString();

        // Verify document is associated with collection
        var docResponse = await _client.GetAsync($"/api/documents/{documentId}");
        var docResult = await docResponse.Content.ReadFromJsonAsync<JsonElement>();
        docResult.GetProperty("collectionId").GetString().Should().Be(collectionId);
    }

    [Fact]
    public async Task Upload_WithInvalidCollectionId_ReturnsError()
    {
        // Arrange - Use a non-existent collection ID
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("# Invalid Collection Test\n\nContent.", Encoding.UTF8), "file", "invalid-collection-test.md");
        content.Add(new StringContent(Guid.NewGuid().ToString()), "collectionId");

        // Act
        var response = await _client.PostAsync("/api/documents/upload", content);

        // Assert - Should fail because FK constraint requires valid collection
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
}
