using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace Mostlylucid.GraphRag.Extraction;

/// <summary>
/// ONNX-based Named Entity Recognition service.
/// Uses transformer models (BERT-based) for entity span detection.
///
/// The NER model finds WHERE entities are in the text (spans).
/// Entity TYPE classification is done separately using EntityTypeProfiles.
/// </summary>
public sealed class OnnxNerService : IDisposable
{
    private readonly NerModelInfo _modelInfo;
    private readonly string _modelPath;
    private readonly int _maxSequenceLength;
    private InferenceSession? _session;
    private HuggingFaceTokenizer? _tokenizer;
    private string[]? _labels;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // BIO tag patterns
    private static readonly Regex BioTagRx = new(@"^([BI])-(.+)$", RegexOptions.Compiled);

    public OnnxNerService(string modelPath, NerModelInfo? modelInfo = null, int maxSequenceLength = 512)
    {
        _modelPath = modelPath;
        _modelInfo = modelInfo ?? NerModelRegistry.BertBaseNer;
        _maxSequenceLength = Math.Min(maxSequenceLength, _modelInfo.MaxSequenceLength);
    }

    /// <summary>
    /// Initialize the NER model and tokenizer.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var modelFile = Path.Combine(_modelPath, _modelInfo.ModelFile);
            var tokenizerFile = Path.Combine(_modelPath, _modelInfo.TokenizerFile);
            var configFile = Path.Combine(_modelPath, "config.json");

            if (!File.Exists(modelFile))
                throw new FileNotFoundException($"NER model not found: {modelFile}");

            // Load model
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Environment.ProcessorCount
            };
            _session = new InferenceSession(modelFile, options);

            // Load tokenizer
            _tokenizer = File.Exists(tokenizerFile)
                ? HuggingFaceTokenizer.FromFile(tokenizerFile)
                : throw new FileNotFoundException($"Tokenizer not found: {tokenizerFile}");

            // Load label mapping from config.json
            if (File.Exists(configFile))
            {
                var configJson = await File.ReadAllTextAsync(configFile, ct);
                var config = JsonDocument.Parse(configJson);
                if (config.RootElement.TryGetProperty("id2label", out var id2label))
                {
                    var maxId = id2label.EnumerateObject().Max(p => int.Parse(p.Name));
                    _labels = new string[maxId + 1];
                    foreach (var prop in id2label.EnumerateObject())
                    {
                        _labels[int.Parse(prop.Name)] = prop.Value.GetString() ?? "O";
                    }
                }
            }

            // Fallback to default labels if config doesn't have id2label
            _labels ??= _modelInfo.DefaultLabels;

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Extract entity spans from text.
    /// Returns raw spans with BIO-derived entity types.
    /// </summary>
    public async Task<List<EntitySpan>> ExtractSpansAsync(string text, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        if (_session == null || _tokenizer == null || _labels == null)
            throw new InvalidOperationException("NER model not initialized");

        // Tokenize
        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Encode(text, _maxSequenceLength);
        var tokens = _tokenizer.Decode(inputIds);

        // Create tensors
        var inputIdsTensor = new DenseTensor<long>(inputIds, new[] { 1, inputIds.Length });
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, new[] { 1, attentionMask.Length });
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, new[] { 1, tokenTypeIds.Length });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };

        // Run inference
        using var results = _session.Run(inputs);

        // Get logits output [1, seq_len, num_labels]
        var output = results.First(r => r.Name == "logits" || r.Name == "output_0");
        var logits = output.AsTensor<float>();

        // Convert logits to predictions and spans
        return ExtractEntitiesFromLogits(logits, tokens, text);
    }

    /// <summary>
    /// Extract entities with profile-aware type mapping.
    /// Maps generic NER types to profile-specific types.
    /// </summary>
    public async Task<List<EntityCandidate>> ExtractWithProfileAsync(
        string text,
        EntityProfile profile,
        CancellationToken ct = default)
    {
        var spans = await ExtractSpansAsync(text, ct);

        return spans.Select(s => new EntityCandidate
        {
            Name = s.Text,
            Type = MapToProfileType(s.EntityType, profile),
            Confidence = s.Confidence,
            Signals = ["onnx_ner"]
        }).ToList();
    }

    private List<EntitySpan> ExtractEntitiesFromLogits(Tensor<float> logits, string[] tokens, string originalText)
    {
        var spans = new List<EntitySpan>();
        var dims = logits.Dimensions.ToArray();
        var seqLen = dims[1];
        var numLabels = dims[2];

        EntitySpan? currentEntity = null;
        var currentTokens = new List<string>();

        for (int i = 0; i < seqLen && i < tokens.Length; i++)
        {
            // Skip special tokens
            if (tokens[i] == "[CLS]" || tokens[i] == "[SEP]" || tokens[i] == "[PAD]")
                continue;

            // Get prediction (argmax)
            var maxProb = float.MinValue;
            var maxIdx = 0;
            for (int j = 0; j < numLabels; j++)
            {
                var prob = logits[0, i, j];
                if (prob > maxProb)
                {
                    maxProb = prob;
                    maxIdx = j;
                }
            }

            var label = _labels![maxIdx];
            var confidence = Softmax(logits, i, numLabels, maxIdx);

            // Parse BIO tag
            var match = BioTagRx.Match(label);
            if (match.Success)
            {
                var bioTag = match.Groups[1].Value; // B or I
                var entityType = match.Groups[2].Value; // PER, ORG, LOC, MISC

                if (bioTag == "B")
                {
                    // Save previous entity if any
                    if (currentEntity != null && currentTokens.Count > 0)
                    {
                        currentEntity.Text = MergeTokens(currentTokens);
                        spans.Add(currentEntity);
                    }

                    // Start new entity
                    currentEntity = new EntitySpan
                    {
                        EntityType = entityType,
                        Confidence = confidence
                    };
                    currentTokens = [tokens[i]];
                }
                else if (bioTag == "I" && currentEntity != null)
                {
                    // Continue current entity
                    currentTokens.Add(tokens[i]);
                    currentEntity.Confidence = (currentEntity.Confidence + confidence) / 2;
                }
            }
            else if (label == "O")
            {
                // Outside - save and reset
                if (currentEntity != null && currentTokens.Count > 0)
                {
                    currentEntity.Text = MergeTokens(currentTokens);
                    spans.Add(currentEntity);
                }
                currentEntity = null;
                currentTokens.Clear();
            }
        }

        // Don't forget last entity
        if (currentEntity != null && currentTokens.Count > 0)
        {
            currentEntity.Text = MergeTokens(currentTokens);
            spans.Add(currentEntity);
        }

        // Filter low-confidence and deduplicate
        return spans
            .Where(s => s.Confidence >= 0.5 && !string.IsNullOrWhiteSpace(s.Text))
            .GroupBy(s => s.Text.ToLowerInvariant())
            .Select(g => g.OrderByDescending(s => s.Confidence).First())
            .ToList();
    }

    /// <summary>
    /// Merge WordPiece tokens back to original text.
    /// Handles ## prefixes from BERT tokenization.
    /// </summary>
    private static string MergeTokens(List<string> tokens)
    {
        if (tokens.Count == 0) return "";

        var merged = tokens[0];
        for (int i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.StartsWith("##"))
            {
                merged += token[2..]; // Remove ## and append directly
            }
            else
            {
                merged += " " + token;
            }
        }

        return merged.Trim();
    }

    /// <summary>
    /// Map generic NER type (PER, ORG, LOC, MISC) to profile-specific type.
    /// </summary>
    private static string MapToProfileType(string nerType, EntityProfile profile)
    {
        // Map standard NER types to profile types
        var mappedType = nerType.ToUpperInvariant() switch
        {
            "PER" or "PERSON" => FindBestMatch(profile, ["person", "party", "individual"]),
            "ORG" or "ORGANIZATION" => FindBestMatch(profile, ["organization", "company", "party"]),
            "LOC" or "LOCATION" => FindBestMatch(profile, ["location", "jurisdiction"]),
            "MISC" or "MISCELLANEOUS" => FindBestMatch(profile, ["concept", "technology", "product"]),
            "DATE" or "TIME" => FindBestMatch(profile, ["date"]),
            "MONEY" or "PERCENT" or "QUANTITY" => FindBestMatch(profile, ["amount", "metric"]),
            "PRODUCT" => FindBestMatch(profile, ["product", "technology", "tool"]),
            "EVENT" => FindBestMatch(profile, ["event", "concept"]),
            "LAW" => FindBestMatch(profile, ["clause", "term", "concept"]),
            "LANGUAGE" => FindBestMatch(profile, ["language"]),
            "FAC" or "FACILITY" => FindBestMatch(profile, ["location", "organization"]),
            "GPE" => FindBestMatch(profile, ["location", "jurisdiction"]),
            "NORP" => FindBestMatch(profile, ["organization", "concept"]),
            "WORK_OF_ART" => FindBestMatch(profile, ["product", "concept"]),
            _ => profile.EntityTypes.FirstOrDefault()?.Name ?? "concept"
        };

        return mappedType;
    }

    private static string FindBestMatch(EntityProfile profile, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = profile.EntityTypes.FirstOrDefault(t =>
                t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                t.Aliases.Contains(candidate, StringComparer.OrdinalIgnoreCase));
            if (match != null)
                return match.Name;
        }
        return profile.EntityTypes.FirstOrDefault()?.Name ?? "concept";
    }

    private static float Softmax(Tensor<float> logits, int seqIdx, int numLabels, int targetIdx)
    {
        var maxLogit = float.MinValue;
        for (int j = 0; j < numLabels; j++)
            maxLogit = Math.Max(maxLogit, logits[0, seqIdx, j]);

        var sumExp = 0f;
        for (int j = 0; j < numLabels; j++)
            sumExp += MathF.Exp(logits[0, seqIdx, j] - maxLogit);

        return MathF.Exp(logits[0, seqIdx, targetIdx] - maxLogit) / sumExp;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _initLock.Dispose();
    }
}

/// <summary>
/// Entity span detected by NER model.
/// </summary>
public class EntitySpan
{
    /// <summary>Entity text (merged from tokens).</summary>
    public string Text { get; set; } = "";

    /// <summary>NER entity type (PER, ORG, LOC, MISC, etc.).</summary>
    public string EntityType { get; set; } = "";

    /// <summary>Confidence score (0-1).</summary>
    public double Confidence { get; set; }

    /// <summary>Character offset in original text.</summary>
    public int StartOffset { get; set; }

    /// <summary>Character length in original text.</summary>
    public int Length { get; set; }
}

/// <summary>
/// Registry of available NER ONNX models.
/// </summary>
public static class NerModelRegistry
{
    /// <summary>
    /// dslim/bert-base-NER - English BERT NER model.
    /// Labels: O, B-PER, I-PER, B-ORG, I-ORG, B-LOC, I-LOC, B-MISC, I-MISC
    /// </summary>
    public static readonly NerModelInfo BertBaseNer = new()
    {
        Name = "bert-base-NER",
        HuggingFaceRepo = "dslim/bert-base-NER",
        ModelFile = "model.onnx",
        TokenizerFile = "tokenizer.json",
        MaxSequenceLength = 512,
        SizeBytes = 433_000_000,
        DefaultLabels = ["O", "B-MISC", "I-MISC", "B-PER", "I-PER", "B-ORG", "I-ORG", "B-LOC", "I-LOC"]
    };

    /// <summary>
    /// Multilingual NER model supporting 9 languages.
    /// </summary>
    public static readonly NerModelInfo DistilBertMultilingual = new()
    {
        Name = "distilbert-multilingual-NER",
        HuggingFaceRepo = "Davlan/distilbert-base-multilingual-cased-ner-hrl",
        ModelFile = "model.onnx",
        TokenizerFile = "tokenizer.json",
        MaxSequenceLength = 512,
        SizeBytes = 530_000_000,
        DefaultLabels = ["O", "B-PER", "I-PER", "B-ORG", "I-ORG", "B-LOC", "I-LOC", "B-DATE", "I-DATE"]
    };

    /// <summary>
    /// WikiNEuRal multilingual NER (high quality but non-commercial license).
    /// </summary>
    public static readonly NerModelInfo WikiNeural = new()
    {
        Name = "wikineural-multilingual-ner",
        HuggingFaceRepo = "Babelscape/wikineural-multilingual-ner",
        ModelFile = "model.onnx",
        TokenizerFile = "tokenizer.json",
        MaxSequenceLength = 512,
        SizeBytes = 710_000_000,
        DefaultLabels = ["O", "B-PER", "I-PER", "B-ORG", "I-ORG", "B-LOC", "I-LOC", "B-MISC", "I-MISC"]
    };

    /// <summary>
    /// Get download URL for HuggingFace file.
    /// </summary>
    public static string GetDownloadUrl(string repo, string file) =>
        $"https://huggingface.co/{repo}/resolve/main/{file}";
}

/// <summary>
/// NER model metadata.
/// </summary>
public sealed class NerModelInfo
{
    public required string Name { get; init; }
    public required string HuggingFaceRepo { get; init; }
    public required string ModelFile { get; init; }
    public required string TokenizerFile { get; init; }
    public required int MaxSequenceLength { get; init; }
    public required long SizeBytes { get; init; }
    public required string[] DefaultLabels { get; init; }

    public string GetModelUrl() => NerModelRegistry.GetDownloadUrl(HuggingFaceRepo, ModelFile);
    public string GetTokenizerUrl() => NerModelRegistry.GetDownloadUrl(HuggingFaceRepo, TokenizerFile);
}
