using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Services;

namespace LucidRAG.Tests.Services;

public class ConversationServiceTests : IDisposable
{
    private readonly RagDocumentsDbContext _db;
    private readonly ConversationService _service;

    public ConversationServiceTests()
    {
        _db = TestDbContextFactory.CreateInMemory();
        var logger = Mock.Of<ILogger<ConversationService>>();
        _service = new ConversationService(_db, logger);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task CreateConversationAsync_CreatesNewConversation()
    {
        // Act
        var conversation = await _service.CreateConversationAsync();

        // Assert
        conversation.Should().NotBeNull();
        conversation.Id.Should().NotBeEmpty();
        conversation.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreateConversationAsync_WithTitle_SetsTitle()
    {
        // Arrange
        var title = "Test Conversation";

        // Act
        var conversation = await _service.CreateConversationAsync(title: title);

        // Assert
        conversation.Title.Should().Be(title);
    }

    [Fact]
    public async Task CreateConversationAsync_WithCollectionId_SetsCollectionId()
    {
        // Arrange
        var collection = new CollectionEntity { Id = Guid.NewGuid(), Name = "Test Collection" };
        _db.Collections.Add(collection);
        await _db.SaveChangesAsync();

        // Act
        var conversation = await _service.CreateConversationAsync(collectionId: collection.Id);

        // Assert
        conversation.CollectionId.Should().Be(collection.Id);
    }

    [Fact]
    public async Task GetConversationAsync_ReturnsConversationWithMessages()
    {
        // Arrange
        var conversation = await _service.CreateConversationAsync(title: "Test");
        await _service.AddMessageAsync(conversation.Id, "user", "Hello");
        await _service.AddMessageAsync(conversation.Id, "assistant", "Hi there!");

        // Act
        var result = await _service.GetConversationAsync(conversation.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetConversationAsync_NonExistent_ReturnsNull()
    {
        // Act
        var result = await _service.GetConversationAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task AddMessageAsync_AddsMessage()
    {
        // Arrange
        var conversation = await _service.CreateConversationAsync();

        // Act
        var message = await _service.AddMessageAsync(conversation.Id, "user", "Hello world");

        // Assert
        message.Should().NotBeNull();
        message.Role.Should().Be("user");
        message.Content.Should().Be("Hello world");
        message.ConversationId.Should().Be(conversation.Id);
    }

    [Fact]
    public async Task AddMessageAsync_UpdatesConversationTimestamp()
    {
        // Arrange
        var conversation = await _service.CreateConversationAsync();
        var originalUpdatedAt = conversation.UpdatedAt;
        await Task.Delay(100); // Small delay to ensure time difference

        // Act
        await _service.AddMessageAsync(conversation.Id, "user", "Test message");

        // Assert
        var updated = await _service.GetConversationAsync(conversation.Id);
        updated!.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    [Fact]
    public async Task AddMessageAsync_FirstUserMessage_SetsTitle()
    {
        // Arrange
        var conversation = await _service.CreateConversationAsync(); // No title

        // Act
        await _service.AddMessageAsync(conversation.Id, "user", "What is machine learning?");

        // Assert
        var updated = await _service.GetConversationAsync(conversation.Id);
        updated!.Title.Should().Be("What is machine learning?");
    }

    [Fact]
    public async Task AddMessageAsync_LongFirstMessage_TruncatesTitle()
    {
        // Arrange
        var conversation = await _service.CreateConversationAsync();
        var longMessage = new string('a', 100);

        // Act
        await _service.AddMessageAsync(conversation.Id, "user", longMessage);

        // Assert
        var updated = await _service.GetConversationAsync(conversation.Id);
        updated!.Title.Should().HaveLength(50);
        updated.Title.Should().EndWith("...");
    }

    [Fact]
    public async Task BuildContextAsync_ReturnsFormattedHistory()
    {
        // Arrange
        var conversation = await _service.CreateConversationAsync();
        await _service.AddMessageAsync(conversation.Id, "user", "Hello");
        await _service.AddMessageAsync(conversation.Id, "assistant", "Hi!");
        await _service.AddMessageAsync(conversation.Id, "user", "How are you?");

        // Act
        var context = await _service.BuildContextAsync(conversation.Id);

        // Assert
        context.Should().Contain("Previous conversation:");
        context.Should().Contain("user: Hello");
        context.Should().Contain("assistant: Hi!");
        context.Should().Contain("user: How are you?");
    }

    [Fact]
    public async Task BuildContextAsync_EmptyConversation_ReturnsEmpty()
    {
        // Arrange
        var conversation = await _service.CreateConversationAsync();

        // Act
        var context = await _service.BuildContextAsync(conversation.Id);

        // Assert
        context.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildContextAsync_RespectsMaxMessages()
    {
        // Arrange
        var conversation = await _service.CreateConversationAsync();
        for (int i = 0; i < 20; i++)
        {
            await _service.AddMessageAsync(conversation.Id, "user", $"Message {i}");
        }

        // Act
        var context = await _service.BuildContextAsync(conversation.Id, maxMessages: 5);

        // Assert
        // Should only contain the last 5 messages
        context.Should().Contain("Message 15");
        context.Should().Contain("Message 19");
        context.Should().NotContain("Message 0");
    }

    [Fact]
    public async Task DeleteConversationAsync_RemovesConversation()
    {
        // Arrange
        var conversation = await _service.CreateConversationAsync();
        await _service.AddMessageAsync(conversation.Id, "user", "Test");

        // Act
        await _service.DeleteConversationAsync(conversation.Id);

        // Assert
        var result = await _service.GetConversationAsync(conversation.Id);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetConversationsAsync_ReturnsAllConversations()
    {
        // Arrange
        await _service.CreateConversationAsync(title: "Conv 1");
        await _service.CreateConversationAsync(title: "Conv 2");
        await _service.CreateConversationAsync(title: "Conv 3");

        // Act
        var conversations = await _service.GetConversationsAsync();

        // Assert
        conversations.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetConversationsAsync_FiltersByCollection()
    {
        // Arrange
        var collection = new CollectionEntity { Id = Guid.NewGuid(), Name = "Test" };
        _db.Collections.Add(collection);
        await _db.SaveChangesAsync();

        await _service.CreateConversationAsync(collectionId: collection.Id, title: "In Collection");
        await _service.CreateConversationAsync(title: "No Collection");

        // Act
        var filtered = await _service.GetConversationsAsync(collectionId: collection.Id);

        // Assert
        filtered.Should().HaveCount(1);
        filtered[0].Title.Should().Be("In Collection");
    }

    [Fact]
    public async Task GetConversationsAsync_OrdersByUpdatedAtDescending()
    {
        // Arrange
        var conv1 = await _service.CreateConversationAsync(title: "First");
        await Task.Delay(50);
        var conv2 = await _service.CreateConversationAsync(title: "Second");
        await Task.Delay(50);
        await _service.AddMessageAsync(conv1.Id, "user", "Update first"); // This updates conv1

        // Act
        var conversations = await _service.GetConversationsAsync();

        // Assert
        conversations[0].Title.Should().Be("First"); // Most recently updated
        conversations[1].Title.Should().Be("Second");
    }
}
