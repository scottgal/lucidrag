using System.Collections.Generic;

namespace Mostlylucid.DocSummarizer.Images.Coordination;

/// <summary>
/// Declarative wave manifest loaded from YAML.
/// Defines signal contracts for dynamic composition.
/// </summary>
public sealed class WaveManifest
{
    public required string Name { get; init; }
    public int Priority { get; init; } = 50;
    public bool Enabled { get; init; } = true;

    public SignalScope Scope { get; init; } = new();
    public TriggerConfig Triggers { get; init; } = new();
    public EmissionConfig Emits { get; init; } = new();
    public ListenConfig Listens { get; init; } = new();
    public CacheConfig Cache { get; init; } = new();
    public ConfigBindings Config { get; init; } = new();
    public LaneConfig Lane { get; init; } = new();
    public EscalationConfig? Escalation { get; init; }
    public List<string> Tags { get; init; } = new();
}

public sealed class SignalScope
{
    public string Sink { get; init; } = "docsummarizer.images";
    public string Coordinator { get; init; } = "analysis";
    public string Atom { get; init; } = "";
}

public sealed class TriggerConfig
{
    public List<SignalRequirement> Requires { get; init; } = new();
    public List<string> Signals { get; init; } = new();
    public List<string> SkipWhen { get; init; } = new();
}

public sealed class SignalRequirement
{
    public required string Signal { get; init; }
    public string? Condition { get; init; }
    public object? Value { get; init; }
}

public sealed class EmissionConfig
{
    public List<string> OnStart { get; init; } = new();
    public List<SignalDefinition> OnComplete { get; init; } = new();
    public List<string> OnFailure { get; init; } = new();
    public List<ConditionalSignal> Conditional { get; init; } = new();
}

public class SignalDefinition
{
    public required string Key { get; init; }
    public string Type { get; init; } = "string";
    public string? Description { get; init; }
    public double[]? ConfidenceRange { get; init; }
}

public sealed class ConditionalSignal : SignalDefinition
{
    public string? When { get; init; }
}

public sealed class ListenConfig
{
    public List<string> Required { get; init; } = new();
    public List<string> Optional { get; init; } = new();
}

public sealed class CacheConfig
{
    public List<CacheEntry> Emits { get; init; } = new();
    public List<string> Uses { get; init; } = new();
}

public sealed class CacheEntry
{
    public required string Key { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
}

public sealed class ConfigBindings
{
    public List<ConfigBinding> Bindings { get; init; } = new();
}

public sealed class ConfigBinding
{
    public required string ConfigKey { get; init; }
    public bool SkipIfFalse { get; init; }
}

public sealed class LaneConfig
{
    public string Name { get; init; } = "default";
    public int MaxConcurrency { get; init; } = 4;
    public int Priority { get; init; } = 0;
}

public sealed class EscalationConfig
{
    public EscalationRule? TextExtraction { get; init; }
}

public sealed class EscalationRule
{
    public List<EscalationCondition> When { get; init; } = new();
    public List<EscalationCondition> SkipWhen { get; init; } = new();
}

public sealed class EscalationCondition
{
    public required string Signal { get; init; }
    public object? Value { get; init; }
    public string? Condition { get; init; }
    public string? AndSignal { get; init; }
    public string? AndCondition { get; init; }
}
