using Microsoft.EntityFrameworkCore;
using LucidRAG.Data;

namespace LucidRAG.Tests;

/// <summary>
/// Factory for creating test database contexts
/// </summary>
public static class TestDbContextFactory
{
    /// <summary>
    /// Create an in-memory database context for unit testing
    /// </summary>
    public static RagDocumentsDbContext CreateInMemory(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<RagDocumentsDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options;

        var context = new RagDocumentsDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    /// <summary>
    /// Create a real PostgreSQL database context for integration testing
    /// Uses the existing dev database
    /// </summary>
    public static RagDocumentsDbContext CreatePostgres(string connectionString)
    {
        var options = new DbContextOptionsBuilder<RagDocumentsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        var context = new RagDocumentsDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}
