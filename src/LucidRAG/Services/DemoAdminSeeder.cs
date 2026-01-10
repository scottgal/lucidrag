using LucidRAG.Authorization;
using LucidRAG.Identity;
using Microsoft.AspNetCore.Identity;

namespace LucidRAG.Services;

/// <summary>
/// Seeds a demo admin user in development mode for easier testing.
/// The demo user can be used for auto-login in dev environments.
/// </summary>
public class DemoAdminSeeder : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DemoAdminSeeder> _logger;

    public const string DemoAdminEmail = "admin@lucidrag.local";
    public const string DemoAdminPassword = "Admin123!";
    public const string DemoAdminDisplayName = "Demo Admin";

    // All roles to seed
    private static readonly string[] AllRoles =
    [
        Roles.SystemAdmin,
        Roles.TenantAdmin,
        Roles.User
    ];

    public DemoAdminSeeder(
        IServiceProvider serviceProvider,
        IWebHostEnvironment environment,
        ILogger<DemoAdminSeeder> logger)
    {
        _serviceProvider = serviceProvider;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Only seed in development
        if (!_environment.IsDevelopment())
        {
            _logger.LogDebug("Skipping demo admin seeding - not in development mode");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        try
        {
            // Ensure all roles exist
            foreach (var role in AllRoles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    _logger.LogInformation("Creating {Role} role", role);
                    var roleResult = await roleManager.CreateAsync(new IdentityRole(role));
                    if (!roleResult.Succeeded)
                    {
                        _logger.LogWarning("Failed to create {Role} role: {Errors}",
                            role, string.Join(", ", roleResult.Errors.Select(e => e.Description)));
                    }
                }
            }

            // Check if demo admin already exists
            var existingUser = await userManager.FindByEmailAsync(DemoAdminEmail);
            if (existingUser != null)
            {
                _logger.LogDebug("Demo admin user already exists");

                // Ensure they have SystemAdmin role
                if (!await userManager.IsInRoleAsync(existingUser, Roles.SystemAdmin))
                {
                    await userManager.AddToRoleAsync(existingUser, Roles.SystemAdmin);
                    _logger.LogInformation("Added {Role} role to existing demo admin", Roles.SystemAdmin);
                }
                return;
            }

            // Create demo admin user
            var demoUser = new ApplicationUser
            {
                UserName = DemoAdminEmail,
                Email = DemoAdminEmail,
                EmailConfirmed = true,
                DisplayName = DemoAdminDisplayName,
                CreatedAt = DateTimeOffset.UtcNow
            };

            var result = await userManager.CreateAsync(demoUser, DemoAdminPassword);
            if (result.Succeeded)
            {
                // Add SystemAdmin role (includes all permissions)
                await userManager.AddToRoleAsync(demoUser, Roles.SystemAdmin);
                _logger.LogInformation(
                    "Created demo admin user: {Email} with role: {Role}",
                    DemoAdminEmail, Roles.SystemAdmin);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to create demo admin user: {Errors}",
                    string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding demo admin user");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
