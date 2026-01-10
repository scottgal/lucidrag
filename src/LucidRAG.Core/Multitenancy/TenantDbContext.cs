using Microsoft.EntityFrameworkCore;

namespace LucidRAG.Multitenancy;

/// <summary>
/// DbContext for tenant management.
/// Operates in the public schema, shared across all tenants.
/// </summary>
public class TenantDbContext : DbContext
{
    public TenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
    {
    }

    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Always use public schema for tenant management
        modelBuilder.HasDefaultSchema(TenantConstants.PublicSchema);

        modelBuilder.Entity<TenantEntity>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SchemaName).HasMaxLength(128).IsRequired();
            entity.Property(e => e.QdrantCollection).HasMaxLength(128).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(256);
            entity.Property(e => e.ContactEmail).HasMaxLength(256);
            entity.Property(e => e.Plan).HasMaxLength(32);

            // Unique constraint on tenant ID
            entity.HasIndex(e => e.TenantId).IsUnique();
            entity.HasIndex(e => e.SchemaName).IsUnique();
            entity.HasIndex(e => e.IsActive);
        });
    }
}
