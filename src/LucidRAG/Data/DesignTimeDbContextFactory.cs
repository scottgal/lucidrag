using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LucidRAG.Data;

/// <summary>
/// Design-time factory for creating DbContext during migrations.
/// Uses PostgreSQL for design-time to match production migrations.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RagDocumentsDbContext>
{
    public RagDocumentsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RagDocumentsDbContext>();

        // Use PostgreSQL for migrations (design-time only - doesn't need real connection)
        optionsBuilder.UseNpgsql("Host=localhost;Database=design_time;Username=postgres;Password=unused");

        return new RagDocumentsDbContext(optionsBuilder.Options);
    }
}
