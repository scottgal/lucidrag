namespace Mostlylucid.Shared.Services;

/// <summary>
/// Coordinates startup of background services, allowing services to signal readiness
/// and wait for other services or all services to be ready.
/// </summary>
public interface IStartupCoordinator
{
    /// <summary>
    /// Register a service that will participate in startup coordination.
    /// Call this during service registration (DI setup).
    /// </summary>
    void RegisterService(string serviceName);

    /// <summary>
    /// Signal that a service has completed its startup/initialization.
    /// </summary>
    void SignalReady(string serviceName);

    /// <summary>
    /// Check if a specific service is ready.
    /// </summary>
    bool IsServiceReady(string serviceName);

    /// <summary>
    /// Check if all registered services are ready.
    /// </summary>
    bool AllServicesReady { get; }

    /// <summary>
    /// Wait for a specific service to be ready.
    /// </summary>
    Task WaitForServiceAsync(string serviceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Wait for all registered services to be ready.
    /// </summary>
    Task WaitForAllServicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Wait for all registered services to be ready, with a timeout.
    /// Returns true if all services ready, false if timeout.
    /// </summary>
    Task<bool> WaitForAllServicesAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get list of services that are not yet ready.
    /// </summary>
    IReadOnlyList<string> GetPendingServices();

    /// <summary>
    /// Get list of services that are ready.
    /// </summary>
    IReadOnlyList<string> GetReadyServices();

    /// <summary>
    /// Event raised when all services are ready.
    /// </summary>
    event EventHandler? AllServicesStarted;
}
