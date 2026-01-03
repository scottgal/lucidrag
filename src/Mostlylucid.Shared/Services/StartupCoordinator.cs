using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Shared.Services;

/// <summary>
/// Singleton service that coordinates startup of background services.
/// Services register themselves, signal when ready, and can wait for others.
/// </summary>
public class StartupCoordinator : IStartupCoordinator
{
    private readonly ConcurrentDictionary<string, bool> _services = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _serviceWaiters = new();
    private readonly TaskCompletionSource<bool> _allReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger<StartupCoordinator> _logger;
    private readonly object _lock = new();
    private bool _allServicesStartedRaised;

    public event EventHandler? AllServicesStarted;

    public StartupCoordinator(ILogger<StartupCoordinator> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void RegisterService(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new ArgumentException("Service name cannot be empty", nameof(serviceName));

        if (_services.TryAdd(serviceName, false))
        {
            _serviceWaiters.TryAdd(serviceName, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            _logger.LogDebug("Registered service for startup coordination: {ServiceName}", serviceName);
        }
    }

    /// <inheritdoc />
    public void SignalReady(string serviceName)
    {
        if (!_services.ContainsKey(serviceName))
        {
            _logger.LogWarning("Service {ServiceName} signaled ready but was not registered", serviceName);
            RegisterService(serviceName);
        }

        _services[serviceName] = true;
        _logger.LogInformation("Service {ServiceName} is ready", serviceName);

        // Signal the waiter for this specific service
        if (_serviceWaiters.TryGetValue(serviceName, out var tcs))
        {
            tcs.TrySetResult(true);
        }

        // Check if all services are now ready
        CheckAllServicesReady();
    }

    /// <inheritdoc />
    public bool IsServiceReady(string serviceName)
    {
        return _services.TryGetValue(serviceName, out var ready) && ready;
    }

    /// <inheritdoc />
    public bool AllServicesReady
    {
        get
        {
            if (_services.IsEmpty) return true;
            return _services.Values.All(ready => ready);
        }
    }

    /// <inheritdoc />
    public async Task WaitForServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        // If service is already ready, return immediately
        if (IsServiceReady(serviceName))
            return;

        // If service is not registered, register a waiter for it
        var tcs = _serviceWaiters.GetOrAdd(serviceName,
            _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        // Check again in case it became ready while we were setting up
        if (IsServiceReady(serviceName))
        {
            tcs.TrySetResult(true);
            return;
        }

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
        await tcs.Task;
    }

    /// <inheritdoc />
    public async Task WaitForAllServicesAsync(CancellationToken cancellationToken = default)
    {
        if (AllServicesReady)
            return;

        using var registration = cancellationToken.Register(() => _allReadyTcs.TrySetCanceled());
        await _allReadyTcs.Task;
    }

    /// <inheritdoc />
    public async Task<bool> WaitForAllServicesAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (AllServicesReady)
            return true;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await WaitForAllServicesAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout occurred
            var pending = GetPendingServices();
            _logger.LogWarning("Timeout waiting for services. Still pending: {PendingServices}",
                string.Join(", ", pending));
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetPendingServices()
    {
        return _services.Where(kvp => !kvp.Value).Select(kvp => kvp.Key).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetReadyServices()
    {
        return _services.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
    }

    private void CheckAllServicesReady()
    {
        if (!AllServicesReady) return;

        lock (_lock)
        {
            if (_allServicesStartedRaised) return;
            _allServicesStartedRaised = true;
        }

        _logger.LogInformation("All {Count} registered services are ready: {Services}",
            _services.Count, string.Join(", ", _services.Keys));

        _allReadyTcs.TrySetResult(true);
        AllServicesStarted?.Invoke(this, EventArgs.Empty);
    }
}
