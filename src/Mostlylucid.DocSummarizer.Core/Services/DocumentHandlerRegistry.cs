using System.Collections.Concurrent;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Default implementation of the document handler registry.
/// Routes file extensions to the highest-priority handler.
/// </summary>
public class DocumentHandlerRegistry : IDocumentHandlerRegistry
{
    private readonly ConcurrentDictionary<string, List<IDocumentHandler>> _handlersByExtension = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDocumentHandler> _allHandlers = [];
    private readonly object _lock = new();

    /// <inheritdoc />
    public IDocumentHandler? GetHandler(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return null;

        // Normalize extension to lowercase with dot
        var normalizedExt = extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";

        if (_handlersByExtension.TryGetValue(normalizedExt, out var handlers) && handlers.Count > 0)
        {
            // Return highest priority handler
            return handlers.OrderByDescending(h => h.Priority).First();
        }

        return null;
    }

    /// <inheritdoc />
    public IDocumentHandler? GetHandlerForFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        var extension = Path.GetExtension(filePath);
        var handler = GetHandler(extension);

        // Additional validation - check if handler explicitly says it can handle
        if (handler != null && !handler.CanHandle(filePath))
        {
            // Try other handlers for this extension
            if (_handlersByExtension.TryGetValue(extension.ToLowerInvariant(), out var handlers))
            {
                handler = handlers
                    .OrderByDescending(h => h.Priority)
                    .FirstOrDefault(h => h.CanHandle(filePath));
            }
        }

        return handler;
    }

    /// <inheritdoc />
    public void Register(IDocumentHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            _allHandlers.Add(handler);

            foreach (var ext in handler.SupportedExtensions)
            {
                var normalizedExt = ext.StartsWith('.') ? ext.ToLowerInvariant() : $".{ext.ToLowerInvariant()}";

                var handlers = _handlersByExtension.GetOrAdd(normalizedExt, _ => []);
                handlers.Add(handler);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IDocumentHandler> GetAllHandlers()
    {
        lock (_lock)
        {
            return _allHandlers.ToList().AsReadOnly();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSupportedExtensions()
    {
        return _handlersByExtension.Keys.OrderBy(e => e).ToList().AsReadOnly();
    }
}
