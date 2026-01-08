using Mostlylucid.DataSummarizer.Configuration;
using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services.Onnx;

/// <summary>
/// Tiny ONNX-based classifier for fast, local decisions.
/// Uses embedding similarity to predefined labels - no separate classification model needed.
/// Sub-millisecond inference after warmup.
/// 
/// Use cases:
/// - PII detection: "Is this column name/content PII?"
/// - Category ranking: "Order these detected statuses by relevance"
/// - Generation policy: "What synthesis mode should this column use?"
/// - Semantic role: "Is this an ID, measure, category, or target?"
/// </summary>
public class TinyClassifier : IDisposable
{
    private IEmbeddingService? _embeddings;
    private readonly bool _verbose;
    private readonly OnnxConfig? _config;
    
    // Pre-computed label embeddings (cached for fast inference)
    private Dictionary<string, float[]>? _piiLabelEmbeddings;
    private Dictionary<string, float[]>? _roleLabelEmbeddings;
    private Dictionary<string, float[]>? _generationModeLabelEmbeddings;
    
    private bool _initialized;

    public TinyClassifier(bool verbose = false, OnnxConfig? config = null)
    {
        _verbose = verbose;
        _config = config ?? new OnnxConfig { Enabled = true, EmbeddingModel = OnnxEmbeddingModel.ParaphraseMiniLmL3 };
    }

    /// <summary>
    /// Initialize the classifier (downloads model if needed, pre-computes label embeddings)
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        _embeddings = await EmbeddingServiceFactory.GetOrCreateAsync(_config, _verbose);
        await _embeddings.InitializeAsync(ct);

        // Pre-compute label embeddings for fast classification
        await PrecomputeLabelEmbeddingsAsync(ct);
        
        _initialized = true;
        if (_verbose) Console.WriteLine("[TinyClassifier] Initialized with pre-computed label embeddings");
    }

    private async Task PrecomputeLabelEmbeddingsAsync(CancellationToken ct)
    {
        // PII detection labels
        var piiLabels = new Dictionary<string, string>
        {
            ["email"] = "email address, electronic mail, user@domain",
            ["phone"] = "phone number, telephone, mobile, cell",
            ["name"] = "person name, full name, first name, last name, surname",
            ["address"] = "street address, home address, mailing address, location",
            ["ssn"] = "social security number, SSN, national ID, tax ID",
            ["credit_card"] = "credit card number, card number, payment card, CVV",
            ["ip_address"] = "IP address, network address, IPv4, IPv6",
            ["password"] = "password, secret, credential, auth token",
            ["dob"] = "date of birth, birthday, birth date, age",
            ["not_pii"] = "product category, sales amount, order count, status code, identifier"
        };
        
        _piiLabelEmbeddings = await EmbedLabelsAsync(piiLabels, ct);

        // Semantic role labels
        var roleLabels = new Dictionary<string, string>
        {
            ["identifier"] = "unique identifier, primary key, ID, record key, row number",
            ["measure"] = "numeric measurement, amount, quantity, count, sum, total, price, value",
            ["category"] = "category, type, class, group, segment, status, label",
            ["binary_flag"] = "boolean flag, yes no, true false, active inactive, enabled disabled",
            ["target"] = "target variable, outcome, label to predict, dependent variable, churn, converted",
            ["datetime"] = "date, time, timestamp, when, created at, updated at",
            ["free_text"] = "description, comment, notes, free text, remarks, feedback"
        };
        
        _roleLabelEmbeddings = await EmbedLabelsAsync(roleLabels, ct);

        // Generation mode labels
        var genLabels = new Dictionary<string, string>
        {
            ["synthetic"] = "generate synthetic values from statistical distribution, random sampling",
            ["sequential_id"] = "generate sequential identifiers, auto-increment, unique keys",
            ["mask"] = "mask sensitive data, redact PII, anonymize personal information",
            ["faker_pattern"] = "generate realistic fake data, names, addresses, emails using patterns",
            ["copy_safe"] = "safe to copy exact distribution, low cardinality categories, no privacy risk",
            ["exclude"] = "exclude from output, high risk column, should not be synthesized"
        };
        
        _generationModeLabelEmbeddings = await EmbedLabelsAsync(genLabels, ct);
    }

    private async Task<Dictionary<string, float[]>> EmbedLabelsAsync(
        Dictionary<string, string> labels, 
        CancellationToken ct)
    {
        var result = new Dictionary<string, float[]>();
        
        foreach (var (key, description) in labels)
        {
            ct.ThrowIfCancellationRequested();
            var embedding = await _embeddings!.EmbedAsync(description, ct);
            result[key] = embedding;
        }
        
        return result;
    }

    /// <summary>
    /// Classify if a column is likely PII based on name and sample values.
    /// Returns (pii_type, confidence) or ("not_pii", confidence).
    /// </summary>
    public async Task<(string Label, double Confidence)> ClassifyPiiAsync(
        string columnName, 
        IEnumerable<string>? sampleValues = null,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        
        // Build description from column name + samples
        var description = $"column named {columnName}";
        if (sampleValues != null)
        {
            var samples = sampleValues.Take(5).ToList();
            if (samples.Count > 0)
            {
                description += $" with values like: {string.Join(", ", samples)}";
            }
        }
        
        var embedding = await _embeddings!.EmbedAsync(description, ct);
        return FindBestMatch(embedding, _piiLabelEmbeddings!);
    }

    /// <summary>
    /// Classify the semantic role of a column.
    /// </summary>
    public async Task<(SemanticRole Role, double Confidence)> ClassifySemanticRoleAsync(
        ColumnProfile column,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        
        // Build description from column properties
        var description = BuildColumnDescription(column);
        var embedding = await _embeddings!.EmbedAsync(description, ct);
        
        var (label, confidence) = FindBestMatch(embedding, _roleLabelEmbeddings!);
        
        var role = label switch
        {
            "identifier" => SemanticRole.Identifier,
            "measure" => SemanticRole.Measure,
            "category" => SemanticRole.Category,
            "binary_flag" => SemanticRole.BinaryFlag,
            "target" => SemanticRole.Target,
            "free_text" => SemanticRole.FreeText,
            _ => SemanticRole.Unknown
        };
        
        return (role, confidence);
    }

    /// <summary>
    /// Determine the best generation mode for synthesis.
    /// </summary>
    public async Task<(GenerationMode Mode, double Confidence, string Reason)> ClassifyGenerationModeAsync(
        ColumnProfile column,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        
        // Build description from column properties
        var description = BuildColumnDescription(column);
        var embedding = await _embeddings!.EmbedAsync(description, ct);
        
        var (label, confidence) = FindBestMatch(embedding, _generationModeLabelEmbeddings!);
        
        var mode = label switch
        {
            "synthetic" => GenerationMode.Synthetic,
            "sequential_id" => GenerationMode.SequentialId,
            "mask" => GenerationMode.Mask,
            "faker_pattern" => GenerationMode.FakerPattern,
            "copy_safe" => GenerationMode.CopySafe,
            "exclude" => GenerationMode.Exclude,
            _ => GenerationMode.Synthetic
        };
        
        var reason = label switch
        {
            "mask" => "Detected as PII - will mask/redact",
            "faker_pattern" => "Name/address pattern - will use Faker",
            "sequential_id" => "Identifier column - will generate sequential IDs",
            "exclude" => "High risk - excluded from synthesis",
            "copy_safe" => "Low cardinality - safe to copy distribution",
            _ => "Standard synthetic generation"
        };
        
        return (mode, confidence, reason);
    }

    /// <summary>
    /// Rank a list of items by semantic similarity to a query.
    /// Useful for ordering detected statuses, categories, etc.
    /// </summary>
    public async Task<List<(T Item, double Score)>> RankByRelevanceAsync<T>(
        IEnumerable<T> items,
        Func<T, string> getDescription,
        string query,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        
        var queryEmbedding = await _embeddings!.EmbedAsync(query, ct);
        var results = new List<(T Item, double Score)>();
        
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var desc = getDescription(item);
            var itemEmbedding = await _embeddings.EmbedAsync(desc, ct);
            var score = CosineSimilarity(queryEmbedding, itemEmbedding);
            results.Add((item, score));
        }
        
        return results.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>
    /// Batch classify multiple columns efficiently.
    /// </summary>
    public async Task<List<GenerationPolicy>> ClassifyColumnsAsync(
        IEnumerable<ColumnProfile> columns,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        
        var results = new List<GenerationPolicy>();
        
        foreach (var col in columns)
        {
            ct.ThrowIfCancellationRequested();
            
            // Check PII first
            var sampleValues = col.TopValues?.Take(3).Select(v => v.Value);
            var (piiLabel, piiConf) = await ClassifyPiiAsync(col.Name, sampleValues, ct);
            
            if (piiLabel != "not_pii" && piiConf > 0.6)
            {
                results.Add(new GenerationPolicy
                {
                    Mode = piiLabel is "email" or "phone" or "name" or "address" 
                        ? GenerationMode.FakerPattern 
                        : GenerationMode.Mask,
                    Reason = $"PII detected: {piiLabel} (confidence: {piiConf:P0})",
                    SuppressTopValues = true,
                    AutoClassified = true
                });
                continue;
            }
            
            // Otherwise classify generation mode
            var (mode, conf, reason) = await ClassifyGenerationModeAsync(col, ct);
            results.Add(new GenerationPolicy
            {
                Mode = mode,
                Reason = $"{reason} (confidence: {conf:P0})",
                AutoClassified = true,
                KAnonymityThreshold = mode == GenerationMode.CopySafe ? 5 : null
            });
        }
        
        return results;
    }

    private string BuildColumnDescription(ColumnProfile column)
    {
        var parts = new List<string> { $"column named {column.Name}" };
        
        // Type info
        parts.Add($"type {column.InferredType}");
        
        // Cardinality
        if (column.CardinalityRatio > 0.9)
            parts.Add("nearly all unique values");
        else if (column.CardinalityRatio < 0.01)
            parts.Add("very few unique values");
        
        // Numeric stats
        if (column.Mean.HasValue)
            parts.Add($"mean {column.Mean:F1}");
        
        // Top values for categorical
        if (column.TopValues?.Count > 0)
        {
            var topVals = string.Join(", ", column.TopValues.Take(3).Select(v => v.Value));
            parts.Add($"values like {topVals}");
        }
        
        // Text patterns
        if (column.TextPatterns.Count > 0)
        {
            var pattern = column.TextPatterns[0].PatternType.ToString().ToLower();
            parts.Add($"contains {pattern} patterns");
        }
        
        return string.Join(", ", parts);
    }

    private (string Label, double Confidence) FindBestMatch(
        float[] embedding, 
        Dictionary<string, float[]> labelEmbeddings)
    {
        string bestLabel = "";
        double bestScore = double.MinValue;
        
        foreach (var (label, labelEmb) in labelEmbeddings)
        {
            var score = CosineSimilarity(embedding, labelEmb);
            if (score > bestScore)
            {
                bestScore = score;
                bestLabel = label;
            }
        }
        
        // Convert cosine similarity (-1 to 1) to confidence (0 to 1)
        var confidence = (bestScore + 1) / 2;
        
        return (bestLabel, confidence);
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        
        var denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom > 0 ? dot / denom : 0;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("TinyClassifier not initialized. Call InitializeAsync first.");
    }

    public void Dispose()
    {
        // Embedding service is managed by factory, don't dispose
    }
}
