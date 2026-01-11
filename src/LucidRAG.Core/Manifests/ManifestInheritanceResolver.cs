using Microsoft.Extensions.Logging;
using System.Reflection;

namespace LucidRAG.Manifests;

/// <summary>
/// Resolves YAML manifest inheritance by merging parent and child manifests.
/// Supports deep merging of nested objects and collections.
/// </summary>
public sealed class ManifestInheritanceResolver<TManifest> where TManifest : class
{
    private readonly IManifestLoader<TManifest> _loader;
    private readonly ILogger _logger;
    private readonly HashSet<string> _resolutionStack = new();

    public ManifestInheritanceResolver(
        IManifestLoader<TManifest> loader,
        ILogger logger)
    {
        _loader = loader;
        _logger = logger;
    }

    /// <summary>
    /// Resolves inheritance for a manifest by loading parent and merging properties.
    /// Returns the fully resolved manifest with all inherited properties.
    /// </summary>
    public async Task<TManifest> ResolveInheritanceAsync(
        TManifest manifest,
        CancellationToken ct = default)
    {
        // Check if this manifest inherits from another
        var inheritsProperty = typeof(TManifest).GetProperty("Inherits");
        if (inheritsProperty == null)
            return manifest;  // No inheritance support

        var inheritsValue = inheritsProperty.GetValue(manifest) as string;
        if (string.IsNullOrEmpty(inheritsValue))
            return manifest;  // No parent specified

        // Get manifest name for cycle detection
        var nameProperty = typeof(TManifest).GetProperty("Name");
        var manifestName = nameProperty?.GetValue(manifest)?.ToString() ?? "unknown";

        // Detect circular inheritance
        if (_resolutionStack.Contains(manifestName))
        {
            _logger.LogError(
                "Circular inheritance detected: {Name} inherits from {Parent}",
                manifestName,
                inheritsValue);
            throw new InvalidOperationException($"Circular inheritance detected: {manifestName}");
        }

        _resolutionStack.Add(manifestName);

        try
        {
            // Load parent manifest
            var parent = await _loader.LoadByNameAsync(inheritsValue, ct);
            if (parent == null)
            {
                _logger.LogWarning(
                    "Parent manifest '{Parent}' not found for '{Child}'",
                    inheritsValue,
                    manifestName);
                return manifest;  // Return child as-is if parent not found
            }

            // Recursively resolve parent's inheritance
            var resolvedParent = await ResolveInheritanceAsync(parent, ct);

            // Merge parent and child
            var merged = MergeManifests(resolvedParent, manifest);

            _logger.LogDebug(
                "Resolved inheritance: {Child} inherits from {Parent}",
                manifestName,
                inheritsValue);

            return merged;
        }
        finally
        {
            _resolutionStack.Remove(manifestName);
        }
    }

    /// <summary>
    /// Merges parent and child manifests.
    /// Child properties override parent properties.
    /// </summary>
    private TManifest MergeManifests(TManifest parent, TManifest child)
    {
        // Create a shallow copy of the parent as the base
        var merged = CloneManifest(parent);

        // Override with child properties (non-null, non-default values)
        foreach (var property in typeof(TManifest).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || !property.CanRead)
                continue;

            // Skip "Inherits" property (don't copy inheritance chain to result)
            if (property.Name == "Inherits")
                continue;

            var childValue = property.GetValue(child);
            if (childValue == null)
                continue;  // Skip null values (keep parent value)

            // For strings, check if empty
            if (childValue is string str && string.IsNullOrEmpty(str))
                continue;  // Skip empty strings (keep parent value)

            // For collections, merge or replace based on type
            if (childValue is System.Collections.IDictionary childDict)
            {
                var parentValue = property.GetValue(merged);
                if (parentValue is System.Collections.IDictionary parentDict)
                {
                    // Merge dictionaries
                    MergeDictionaries(parentDict, childDict);
                    continue;
                }
            }

            // For simple types and non-mergeable collections, override
            property.SetValue(merged, childValue);
        }

        return merged;
    }

    /// <summary>
    /// Creates a shallow copy of a manifest.
    /// </summary>
    private TManifest CloneManifest(TManifest source)
    {
        var clone = Activator.CreateInstance<TManifest>();

        foreach (var property in typeof(TManifest).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || !property.CanRead)
                continue;

            var value = property.GetValue(source);
            property.SetValue(clone, value);
        }

        return clone;
    }

    /// <summary>
    /// Merges two dictionaries, with child values overriding parent values.
    /// </summary>
    private void MergeDictionaries(
        System.Collections.IDictionary parent,
        System.Collections.IDictionary child)
    {
        foreach (var key in child.Keys)
        {
            var childValue = child[key];
            if (childValue != null)
            {
                parent[key] = childValue;  // Override parent value
            }
        }
    }
}
