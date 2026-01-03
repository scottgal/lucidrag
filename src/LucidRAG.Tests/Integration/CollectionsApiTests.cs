using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace LucidRAG.Tests.Integration;

/// <summary>
/// Integration tests for the Collections API
/// </summary>
[Collection("Integration")]
public class CollectionsApiTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CollectionsApiTests(TestWebApplicationFactory factory)
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
    public async Task CreateCollection_ValidRequest_ReturnsCreated()
    {
        // Arrange
        var request = new
        {
            name = "Test Collection",
            description = "A test collection for documents"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/collections", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        result.GetProperty("name").GetString().Should().Be("Test Collection");
        result.GetProperty("description").GetString().Should().Be("A test collection for documents");
    }

    [Fact]
    public async Task CreateCollection_EmptyName_ReturnsBadRequest()
    {
        // Arrange
        var request = new { name = "", description = "No name" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/collections", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("error").GetString().Should().Contain("Name is required");
    }

    [Fact]
    public async Task CreateCollection_DuplicateName_ReturnsConflict()
    {
        // Arrange
        var request = new { name = "Duplicate Collection" };

        // Create first collection
        await _client.PostAsJsonAsync("/api/collections", request);

        // Act - Try to create another with same name
        var response = await _client.PostAsJsonAsync("/api/collections", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("error").GetString().Should().Contain("already exists");
    }

    [Fact]
    public async Task GetCollection_ExistingId_ReturnsCollection()
    {
        // Arrange
        var createRequest = new { name = "Get Test Collection" };
        var createResponse = await _client.PostAsJsonAsync("/api/collections", createRequest);
        var createResult = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var collectionId = createResult.GetProperty("id").GetString();

        // Act
        var response = await _client.GetAsync($"/api/collections/{collectionId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("id").GetString().Should().Be(collectionId);
        result.GetProperty("name").GetString().Should().Be("Get Test Collection");
        result.GetProperty("documentCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetCollection_NonExistent_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync($"/api/collections/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListCollections_ReturnsAllCollections()
    {
        // Arrange - Create multiple collections
        for (int i = 0; i < 3; i++)
        {
            await _client.PostAsJsonAsync("/api/collections", new { name = $"List Test {i}" });
        }

        // Act
        var response = await _client.GetAsync("/api/collections");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var collections = result.GetProperty("collections");
        collections.GetArrayLength().Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task UpdateCollection_ValidRequest_ReturnsUpdated()
    {
        // Arrange
        var createResponse = await _client.PostAsJsonAsync("/api/collections", new { name = "Original Name" });
        var createResult = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var collectionId = createResult.GetProperty("id").GetString();

        var updateRequest = new
        {
            name = "Updated Name",
            description = "Updated description"
        };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/collections/{collectionId}", updateRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("name").GetString().Should().Be("Updated Name");
        result.GetProperty("description").GetString().Should().Be("Updated description");
    }

    [Fact]
    public async Task UpdateCollection_NonExistent_ReturnsNotFound()
    {
        // Arrange
        var updateRequest = new { name = "Doesn't matter" };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/collections/{Guid.NewGuid()}", updateRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteCollection_ExistingId_ReturnsSuccess()
    {
        // Arrange
        var createResponse = await _client.PostAsJsonAsync("/api/collections", new { name = "To Delete" });
        var createResult = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var collectionId = createResult.GetProperty("id").GetString();

        // Act
        var response = await _client.DeleteAsync($"/api/collections/{collectionId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("success").GetBoolean().Should().BeTrue();

        // Verify deletion
        var getResponse = await _client.GetAsync($"/api/collections/{collectionId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteCollection_NonExistent_ReturnsNotFound()
    {
        // Act
        var response = await _client.DeleteAsync($"/api/collections/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddDocumentsToCollection_ValidRequest_ReturnsSuccess()
    {
        // Arrange - Create collection
        var createResponse = await _client.PostAsJsonAsync("/api/collections", new { name = "Docs Collection" });
        var createResult = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var collectionId = createResult.GetProperty("id").GetString();

        // Upload documents
        var docIds = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var content = new MultipartFormDataContent();
            content.Add(new StringContent($"# Doc {i}\n\nContent {i}.", Encoding.UTF8), "file", $"doc-{i}.md");
            var docResponse = await _client.PostAsync("/api/documents/upload", content);
            var docResult = await docResponse.Content.ReadFromJsonAsync<JsonElement>();
            docIds.Add(docResult.GetProperty("documentId").GetString()!);
        }

        var addRequest = new { documentIds = docIds.Select(Guid.Parse).ToArray() };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/collections/{collectionId}/documents", addRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("added").GetInt32().Should().Be(2);

        // Verify via GET
        var getResponse = await _client.GetAsync($"/api/collections/{collectionId}");
        var getResult = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        getResult.GetProperty("documentCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task AddDocumentsToCollection_NonExistentCollection_ReturnsNotFound()
    {
        // Arrange
        var addRequest = new { documentIds = new[] { Guid.NewGuid() } };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/collections/{Guid.NewGuid()}/documents", addRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddDocumentsToCollection_NoValidDocuments_ReturnsBadRequest()
    {
        // Arrange - Create collection
        var createResponse = await _client.PostAsJsonAsync("/api/collections", new { name = "Empty Docs Collection" });
        var createResult = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var collectionId = createResult.GetProperty("id").GetString();

        var addRequest = new { documentIds = new[] { Guid.NewGuid(), Guid.NewGuid() } };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/collections/{collectionId}/documents", addRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RemoveDocumentsFromCollection_ValidRequest_ReturnsSuccess()
    {
        // Arrange - Create collection and add documents
        var createResponse = await _client.PostAsJsonAsync("/api/collections", new { name = "Remove Docs Collection" });
        var createResult = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var collectionId = createResult.GetProperty("id").GetString();

        // Upload and add document
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("# To Remove\n\nContent.", Encoding.UTF8), "file", "to-remove.md");
        var docResponse = await _client.PostAsync("/api/documents/upload", content);
        var docResult = await docResponse.Content.ReadFromJsonAsync<JsonElement>();
        var docId = docResult.GetProperty("documentId").GetString()!;

        await _client.PostAsJsonAsync($"/api/collections/{collectionId}/documents",
            new { documentIds = new[] { Guid.Parse(docId) } });

        // Act - Remove document
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/collections/{collectionId}/documents")
        {
            Content = JsonContent.Create(new { documentIds = new[] { Guid.Parse(docId) } })
        };
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("removed").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ListCollections_IncludesStats()
    {
        // Arrange - Create collection with documents
        var createResponse = await _client.PostAsJsonAsync("/api/collections", new { name = "Stats Collection" });
        var createResult = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var collectionId = createResult.GetProperty("id").GetString();

        var content = new MultipartFormDataContent();
        content.Add(new StringContent("# Stats Doc\n\nContent for stats.", Encoding.UTF8), "file", "stats-doc.md");
        var docResponse = await _client.PostAsync("/api/documents/upload", content);
        var docResult = await docResponse.Content.ReadFromJsonAsync<JsonElement>();
        var docId = docResult.GetProperty("documentId").GetString()!;

        await _client.PostAsJsonAsync($"/api/collections/{collectionId}/documents",
            new { documentIds = new[] { Guid.Parse(docId) } });

        // Act
        var response = await _client.GetAsync("/api/collections");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var collections = result.GetProperty("collections");

        var statsCollection = collections.EnumerateArray()
            .FirstOrDefault(c => c.GetProperty("name").GetString() == "Stats Collection");

        statsCollection.GetProperty("documentCount").GetInt32().Should().Be(1);
    }
}
