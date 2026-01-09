using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LucidRAG.Multitenancy;

/// <summary>
/// Design-time factory for TenantDbContext.
/// Used by EF Core tools for migrations.
/// </summary>
public class TenantDbContextDesignFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TenantDbContext>();

        // Use a default connection string for design-time
        // This can be overridden by environment variables or args
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=ragdocs;Username=postgres;Password=";

        optionsBuilder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Tenants", "public");
        });

        return new TenantDbContext(optionsBuilder.Options);
    }
}
