using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mostlylucid.DocSummarizer.Services.Onnx;

/// <summary>
/// Unified tokenizer that supports WordPiece, BPE, and Unigram models
/// by parsing HuggingFace's tokenizer.json format.
/// </summary>
public class HuggingFaceTokenizer
{
    private readonly TokenizerConfig _config;
    private readonly Dictionary<string, int> _vocab;
    private readonly ITokenizerModel _model;
    private readonly PreTokenizer? _preTokenizer;
    private readonly Normalizer? _normalizer;
    
    // Special token IDs
    public int ClsTokenId { get; }
    public int SepTokenId { get; }
    public int PadTokenId { get; }
    public int UnkTokenId { get; }
    
    public TokenizerType Type => _model.Type;

    private HuggingFaceTokenizer(
        TokenizerConfig config,
        Dictionary<string, int> vocab,
        ITokenizerModel model,
        PreTokenizer? preTokenizer,
        Normalizer? normalizer)
    {
        _config = config;
        _vocab = vocab;
        _model = model;
        _preTokenizer = preTokenizer;
        _normalizer = normalizer;
        
        // Resolve special tokens
        ClsTokenId = ResolveSpecialToken("[CLS]", "cls_token", 101);
        SepTokenId = ResolveSpecialToken("[SEP]", "sep_token", 102);
        PadTokenId = ResolveSpecialToken("[PAD]", "pad_token", 0);
        UnkTokenId = ResolveSpecialToken("[UNK]", "unk_token", 100);
    }

    /// <summary>
    /// Load tokenizer from tokenizer.json file
    /// </summary>
    public static HuggingFaceTokenizer FromFile(string tokenizerJsonPath)
    {
        var json = File.ReadAllText(tokenizerJsonPath);
        return FromJson(json);
    }

    /// <summary>
    /// Load tokenizer from JSON string
    /// </summary>
    public static HuggingFaceTokenizer FromJson(string json)
    {
        var config = JsonSerializer.Deserialize<TokenizerConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse tokenizer.json");

        // Build vocabulary
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        if (config.Model?.Vocab != null)
        {
            foreach (var kvp in config.Model.Vocab)
            {
                vocab[kvp.Key] = kvp.Value;
            }
        }

        // Create the appropriate tokenizer model
        ITokenizerModel model = config.Model?.Type?.ToLowerInvariant() switch
        {
            "wordpiece" => new WordPieceModel(vocab, config.Model),
            "bpe" => new BpeModel(vocab, config.Model),
            "unigram" => new UnigramModel(vocab, config.Model),
            _ => throw new NotSupportedException($"Tokenizer type '{config.Model?.Type}' is not supported")
        };

        // Create pre-tokenizer if specified
        PreTokenizer? preTokenizer = config.PreTokenizer?.Type?.ToLowerInvariant() switch
        {
            "whitespace" => new WhitespacePreTokenizer(),
            "berttokenizer" or "bert" => new BertPreTokenizer(),
            "metaspace" => new MetaspacePreTokenizer(config.PreTokenizer),
            "bytlevel" or "bytelevel" => new ByteLevelPreTokenizer(),
            "sequence" => CreateSequencePreTokenizer(config.PreTokenizer),
            _ => null
        };

        // Create normalizer if specified
        Normalizer? normalizer = config.Normalizer?.Type?.ToLowerInvariant() switch
        {
            "bertnormalizer" or "bert" => new BertNormalizer(config.Normalizer),
            "lowercase" => new LowercaseNormalizer(),
            "nfc" => new NfcNormalizer(),
            "nfkc" => new NfkcNormalizer(),
            "sequence" => CreateSequenceNormalizer(config.Normalizer),
            _ => null
        };

        return new HuggingFaceTokenizer(config, vocab, model, preTokenizer, normalizer);
    }

    /// <summary>
    /// Create a fallback tokenizer from vocab.txt (legacy WordPiece format)
    /// </summary>
    public static HuggingFaceTokenizer FromVocabFile(string vocabPath)
    {
        var vocab = File.ReadAllLines(vocabPath)
            .Select((word, index) => (word, index))
            .ToDictionary(x => x.word, x => x.index, StringComparer.Ordinal);

        var model = new WordPieceModel(vocab, null);
        var config = new TokenizerConfig();

        return new HuggingFaceTokenizer(config, vocab, model, new BertPreTokenizer(), new BertNormalizer(null));
    }

    /// <summary>
    /// Encode text to token IDs with attention mask
    /// </summary>
    public (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Encode(string text, int maxLength)
    {
        // Normalize
        var normalized = _normalizer?.Normalize(text) ?? text;

        // Pre-tokenize
        var preTokens = _preTokenizer?.PreTokenize(normalized) ?? new[] { normalized };

        // Tokenize each pre-token
        var tokens = new List<string>();
        foreach (var preToken in preTokens)
        {
            tokens.AddRange(_model.Tokenize(preToken));
        }

        // Truncate to fit special tokens
        if (tokens.Count > maxLength - 2)
            tokens = tokens.Take(maxLength - 2).ToList();

        // Build input IDs with special tokens
        var inputIds = new List<long> { ClsTokenId };
        inputIds.AddRange(tokens.Select(t => (long)GetTokenId(t)));
        inputIds.Add(SepTokenId);

        // Pad to maxLength
        var padCount = maxLength - inputIds.Count;
        inputIds.AddRange(Enumerable.Repeat((long)PadTokenId, padCount));

        var attentionMask = inputIds.Select(id => id != PadTokenId ? 1L : 0L).ToArray();
        var tokenTypeIds = new long[maxLength]; // All zeros for single sentence

        return (inputIds.ToArray(), attentionMask, tokenTypeIds);
    }

    private int GetTokenId(string token) =>
        _vocab.GetValueOrDefault(token, UnkTokenId);

    private int ResolveSpecialToken(string defaultToken, string configKey, int defaultId)
    {
        // Try to find in added_tokens
        var addedToken = _config.AddedTokens?.FirstOrDefault(t => 
            t.Content == defaultToken || 
            t.Content?.Contains(configKey, StringComparison.OrdinalIgnoreCase) == true);
        
        if (addedToken != null && addedToken.Id.HasValue)
            return addedToken.Id.Value;

        // Try vocabulary
        if (_vocab.TryGetValue(defaultToken, out var vocabId))
            return vocabId;

        return defaultId;
    }

    private static PreTokenizer? CreateSequencePreTokenizer(PreTokenizerConfig? config)
    {
        if (config?.PreTokenizers == null) return null;
        var tokenizers = new List<PreTokenizer>();
        foreach (var pt in config.PreTokenizers)
        {
            PreTokenizer? sub = pt.Type?.ToLowerInvariant() switch
            {
                "whitespace" => new WhitespacePreTokenizer(),
                "berttokenizer" or "bert" => new BertPreTokenizer(),
                "metaspace" => new MetaspacePreTokenizer(pt),
                "bytelevel" => new ByteLevelPreTokenizer(),
                _ => null
            };
            if (sub != null) tokenizers.Add(sub);
        }
        return tokenizers.Count > 0 ? new SequencePreTokenizer(tokenizers) : null;
    }

    private static Normalizer? CreateSequenceNormalizer(NormalizerConfig? config)
    {
        if (config?.Normalizers == null) return null;
        var normalizers = new List<Normalizer>();
        foreach (var n in config.Normalizers)
        {
            Normalizer? sub = n.Type?.ToLowerInvariant() switch
            {
                "bertnormalizer" or "bert" => new BertNormalizer(n),
                "lowercase" => new LowercaseNormalizer(),
                "nfc" => new NfcNormalizer(),
                "nfkc" => new NfkcNormalizer(),
                _ => null
            };
            if (sub != null) normalizers.Add(sub);
        }
        return normalizers.Count > 0 ? new SequenceNormalizer(normalizers) : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

#region Tokenizer Type

public enum TokenizerType
{
    WordPiece,
    Bpe,
    Unigram
}

#endregion

#region Tokenizer Models

public interface ITokenizerModel
{
    TokenizerType Type { get; }
    IEnumerable<string> Tokenize(string text);
}

/// <summary>
/// WordPiece tokenizer (BERT-style)
/// </summary>
public class WordPieceModel : ITokenizerModel
{
    private readonly Dictionary<string, int> _vocab;
    private readonly string _continuingSubwordPrefix;
    private readonly string _unkToken;
    private readonly int _maxInputCharsPerWord;

    public TokenizerType Type => TokenizerType.WordPiece;

    public WordPieceModel(Dictionary<string, int> vocab, ModelConfig? config)
    {
        _vocab = vocab;
        _continuingSubwordPrefix = config?.ContinuingSubwordPrefix ?? "##";
        _unkToken = config?.UnkToken ?? "[UNK]";
        _maxInputCharsPerWord = config?.MaxInputCharsPerWord ?? 100;
    }

    public IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        // Check if whole word is in vocab
        if (_vocab.ContainsKey(text))
        {
            yield return text;
            yield break;
        }

        // Too long - return UNK
        if (text.Length > _maxInputCharsPerWord)
        {
            yield return _unkToken;
            yield break;
        }

        // WordPiece greedy longest-match-first
        int start = 0;
        while (start < text.Length)
        {
            int end = text.Length;
            string? curSubstr = null;

            while (start < end)
            {
                var substr = text[start..end];
                if (start > 0)
                    substr = _continuingSubwordPrefix + substr;

                if (_vocab.ContainsKey(substr))
                {
                    curSubstr = substr;
                    break;
                }
                end--;
            }

            if (curSubstr == null)
            {
                yield return _unkToken;
                yield break;
            }

            yield return curSubstr;
            start = end;
        }
    }
}

/// <summary>
/// BPE tokenizer (GPT-style, RoBERTa, etc.)
/// </summary>
public class BpeModel : ITokenizerModel
{
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _merges;
    private readonly string _unkToken;
    private readonly string? _endOfWordSuffix;

    public TokenizerType Type => TokenizerType.Bpe;

    public BpeModel(Dictionary<string, int> vocab, ModelConfig? config)
    {
        _vocab = vocab;
        _unkToken = config?.UnkToken ?? "<unk>";
        _endOfWordSuffix = config?.EndOfWordSuffix;
        
        // Parse merges
        _merges = new Dictionary<(string, string), int>();
        if (config?.Merges != null)
        {
            for (int i = 0; i < config.Merges.Count; i++)
            {
                var parts = config.Merges[i].Split(' ');
                if (parts.Length == 2)
                {
                    _merges[(parts[0], parts[1])] = i;
                }
            }
        }
    }

    public IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        // Start with characters
        var word = text.ToList().Select(c => c.ToString()).ToList();
        
        // Add end-of-word suffix if specified
        if (!string.IsNullOrEmpty(_endOfWordSuffix) && word.Count > 0)
        {
            word[^1] = word[^1] + _endOfWordSuffix;
        }

        // Apply BPE merges
        while (word.Count > 1)
        {
            // Find the highest priority merge
            int bestIdx = -1;
            int bestRank = int.MaxValue;

            for (int i = 0; i < word.Count - 1; i++)
            {
                var pair = (word[i], word[i + 1]);
                if (_merges.TryGetValue(pair, out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    bestIdx = i;
                }
            }

            if (bestIdx == -1)
                break; // No more merges

            // Apply the merge
            var merged = word[bestIdx] + word[bestIdx + 1];
            word[bestIdx] = merged;
            word.RemoveAt(bestIdx + 1);
        }

        // Return tokens, replacing unknown ones
        foreach (var token in word)
        {
            yield return _vocab.ContainsKey(token) ? token : _unkToken;
        }
    }
}

/// <summary>
/// Unigram tokenizer (SentencePiece-style, T5, XLNet, etc.)
/// </summary>
public class UnigramModel : ITokenizerModel
{
    private readonly Dictionary<string, (int Id, float Score)> _pieces;
    private readonly string _unkToken;
    private readonly int _unkId;

    public TokenizerType Type => TokenizerType.Unigram;

    public UnigramModel(Dictionary<string, int> vocab, ModelConfig? config)
    {
        _unkToken = config?.UnkToken ?? "<unk>";
        _unkId = config?.UnkId ?? 0;
        
        // Build pieces with scores
        _pieces = new Dictionary<string, (int, float)>(StringComparer.Ordinal);
        
        // If we have vocab from tokenizer.json, use it
        // Unigram models typically have scores in the vocab
        foreach (var kvp in vocab)
        {
            _pieces[kvp.Key] = (kvp.Value, 0f); // Default score 0
        }
    }

    public IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        // Viterbi-based tokenization (simplified)
        // For production, this would use dynamic programming with scores
        var result = TokenizeViterbi(text);
        foreach (var token in result)
        {
            yield return token;
        }
    }

    private List<string> TokenizeViterbi(string text)
    {
        int n = text.Length;
        var best = new (float Score, int Prev, string Token)[n + 1];
        best[0] = (0f, -1, "");

        for (int i = 1; i <= n; i++)
        {
            best[i] = (float.NegativeInfinity, -1, _unkToken);
            
            // Try all possible previous positions
            for (int j = 0; j < i; j++)
            {
                var substr = text[j..i];
                
                if (_pieces.TryGetValue(substr, out var piece))
                {
                    float score = best[j].Score + piece.Score;
                    if (score > best[i].Score)
                    {
                        best[i] = (score, j, substr);
                    }
                }
                else if (j == i - 1)
                {
                    // Single character fallback
                    float score = best[j].Score - 10f; // Penalty for unknown
                    if (score > best[i].Score)
                    {
                        best[i] = (score, j, substr);
                    }
                }
            }
        }

        // Backtrack to get tokens
        var tokens = new List<string>();
        int pos = n;
        while (pos > 0)
        {
            tokens.Add(best[pos].Token);
            pos = best[pos].Prev;
        }
        tokens.Reverse();
        
        return tokens;
    }
}

#endregion

#region Pre-Tokenizers

public abstract class PreTokenizer
{
    public abstract IEnumerable<string> PreTokenize(string text);
}

public class WhitespacePreTokenizer : PreTokenizer
{
    public override IEnumerable<string> PreTokenize(string text)
    {
        return text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
    }
}

public class BertPreTokenizer : PreTokenizer
{
    private static readonly Regex PunctuationRegex = new(@"([^\w\s])", RegexOptions.Compiled);

    public override IEnumerable<string> PreTokenize(string text)
    {
        // Split on whitespace
        var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var word in words)
        {
            // Split on punctuation while keeping it
            var parts = PunctuationRegex.Split(word);
            foreach (var part in parts)
            {
                if (!string.IsNullOrWhiteSpace(part))
                    yield return part;
            }
        }
    }
}

public class MetaspacePreTokenizer : PreTokenizer
{
    private readonly string _replacement;
    private readonly bool _addPrefixSpace;

    public MetaspacePreTokenizer(PreTokenizerConfig? config)
    {
        _replacement = config?.Replacement ?? "▁";
        _addPrefixSpace = config?.AddPrefixSpace ?? true;
    }

    public override IEnumerable<string> PreTokenize(string text)
    {
        if (_addPrefixSpace && !text.StartsWith(' '))
            text = " " + text;
        
        // Replace spaces with special character
        text = text.Replace(" ", _replacement);
        
        yield return text;
    }
}

public class ByteLevelPreTokenizer : PreTokenizer
{
    public override IEnumerable<string> PreTokenize(string text)
    {
        // GPT-2 style byte-level tokenization
        // Split on whitespace but keep track of leading spaces
        var pattern = new Regex(@"'s|'t|'re|'ve|'m|'ll|'d| ?\w+| ?\d+| ?[^\s\w\d]+|\s+(?!\S)|\s+", RegexOptions.Compiled);
        
        foreach (Match match in pattern.Matches(text))
        {
            yield return match.Value;
        }
    }
}

public class SequencePreTokenizer : PreTokenizer
{
    private readonly List<PreTokenizer> _tokenizers;

    public SequencePreTokenizer(List<PreTokenizer> tokenizers)
    {
        _tokenizers = tokenizers;
    }

    public override IEnumerable<string> PreTokenize(string text)
    {
        IEnumerable<string> current = new[] { text };
        
        foreach (var tokenizer in _tokenizers)
        {
            current = current.SelectMany(t => tokenizer.PreTokenize(t));
        }
        
        return current;
    }
}

#endregion

#region Normalizers

public abstract class Normalizer
{
    public abstract string Normalize(string text);
}

public class BertNormalizer : Normalizer
{
    private readonly bool _lowercase;
    private readonly bool _stripAccents;

    public BertNormalizer(NormalizerConfig? config)
    {
        _lowercase = config?.Lowercase ?? true;
        _stripAccents = config?.StripAccents ?? false;
    }

    public override string Normalize(string text)
    {
        // Clean whitespace
        text = Regex.Replace(text, @"\s+", " ").Trim();
        
        // Handle Chinese characters (add spaces around them)
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (IsChineseChar(c))
            {
                sb.Append(' ').Append(c).Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }
        text = sb.ToString();
        
        if (_lowercase)
            text = text.ToLowerInvariant();
        
        if (_stripAccents)
            text = RemoveAccents(text);
        
        return text;
    }

    private static bool IsChineseChar(char c)
    {
        // CJK Unified Ideographs and related blocks
        return (c >= 0x4E00 && c <= 0x9FFF) ||
               (c >= 0x3400 && c <= 0x4DBF) ||
               (c >= 0x20000 && c <= 0x2A6DF) ||
               (c >= 0x2A700 && c <= 0x2B73F) ||
               (c >= 0x2B740 && c <= 0x2B81F) ||
               (c >= 0x2B820 && c <= 0x2CEAF) ||
               (c >= 0xF900 && c <= 0xFAFF) ||
               (c >= 0x2F800 && c <= 0x2FA1F);
    }

    private static string RemoveAccents(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != 
                System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

public class LowercaseNormalizer : Normalizer
{
    public override string Normalize(string text) => text.ToLowerInvariant();
}

public class NfcNormalizer : Normalizer
{
    public override string Normalize(string text) => text.Normalize(NormalizationForm.FormC);
}

public class NfkcNormalizer : Normalizer
{
    public override string Normalize(string text) => text.Normalize(NormalizationForm.FormKC);
}

public class SequenceNormalizer : Normalizer
{
    private readonly List<Normalizer> _normalizers;

    public SequenceNormalizer(List<Normalizer> normalizers)
    {
        _normalizers = normalizers;
    }

    public override string Normalize(string text)
    {
        foreach (var normalizer in _normalizers)
        {
            text = normalizer.Normalize(text);
        }
        return text;
    }
}

#endregion

#region JSON Config Classes

public class TokenizerConfig
{
    [JsonPropertyName("model")]
    public ModelConfig? Model { get; set; }
    
    [JsonPropertyName("pre_tokenizer")]
    public PreTokenizerConfig? PreTokenizer { get; set; }
    
    [JsonPropertyName("normalizer")]
    public NormalizerConfig? Normalizer { get; set; }
    
    [JsonPropertyName("added_tokens")]
    public List<AddedToken>? AddedTokens { get; set; }
}

public class ModelConfig
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    
    [JsonPropertyName("vocab")]
    public Dictionary<string, int>? Vocab { get; set; }
    
    [JsonPropertyName("merges")]
    public List<string>? Merges { get; set; }
    
    [JsonPropertyName("unk_token")]
    public string? UnkToken { get; set; }
    
    [JsonPropertyName("unk_id")]
    public int? UnkId { get; set; }
    
    [JsonPropertyName("continuing_subword_prefix")]
    public string? ContinuingSubwordPrefix { get; set; }
    
    [JsonPropertyName("end_of_word_suffix")]
    public string? EndOfWordSuffix { get; set; }
    
    [JsonPropertyName("max_input_chars_per_word")]
    public int? MaxInputCharsPerWord { get; set; }
}

public class PreTokenizerConfig
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    
    [JsonPropertyName("replacement")]
    public string? Replacement { get; set; }
    
    [JsonPropertyName("add_prefix_space")]
    public bool? AddPrefixSpace { get; set; }
    
    [JsonPropertyName("pretokenizers")]
    public List<PreTokenizerConfig>? PreTokenizers { get; set; }
}

public class NormalizerConfig
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    
    [JsonPropertyName("lowercase")]
    public bool? Lowercase { get; set; }
    
    [JsonPropertyName("strip_accents")]
    public bool? StripAccents { get; set; }
    
    [JsonPropertyName("normalizers")]
    public List<NormalizerConfig>? Normalizers { get; set; }
}

public class AddedToken
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }
    
    [JsonPropertyName("content")]
    public string? Content { get; set; }
    
    [JsonPropertyName("special")]
    public bool? Special { get; set; }
}

#endregion
