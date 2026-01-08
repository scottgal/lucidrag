using Mostlylucid.DataSummarizer.Services.Onnx;
using Xunit;

namespace Mostlylucid.DataSummarizer.Tests;

/// <summary>
/// Tests for HuggingFaceTokenizer
/// </summary>
public class TokenizerTests
{
    #region WordPiece Model Tests

    [Fact]
    public void WordPiece_TokenizesSimpleWord()
    {
        var vocab = CreateBertVocab();
        var model = new WordPieceModel(vocab, null);

        var tokens = model.Tokenize("hello").ToList();

        Assert.Single(tokens);
        Assert.Equal("hello", tokens[0]);
    }

    [Fact]
    public void WordPiece_TokenizesUnknownWord()
    {
        var vocab = CreateBertVocab();
        var model = new WordPieceModel(vocab, null);

        var tokens = model.Tokenize("xyzabc").ToList();

        Assert.Contains("[UNK]", tokens);
    }

    [Fact]
    public void WordPiece_SplitsIntoSubwords()
    {
        var vocab = new Dictionary<string, int>
        {
            { "[UNK]", 0 },
            { "play", 1 },
            { "##ing", 2 },
            { "##ed", 3 }
        };
        var model = new WordPieceModel(vocab, null);

        var tokens = model.Tokenize("playing").ToList();

        Assert.Equal(2, tokens.Count);
        Assert.Equal("play", tokens[0]);
        Assert.Equal("##ing", tokens[1]);
    }

    [Fact]
    public void WordPiece_HandlesEmptyString()
    {
        var vocab = CreateBertVocab();
        var model = new WordPieceModel(vocab, null);

        var tokens = model.Tokenize("").ToList();

        Assert.Empty(tokens);
    }

    [Fact]
    public void WordPiece_HandlesTooLongWord()
    {
        var vocab = CreateBertVocab();
        var config = new ModelConfig { MaxInputCharsPerWord = 5 };
        var model = new WordPieceModel(vocab, config);

        var tokens = model.Tokenize("verylongword").ToList();

        Assert.Single(tokens);
        Assert.Equal("[UNK]", tokens[0]);
    }

    #endregion

    #region BPE Model Tests

    [Fact]
    public void Bpe_TokenizesSimpleText()
    {
        var vocab = new Dictionary<string, int>
        {
            { "<unk>", 0 },
            { "h", 1 },
            { "e", 2 },
            { "l", 3 },
            { "o", 4 },
            { "he", 5 },
            { "hel", 6 },
            { "hell", 7 },
            { "hello", 8 }
        };
        var config = new ModelConfig
        {
            Merges = new List<string> { "h e", "he l", "hel l", "hell o" }
        };
        var model = new BpeModel(vocab, config);

        var tokens = model.Tokenize("hello").ToList();

        Assert.Single(tokens);
        Assert.Equal("hello", tokens[0]);
    }

    [Fact]
    public void Bpe_HandlesMissingMerges()
    {
        var vocab = new Dictionary<string, int>
        {
            { "<unk>", 0 },
            { "a", 1 },
            { "b", 2 },
            { "c", 3 }
        };
        var model = new BpeModel(vocab, null);

        var tokens = model.Tokenize("abc").ToList();

        Assert.Equal(3, tokens.Count);
    }

    #endregion

    #region PreTokenizer Tests

    [Fact]
    public void WhitespacePreTokenizer_SplitsOnSpaces()
    {
        var preTokenizer = new WhitespacePreTokenizer();

        var tokens = preTokenizer.PreTokenize("hello world test").ToList();

        Assert.Equal(3, tokens.Count);
        Assert.Equal("hello", tokens[0]);
        Assert.Equal("world", tokens[1]);
        Assert.Equal("test", tokens[2]);
    }

    [Fact]
    public void WhitespacePreTokenizer_HandlesTabs()
    {
        var preTokenizer = new WhitespacePreTokenizer();

        var tokens = preTokenizer.PreTokenize("hello\tworld").ToList();

        Assert.Equal(2, tokens.Count);
    }

    [Fact]
    public void WhitespacePreTokenizer_HandlesNewlines()
    {
        var preTokenizer = new WhitespacePreTokenizer();

        var tokens = preTokenizer.PreTokenize("hello\nworld").ToList();

        Assert.Equal(2, tokens.Count);
    }

    [Fact]
    public void BertPreTokenizer_SplitsOnPunctuation()
    {
        var preTokenizer = new BertPreTokenizer();

        var tokens = preTokenizer.PreTokenize("hello, world!").ToList();

        Assert.Contains("hello", tokens);
        Assert.Contains(",", tokens);
        Assert.Contains("world", tokens);
        Assert.Contains("!", tokens);
    }

    [Fact]
    public void BertPreTokenizer_KeepsPunctuation()
    {
        var preTokenizer = new BertPreTokenizer();

        var tokens = preTokenizer.PreTokenize("what's up?").ToList();

        Assert.Contains("'", tokens);
        Assert.Contains("?", tokens);
    }

    [Fact]
    public void MetaspacePreTokenizer_AddsPrefix()
    {
        var config = new PreTokenizerConfig { AddPrefixSpace = true, Replacement = "▁" };
        var preTokenizer = new MetaspacePreTokenizer(config);

        var tokens = preTokenizer.PreTokenize("hello world").ToList();

        Assert.Single(tokens);
        Assert.StartsWith("▁", tokens[0]);
    }

    [Fact]
    public void SequencePreTokenizer_ChainsTokenizers()
    {
        var tokenizers = new List<PreTokenizer>
        {
            new WhitespacePreTokenizer(),
            new BertPreTokenizer()
        };
        var preTokenizer = new SequencePreTokenizer(tokenizers);

        var tokens = preTokenizer.PreTokenize("hello, world!").ToList();

        Assert.Contains("hello", tokens);
        Assert.Contains(",", tokens);
    }

    #endregion

    #region Normalizer Tests

    [Fact]
    public void BertNormalizer_Lowercases()
    {
        var config = new NormalizerConfig { Lowercase = true };
        var normalizer = new BertNormalizer(config);

        var result = normalizer.Normalize("HELLO WORLD");

        Assert.Equal("hello world", result);
    }

    [Fact]
    public void BertNormalizer_CleansWhitespace()
    {
        var normalizer = new BertNormalizer(null);

        var result = normalizer.Normalize("hello   world\t\ntest");

        Assert.DoesNotContain("  ", result);
        Assert.DoesNotContain("\t", result);
        Assert.DoesNotContain("\n", result);
    }

    [Fact]
    public void LowercaseNormalizer_Works()
    {
        var normalizer = new LowercaseNormalizer();

        var result = normalizer.Normalize("HeLLo WoRLD");

        Assert.Equal("hello world", result);
    }

    [Fact]
    public void NfcNormalizer_Normalizes()
    {
        var normalizer = new NfcNormalizer();

        // é can be represented as single character or e + combining acute
        var result = normalizer.Normalize("café");

        Assert.NotNull(result);
    }

    [Fact]
    public void SequenceNormalizer_ChainsNormalizers()
    {
        var normalizers = new List<Normalizer>
        {
            new LowercaseNormalizer(),
            new NfcNormalizer()
        };
        var normalizer = new SequenceNormalizer(normalizers);

        var result = normalizer.Normalize("CAFÉ");

        Assert.Equal("café", result);
    }

    #endregion

    #region Tokenizer Type Tests

    [Fact]
    public void TokenizerType_WordPiece()
    {
        var vocab = CreateBertVocab();
        var model = new WordPieceModel(vocab, null);

        Assert.Equal(TokenizerType.WordPiece, model.Type);
    }

    [Fact]
    public void TokenizerType_Bpe()
    {
        var vocab = new Dictionary<string, int> { { "<unk>", 0 } };
        var model = new BpeModel(vocab, null);

        Assert.Equal(TokenizerType.Bpe, model.Type);
    }

    [Fact]
    public void TokenizerType_Unigram()
    {
        var vocab = new Dictionary<string, int> { { "<unk>", 0 } };
        var model = new UnigramModel(vocab, null);

        Assert.Equal(TokenizerType.Unigram, model.Type);
    }

    #endregion

    #region Unigram Model Tests

    [Fact]
    public void Unigram_TokenizesWithVocab()
    {
        var vocab = new Dictionary<string, int>
        {
            { "<unk>", 0 },
            { "hello", 1 },
            { "world", 2 },
            { "h", 3 },
            { "e", 4 },
            { "l", 5 },
            { "o", 6 }
        };
        var model = new UnigramModel(vocab, null);

        var tokens = model.Tokenize("hello").ToList();

        // Should prefer "hello" as it's in vocab
        Assert.Contains("hello", tokens);
    }

    [Fact]
    public void Unigram_HandlesEmptyString()
    {
        var vocab = new Dictionary<string, int> { { "<unk>", 0 } };
        var model = new UnigramModel(vocab, null);

        var tokens = model.Tokenize("").ToList();

        Assert.Empty(tokens);
    }

    #endregion

    #region Helper Methods

    private static Dictionary<string, int> CreateBertVocab()
    {
        return new Dictionary<string, int>
        {
            { "[PAD]", 0 },
            { "[UNK]", 100 },
            { "[CLS]", 101 },
            { "[SEP]", 102 },
            { "[MASK]", 103 },
            { "hello", 1000 },
            { "world", 1001 },
            { "test", 1002 },
            { "the", 1003 },
            { "a", 1004 },
            { "is", 1005 },
            { "##ing", 1006 },
            { "##ed", 1007 },
            { "##s", 1008 }
        };
    }

    #endregion
}
