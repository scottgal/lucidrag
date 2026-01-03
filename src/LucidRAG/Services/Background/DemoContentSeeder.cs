using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LucidRAG.Config;
using LucidRAG.Data;

namespace LucidRAG.Services.Background;

/// <summary>
/// Seeds demo content on startup and watches for new files when demo mode is enabled.
/// Drop files into the demo content directory and they'll be processed automatically.
/// </summary>
public class DemoContentSeeder : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RagDocumentsConfig _config;
    private readonly ILogger<DemoContentSeeder> _logger;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, DateTime> _recentlyProcessed = new();
    private readonly string[] _allowedExtensions = [".md", ".txt", ".pdf", ".docx", ".html"];

    public DemoContentSeeder(
        IServiceScopeFactory scopeFactory,
        IOptions<RagDocumentsConfig> config,
        ILogger<DemoContentSeeder> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.DemoMode.Enabled)
        {
            _logger.LogInformation("Demo mode disabled, content seeder inactive");
            return;
        }

        var contentPath = Path.GetFullPath(_config.DemoMode.ContentPath);
        EnsureDirectoryExists(contentPath);

        // Seed existing content on startup
        await SeedExistingContentAsync(contentPath, stoppingToken);

        // Start watching for new files
        StartFileWatcher(contentPath);

        _logger.LogInformation("Demo content watcher active on: {Path}", contentPath);

        // Keep running until cancelled
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private void EnsureDirectoryExists(string contentPath)
    {
        if (!Directory.Exists(contentPath))
        {
            Directory.CreateDirectory(contentPath);
            _logger.LogInformation("Created demo content directory: {Path}", contentPath);
        }
    }

    private async Task SeedExistingContentAsync(string contentPath, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        // Get existing document hashes to avoid re-processing
        var existingHashes = await db.Documents
            .Select(d => d.ContentHash)
            .ToHashSetAsync(ct);

        var files = Directory.GetFiles(contentPath)
            .Where(f => _allowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        if (files.Count == 0)
        {
            _logger.LogInformation("No demo content files found in {Path}. Drop files there to add demo content.", contentPath);
            return;
        }

        _logger.LogInformation("Found {Count} files in demo content directory", files.Count);

        foreach (var filePath in files)
        {
            await ProcessFileAsync(filePath, existingHashes, ct);
        }
    }

    private void StartFileWatcher(string contentPath)
    {
        _watcher = new FileSystemWatcher(contentPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        // Watch for all allowed extensions
        foreach (var ext in _allowedExtensions)
        {
            _watcher.Filters.Add($"*{ext}");
        }

        _watcher.Created += OnFileCreated;
        _watcher.Changed += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;

        _logger.LogInformation("File watcher started for demo content");
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        _ = ProcessFileWithDelayAsync(e.FullPath);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _ = ProcessFileWithDelayAsync(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (_allowedExtensions.Contains(Path.GetExtension(e.FullPath).ToLowerInvariant()))
        {
            _ = ProcessFileWithDelayAsync(e.FullPath);
        }
    }

    private async Task ProcessFileWithDelayAsync(string filePath)
    {
        // Debounce - file events can fire multiple times
        var now = DateTime.UtcNow;
        if (_recentlyProcessed.TryGetValue(filePath, out var lastProcessed))
        {
            if ((now - lastProcessed).TotalSeconds < 5)
            {
                return; // Skip if processed in last 5 seconds
            }
        }
        _recentlyProcessed[filePath] = now;

        // Wait for file to be fully written
        await Task.Delay(1000);

        // Verify file still exists (might have been temp file)
        if (!File.Exists(filePath))
        {
            return;
        }

        _logger.LogInformation("New demo file detected: {FileName}", Path.GetFileName(filePath));

        try
        {
            await ProcessFileAsync(filePath, [], CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process demo file: {FilePath}", filePath);
        }
    }

    private async Task ProcessFileAsync(string filePath, HashSet<string> existingHashes, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var processingService = scope.ServiceProvider.GetRequiredService<IDocumentProcessingService>();

            await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var documentId = await processingService.QueueDocumentAsync(
                fileStream,
                fileName,
                collectionId: null,
                ct);

            _logger.LogInformation("Queued demo document: {FileName} -> {DocumentId}", fileName, documentId);
        }
        catch (Exception ex) when (ex.Message.Contains("already exists"))
        {
            _logger.LogDebug("Demo document already indexed: {FileName}", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process demo document: {FileName}", fileName);
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
