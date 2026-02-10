using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Services;

/// <summary>
///     Per-host rate limiter for web crawling and GitHub API access.
/// </summary>
public class CrawlRateLimiter
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostSemaphores = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hostLastRequest = new();
    private readonly int _defaultDelayMs;
    private readonly Dictionary<string, int> _hostOverrides;
    private readonly ILogger<CrawlRateLimiter> _logger;

    // GitHub rate limit tracking
    private int _gitHubRemainingRequests = -1; // -1 = unknown
    private DateTimeOffset _gitHubResetTime = DateTimeOffset.MinValue;
    private readonly int _gitHubBackoffThreshold;
    private readonly SemaphoreSlim _gitHubSemaphore = new(1, 1);

    public CrawlRateLimiter(IConfiguration configuration, ILogger<CrawlRateLimiter> logger)
    {
        _logger = logger;

        var section = configuration.GetSection("BackgroundIndexer:RateLimiting");
        _defaultDelayMs = section.GetValue("DefaultDelayMs", 1000);
        _gitHubBackoffThreshold = section.GetValue("GitHub:BackoffThreshold", 10);

        _hostOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var overrides = section.GetSection("HostOverrides");
        foreach (var child in overrides.GetChildren())
        {
            if (int.TryParse(child.Value, out var delay))
                _hostOverrides[child.Key] = delay;
        }
    }

    /// <summary>
    ///     Wait for a rate limit slot for the given host.
    /// </summary>
    public async Task WaitForSlotAsync(string host, CancellationToken ct)
    {
        var semaphore = _hostSemaphores.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);

        try
        {
            if (_hostLastRequest.TryGetValue(host, out var lastRequest))
            {
                var delayMs = _hostOverrides.GetValueOrDefault(host, _defaultDelayMs);
                var elapsed = (DateTimeOffset.UtcNow - lastRequest).TotalMilliseconds;
                if (elapsed < delayMs)
                {
                    var waitMs = (int)(delayMs - elapsed);
                    _logger.LogDebug("Rate limiting: waiting {WaitMs}ms for host {Host}", waitMs, host);
                    await Task.Delay(waitMs, ct);
                }
            }

            _hostLastRequest[host] = DateTimeOffset.UtcNow;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    ///     Wait for a GitHub API rate limit slot. Backs off when remaining requests are low.
    /// </summary>
    public async Task WaitForGitHubSlotAsync(CancellationToken ct)
    {
        await _gitHubSemaphore.WaitAsync(ct);
        try
        {
            // If we know remaining requests are below threshold, wait until reset
            if (_gitHubRemainingRequests >= 0 && _gitHubRemainingRequests <= _gitHubBackoffThreshold)
            {
                var waitTime = _gitHubResetTime - DateTimeOffset.UtcNow;
                if (waitTime > TimeSpan.Zero)
                {
                    _logger.LogWarning(
                        "GitHub rate limit low ({Remaining} remaining), backing off for {Seconds}s",
                        _gitHubRemainingRequests, waitTime.TotalSeconds);
                    await Task.Delay(waitTime, ct);
                }
            }

            // Also apply standard per-host delay
            await WaitForSlotAsync("api.github.com", ct);
        }
        finally
        {
            _gitHubSemaphore.Release();
        }
    }

    /// <summary>
    ///     Record GitHub rate limit headers from a response.
    /// </summary>
    public void RecordGitHubResponse(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues))
        {
            if (int.TryParse(remainingValues.FirstOrDefault(), out var remaining))
                _gitHubRemainingRequests = remaining;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
        {
            if (long.TryParse(resetValues.FirstOrDefault(), out var resetUnix))
                _gitHubResetTime = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
        }
    }
}
