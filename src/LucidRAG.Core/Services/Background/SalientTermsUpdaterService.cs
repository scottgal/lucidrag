using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace LucidRAG.Services.Background;

/// <summary>
/// Background service that periodically updates salient terms for all collections.
/// Runs every 6 hours to refresh autocomplete suggestions.
/// </summary>
public class SalientTermsUpdaterService(
    IServiceProvider services,
    ILogger<SalientTermsUpdaterService> logger) : BackgroundService
{
    private readonly TimeSpan _updateInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Salient terms updater service starting");

        // Wait 30 seconds after startup before first run
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UpdateAllCollectionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error updating salient terms");
            }

            // Wait for next update interval
            await Task.Delay(_updateInterval, stoppingToken);
        }

        logger.LogInformation("Salient terms updater service stopping");
    }

    private async Task UpdateAllCollectionsAsync(CancellationToken ct)
    {
        logger.LogInformation("Starting periodic salient terms update");

        using var scope = services.CreateScope();
        var salientTermsService = scope.ServiceProvider.GetRequiredService<ISalientTermsService>();

        var startTime = DateTimeOffset.UtcNow;

        try
        {
            await salientTermsService.UpdateAllCollectionsAsync(ct);

            var duration = DateTimeOffset.UtcNow - startTime;
            logger.LogInformation(
                "Completed salient terms update in {Duration:F1}s",
                duration.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update salient terms");
        }
    }
}
