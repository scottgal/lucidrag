using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LucidRAG.Multitenancy;

/// <summary>
/// Service for provisioning and deprovisioning tenant resources.
/// Creates PostgreSQL schemas, runs migrations, and sets up Qdrant collections.
/// </summary>
public interface ITenantProvisioningService
{
    /// <summary>
    /// Provision a new tenant.
    /// Creates schema, runs migrations, creates Qdrant collection.
    /// </summary>
    Task<TenantContext> ProvisionAsync(
        string tenantId,
        string? displayName = null,
        string? contactEmail = null,
        string? plan = null,
        CancellationToken ct = default);

    /// <summary>
    /// Deprovision a tenant.
    /// Drops schema, deletes Qdrant collection, removes tenant record.
    /// </summary>
    Task DeprovisionAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Check if a tenant exists.
    /// </summary>
    Task<bool> ExistsAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Get tenant information.
    /// </summary>
    Task<TenantEntity?> GetTenantAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// List all tenants.
    /// </summary>
    Task<IReadOnlyList<TenantEntity>> ListTenantsAsync(bool? isActive = null, CancellationToken ct = default);

    /// <summary>
    /// Update tenant status.
    /// </summary>
    Task UpdateStatusAsync(string tenantId, bool isActive, CancellationToken ct = default);

    /// <summary>
    /// Run migrations for a specific tenant.
    /// </summary>
    Task MigrateTenantAsync(string tenantId, CancellationToken ct = default);
}

public class TenantProvisioningService : ITenantProvisioningService
{
    private readonly TenantDbContext _tenantDb;
    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantProvisioningService> _logger;

    public TenantProvisioningService(
        TenantDbContext tenantDb,
        ITenantDbContextFactory dbFactory,
        IConfiguration configuration,
        ILogger<TenantProvisioningService> logger)
    {
        _tenantDb = tenantDb;
        _dbFactory = dbFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<TenantContext> ProvisionAsync(
        string tenantId,
        string? displayName = null,
        string? contactEmail = null,
        string? plan = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Provisioning tenant: {TenantId}", tenantId);

        // Create tenant context
        var context = TenantContext.FromTenantId(tenantId, displayName);

        // Check if tenant already exists
        var existing = await _tenantDb.Tenants
            .FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);

        if (existing != null)
        {
            if (existing.IsProvisioned)
            {
                _logger.LogWarning("Tenant already exists and is provisioned: {TenantId}", tenantId);
                throw new InvalidOperationException($"Tenant '{tenantId}' already exists");
            }

            // Resume provisioning for incomplete tenant
            _logger.LogInformation("Resuming provisioning for tenant: {TenantId}", tenantId);
        }
        else
        {
            // Create tenant record
            var tenant = new TenantEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SchemaName = context.SchemaName,
                QdrantCollection = context.QdrantCollection,
                DisplayName = displayName ?? tenantId,
                ContactEmail = contactEmail,
                Plan = plan ?? TenantPlans.Free,
                IsActive = true,
                IsProvisioned = false
            };

            _tenantDb.Tenants.Add(tenant);
            await _tenantDb.SaveChangesAsync(ct);
        }

        try
        {
            // Step 1: Create PostgreSQL schema
            await CreateSchemaAsync(context.SchemaName, ct);
            _logger.LogInformation("Created schema: {Schema}", context.SchemaName);

            // Step 2: Run migrations for the tenant schema
            await MigrateTenantSchemaAsync(context.SchemaName, ct);
            _logger.LogInformation("Migrated schema: {Schema}", context.SchemaName);

            // Step 3: Create Qdrant collection (if Qdrant is configured)
            await CreateQdrantCollectionAsync(context.QdrantCollection, ct);
            _logger.LogInformation("Created Qdrant collection: {Collection}", context.QdrantCollection);

            // Mark tenant as provisioned
            var tenantEntity = await _tenantDb.Tenants.FirstAsync(t => t.TenantId == tenantId, ct);
            tenantEntity.IsProvisioned = true;
            tenantEntity.ProvisionedAt = DateTimeOffset.UtcNow;
            await _tenantDb.SaveChangesAsync(ct);

            _logger.LogInformation("Tenant provisioned successfully: {TenantId}", tenantId);
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision tenant: {TenantId}", tenantId);
            throw;
        }
    }

    public async Task DeprovisionAsync(string tenantId, CancellationToken ct = default)
    {
        _logger.LogInformation("Deprovisioning tenant: {TenantId}", tenantId);

        var tenant = await _tenantDb.Tenants
            .FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);

        if (tenant == null)
        {
            throw new InvalidOperationException($"Tenant '{tenantId}' not found");
        }

        try
        {
            // Step 1: Drop Qdrant collection
            await DropQdrantCollectionAsync(tenant.QdrantCollection, ct);
            _logger.LogInformation("Dropped Qdrant collection: {Collection}", tenant.QdrantCollection);

            // Step 2: Drop PostgreSQL schema (CASCADE to drop all objects)
            await DropSchemaAsync(tenant.SchemaName, ct);
            _logger.LogInformation("Dropped schema: {Schema}", tenant.SchemaName);

            // Step 3: Remove tenant record
            _tenantDb.Tenants.Remove(tenant);
            await _tenantDb.SaveChangesAsync(ct);

            _logger.LogInformation("Tenant deprovisioned successfully: {TenantId}", tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deprovision tenant: {TenantId}", tenantId);
            throw;
        }
    }

    public async Task<bool> ExistsAsync(string tenantId, CancellationToken ct = default)
    {
        return await _tenantDb.Tenants.AnyAsync(t => t.TenantId == tenantId, ct);
    }

    public async Task<TenantEntity?> GetTenantAsync(string tenantId, CancellationToken ct = default)
    {
        return await _tenantDb.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);
    }

    public async Task<IReadOnlyList<TenantEntity>> ListTenantsAsync(bool? isActive = null, CancellationToken ct = default)
    {
        var query = _tenantDb.Tenants.AsNoTracking();

        if (isActive.HasValue)
        {
            query = query.Where(t => t.IsActive == isActive.Value);
        }

        return await query
            .OrderBy(t => t.TenantId)
            .ToListAsync(ct);
    }

    public async Task UpdateStatusAsync(string tenantId, bool isActive, CancellationToken ct = default)
    {
        var tenant = await _tenantDb.Tenants.FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);
        if (tenant == null)
        {
            throw new InvalidOperationException($"Tenant '{tenantId}' not found");
        }

        tenant.IsActive = isActive;
        tenant.UpdatedAt = DateTimeOffset.UtcNow;
        await _tenantDb.SaveChangesAsync(ct);

        _logger.LogInformation("Updated tenant {TenantId} status to: {IsActive}", tenantId, isActive);
    }

    public async Task MigrateTenantAsync(string tenantId, CancellationToken ct = default)
    {
        var tenant = await _tenantDb.Tenants.FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);
        if (tenant == null)
        {
            throw new InvalidOperationException($"Tenant '{tenantId}' not found");
        }

        await MigrateTenantSchemaAsync(tenant.SchemaName, ct);
        _logger.LogInformation("Migrated tenant schema: {TenantId}", tenantId);
    }

    private async Task CreateSchemaAsync(string schemaName, CancellationToken ct)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Create schema if it doesn't exist
        await using var cmd = new NpgsqlCommand(
            $"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\"",
            connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task DropSchemaAsync(string schemaName, CancellationToken ct)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Drop schema with CASCADE to remove all objects
        await using var cmd = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE",
            connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task MigrateTenantSchemaAsync(string schemaName, CancellationToken ct)
    {
        // Get the tenant ID from schema name (tenant_{id})
        var tenantId = schemaName.Replace("tenant_", "");

        await using var db = _dbFactory.CreateDbContextForTenant(tenantId);

        // Set search path and run migrations
        await db.Database.ExecuteSqlRawAsync($"SET search_path TO \"{schemaName}\", public", ct);
        await db.Database.MigrateAsync(ct);
    }

    private async Task CreateQdrantCollectionAsync(string collectionName, CancellationToken ct)
    {
        // TODO: Integrate with Qdrant client to create collection
        // For now, just log the intent
        var qdrantConfig = _configuration.GetSection("DocSummarizer:Qdrant");
        var host = qdrantConfig["Host"];
        var port = qdrantConfig["Port"];
        var vectorSize = qdrantConfig.GetValue<int>("VectorSize", 384);

        if (string.IsNullOrEmpty(host))
        {
            _logger.LogDebug("Qdrant not configured, skipping collection creation");
            return;
        }

        _logger.LogInformation("Would create Qdrant collection: {Collection} (host: {Host}:{Port}, vectors: {Size})",
            collectionName, host, port, vectorSize);

        // Actual Qdrant integration would go here:
        // var client = new QdrantClient(host, port);
        // await client.CreateCollectionAsync(collectionName, vectorSize);
    }

    private async Task DropQdrantCollectionAsync(string collectionName, CancellationToken ct)
    {
        // TODO: Integrate with Qdrant client to delete collection
        _logger.LogInformation("Would drop Qdrant collection: {Collection}", collectionName);

        await Task.CompletedTask;
    }
}
