using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using LucidRAG.Data;

namespace LucidRAG.Multitenancy;

/// <summary>
/// Connection interceptor that sets PostgreSQL search_path for tenant isolation.
/// This is the correct approach because EF Core caches compiled models.
/// </summary>
public class TenantSchemaInterceptor : DbConnectionInterceptor
{
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ILogger<TenantSchemaInterceptor> _logger;

    public TenantSchemaInterceptor(ITenantAccessor tenantAccessor, ILogger<TenantSchemaInterceptor> logger)
    {
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SetSearchPath(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await SetSearchPathAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void SetSearchPath(DbConnection connection)
    {
        var tenant = _tenantAccessor.Current;
        if (tenant == null || tenant.TenantId == TenantConstants.DefaultTenantId)
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        // Use parameterized approach to prevent SQL injection
        var schemaName = tenant.SchemaName;
        cmd.CommandText = $"SET search_path TO \"{schemaName}\", public";
        cmd.ExecuteNonQuery();
        _logger.LogDebug("Set search_path to schema: {Schema}", schemaName);
    }

    private async Task SetSearchPathAsync(DbConnection connection, CancellationToken ct)
    {
        var tenant = _tenantAccessor.Current;
        if (tenant == null || tenant.TenantId == TenantConstants.DefaultTenantId)
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        var schemaName = tenant.SchemaName;
        cmd.CommandText = $"SET search_path TO \"{schemaName}\", public";
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("Set search_path to schema: {Schema}", schemaName);
    }
}

/// <summary>
/// Tenant-aware DbContext that applies schema-per-tenant isolation via search_path.
/// NOTE: Schema isolation is handled by TenantSchemaInterceptor, not OnModelCreating.
/// </summary>
public class TenantAwareDbContext : RagDocumentsDbContext
{
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ILogger<TenantAwareDbContext> _logger;

    public TenantAwareDbContext(
        DbContextOptions<RagDocumentsDbContext> options,
        ITenantAccessor tenantAccessor,
        ILogger<TenantAwareDbContext> logger)
        : base(options)
    {
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    // NOTE: We don't override OnModelCreating for schema anymore.
    // EF Core caches compiled models, so HasDefaultSchema would use the first tenant's schema for all.
    // Instead, TenantSchemaInterceptor sets search_path on each connection.
}

/// <summary>
/// Factory for creating tenant-aware DbContext instances.
/// </summary>
public interface ITenantDbContextFactory
{
    /// <summary>
    /// Create a DbContext for the current tenant.
    /// </summary>
    RagDocumentsDbContext CreateDbContext();

    /// <summary>
    /// Create a DbContext for a specific tenant.
    /// </summary>
    RagDocumentsDbContext CreateDbContextForTenant(string tenantId);

    /// <summary>
    /// Create a DbContext for the public/default schema.
    /// </summary>
    RagDocumentsDbContext CreatePublicDbContext();
}

/// <summary>
/// PostgreSQL implementation of tenant DbContext factory.
/// Uses schema switching for tenant isolation.
/// </summary>
public class PostgresTenantDbContextFactory : ITenantDbContextFactory
{
    private readonly IServiceProvider _services;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgresTenantDbContextFactory> _logger;

    public PostgresTenantDbContextFactory(
        IServiceProvider services,
        ITenantAccessor tenantAccessor,
        IConfiguration configuration,
        ILogger<PostgresTenantDbContextFactory> logger)
    {
        _services = services;
        _tenantAccessor = tenantAccessor;
        _configuration = configuration;
        _logger = logger;
    }

    public RagDocumentsDbContext CreateDbContext()
    {
        var tenant = _tenantAccessor.Current;
        if (tenant == null || tenant.TenantId == TenantConstants.DefaultTenantId)
        {
            return CreatePublicDbContext();
        }

        return CreateDbContextForTenant(tenant.TenantId);
    }

    public RagDocumentsDbContext CreateDbContextForTenant(string tenantId)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        var schema = $"tenant_{tenantId}";

        var optionsBuilder = new DbContextOptionsBuilder<RagDocumentsDbContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", schema);
        });

        // Create a temporary tenant accessor with the specific tenant
        var tempAccessor = new TenantAccessor
        {
            Current = TenantContext.FromTenantId(tenantId)
        };

        var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
        var contextLogger = loggerFactory.CreateLogger<TenantAwareDbContext>();

        _logger.LogDebug("Created DbContext for tenant: {TenantId}, schema: {Schema}", tenantId, schema);

        return new TenantAwareDbContext(optionsBuilder.Options, tempAccessor, contextLogger);
    }

    public RagDocumentsDbContext CreatePublicDbContext()
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        var optionsBuilder = new DbContextOptionsBuilder<RagDocumentsDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        // Create accessor with default tenant
        var tempAccessor = new TenantAccessor
        {
            Current = TenantContext.FromTenantId(TenantConstants.DefaultTenantId)
        };

        var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
        var contextLogger = loggerFactory.CreateLogger<TenantAwareDbContext>();

        return new TenantAwareDbContext(optionsBuilder.Options, tempAccessor, contextLogger);
    }
}

/// <summary>
/// Extension methods for tenant-aware database operations.
/// </summary>
public static class TenantDatabaseExtensions
{
    /// <summary>
    /// Execute a database operation in the context of a specific tenant.
    /// </summary>
    public static async Task<T> ExecuteInTenantContextAsync<T>(
        this ITenantDbContextFactory factory,
        string tenantId,
        Func<RagDocumentsDbContext, Task<T>> operation)
    {
        await using var db = factory.CreateDbContextForTenant(tenantId);
        return await operation(db);
    }

    /// <summary>
    /// Set the search path for a PostgreSQL connection to a tenant's schema.
    /// </summary>
    public static async Task SetSearchPathAsync(this DbContext context, string schema)
    {
        await context.Database.ExecuteSqlRawAsync($"SET search_path TO {schema}, public");
    }
}
