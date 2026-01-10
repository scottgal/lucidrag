using Microsoft.Extensions.Logging;
using WeCantSpell.Hunspell;

namespace Mostlylucid.DocSummarizer.Images.Services.Ocr.PostProcessing;

/// <summary>
/// Multi-language spell checker for OCR quality detection and correction
/// Uses Hunspell dictionaries with auto-download support
/// </summary>
public class SpellChecker : IDisposable
{
    private readonly Dictionary<string, WordList> _dictionaries = new();
    private readonly ILogger<SpellChecker>? _logger;
    private readonly string _dictionaryPath;
    private readonly DictionaryDownloader _downloader;

    public SpellChecker(string? dictionaryPath = null, ILogger<SpellChecker>? logger = null)
    {
        _dictionaryPath = dictionaryPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "lucidrag", "models", "dictionaries");
        _logger = logger;
        _downloader = new DictionaryDownloader(_dictionaryPath, logger as ILogger<DictionaryDownloader>);
    }

    /// <summary>
    /// Load a dictionary for a specific language
    /// Auto-downloads if not available locally
    /// </summary>
    /// <param name="language">Language code (e.g., "en_US", "es_ES", "fr_FR")</param>
    public async Task<bool> LoadDictionaryAsync(string language, CancellationToken ct = default)
    {
        if (_dictionaries.ContainsKey(language))
            return true;

        try
        {
            // Auto-download if needed
            if (!_downloader.IsDictionaryAvailable(language))
            {
                _logger?.LogInformation("Dictionary not found for {Language}, attempting auto-download...", language);
                var downloaded = await _downloader.EnsureDictionaryAsync(language, ct);
                if (!downloaded)
                {
                    _logger?.LogWarning("Failed to auto-download dictionary for {Language}", language);
                    return false;
                }
            }

            // Load from disk
            var affPath = Path.Combine(_dictionaryPath, $"{language}.aff");
            var dicPath = Path.Combine(_dictionaryPath, $"{language}.dic");

            if (File.Exists(affPath) && File.Exists(dicPath))
            {
                _logger?.LogDebug("Loading dictionary files: {AffPath}, {DicPath}", affPath, dicPath);
                var wordList = await Task.Run(() => WordList.CreateFromFiles(dicPath, affPath), ct);
                if (wordList != null)
                {
                    _dictionaries[language] = wordList;
                    _logger?.LogInformation("Successfully loaded dictionary: {Language}", language);
                    return true;
                }
            }

            _logger?.LogWarning("Dictionary files not found after download: {Language}", language);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load dictionary for {Language}", language);
            return false;
        }
    }

    /// <summary>
    /// Analyze OCR text quality by checking spelling
    /// Returns percentage of correctly spelled words (0.0 - 1.0)
    /// </summary>
    public SpellCheckResult CheckTextQuality(string text, string language = "en_US")
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SpellCheckResult { CorrectWordsRatio = 0, TotalWords = 0, CorrectWords = 0, Language = language };

        if (!_dictionaries.TryGetValue(language, out var dictionary))
        {
            _logger?.LogWarning("Dictionary not loaded for {Language}, cannot check spelling", language);
            return new SpellCheckResult { CorrectWordsRatio = -1, TotalWords = 0, Language = language, Error = $"Dictionary not loaded: {language}" };
        }

        // Split into words (remove punctuation and special characters)
        var words = text.Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 0)
            return new SpellCheckResult { CorrectWordsRatio = 0, TotalWords = 0, CorrectWords = 0, Language = language };

        int correctCount = 0;
        int totalCount = 0;
        var misspelledWords = new List<string>();
        var suggestions = new Dictionary<string, List<string>>();

        foreach (var word in words)
        {
            // Skip very short words (often OCR artifacts)
            if (word.Length < 2)
            {
                _logger?.LogDebug("Skipping short word: '{Word}' (length {Length})", word, word.Length);
                continue;
            }

            // Skip numbers
            if (word.All(char.IsDigit))
            {
                _logger?.LogDebug("Skipping numeric word: '{Word}'", word);
                continue;
            }

            totalCount++;

            // OCR-specific heuristics: Flag suspicious patterns even if dictionary-valid
            var isSuspiciousOcrPattern = IsSuspiciousOcrPattern(word);

            if (isSuspiciousOcrPattern)
            {
                _logger?.LogInformation("Word '{Word}' flagged by OCR heuristics (likely scan artifact)", word);
                misspelledWords.Add(word);

                // Get suggestions
                var wordSuggestions = dictionary.Suggest(word).Take(3).ToList();
                if (wordSuggestions.Any())
                {
                    _logger?.LogInformation("Suggestions for '{Word}': {Suggestions}", word, string.Join(", ", wordSuggestions));
                    suggestions[word] = wordSuggestions;
                }
            }
            else if (dictionary.Check(word))
            {
                _logger?.LogDebug("Word '{Word}' is correct (exact match)", word);
                correctCount++;
            }
            else
            {
                // Try lowercase
                var lower = word.ToLowerInvariant();
                if (dictionary.Check(lower))
                {
                    _logger?.LogDebug("Word '{Word}' is correct (lowercase match: '{Lower}')", word, lower);
                    correctCount++;
                }
                else
                {
                    _logger?.LogInformation("Word '{Word}' is misspelled", word);
                    misspelledWords.Add(word);

                    // Get suggestions for correction
                    var wordSuggestions = dictionary.Suggest(word).Take(3).ToList();
                    if (wordSuggestions.Any())
                    {
                        _logger?.LogInformation("Suggestions for '{Word}': {Suggestions}", word, string.Join(", ", wordSuggestions));
                        suggestions[word] = wordSuggestions;
                    }
                }
            }
        }

        var ratio = totalCount > 0 ? (double)correctCount / totalCount : 0;

        // Detect if LLM escalation is recommended
        // Criteria: Low confidence even if ratio is OK, or very short text with errors
        var recommendLlmEscalation = false;
        if (misspelledWords.Count > 0)
        {
            // Short text (< 5 words) with ANY errors should escalate
            if (totalCount < 5 && misspelledWords.Count > 0)
            {
                recommendLlmEscalation = true;
            }
            // OR ratio between 0.5-0.8 (uncertain quality)
            else if (ratio >= 0.5 && ratio < 0.8)
            {
                recommendLlmEscalation = true;
            }
            // OR high ratio but flagged OCR artifacts
            else if (ratio >= 0.8 && misspelledWords.Any())
            {
                recommendLlmEscalation = true;
            }
        }

        _logger?.LogInformation(
            "Spell check complete for '{Text}': {CorrectCount}/{TotalCount} words correct ({Ratio:P0}), {MisspelledCount} misspelled, LLM escalation: {Escalate}",
            text.Length > 50 ? text.Substring(0, 50) + "..." : text,
            correctCount,
            totalCount,
            ratio,
            misspelledWords.Count,
            recommendLlmEscalation);

        if (misspelledWords.Any())
        {
            _logger?.LogInformation("Misspelled words: {Words}", string.Join(", ", misspelledWords));
        }

        return new SpellCheckResult
        {
            CorrectWordsRatio = ratio,
            TotalWords = totalCount,
            CorrectWords = correctCount,
            MisspelledWords = misspelledWords,
            Suggestions = suggestions,
            Language = language,
            IsGarbled = ratio < 0.5, // Less than 50% correct = likely garbled
            RecommendLlmEscalation = recommendLlmEscalation
        };
    }

    /// <summary>
    /// Attempt to correct OCR text using spell checking
    /// Returns corrected text with high-confidence suggestions applied
    /// </summary>
    public string CorrectText(string text, string language = "en_US", double confidenceThreshold = 0.8)
    {
        if (string.IsNullOrWhiteSpace(text) || !_dictionaries.ContainsKey(language))
            return text;

        var result = CheckTextQuality(text, language);

        if (result.Suggestions == null || !result.Suggestions.Any())
            return text;

        var correctedText = text;

        // Apply corrections for words with high-confidence single suggestions
        foreach (var (misspelled, suggestions) in result.Suggestions)
        {
            if (suggestions.Count == 1) // High confidence - only one suggestion
            {
                correctedText = correctedText.Replace(misspelled, suggestions[0]);
            }
        }

        return correctedText;
    }

    /// <summary>
    /// Detect the language of the text based on available dictionaries
    /// Returns the language code with the highest spelling accuracy
    /// </summary>
    public string DetectLanguage(string text, params string[] languagesToCheck)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "unknown";

        var results = languagesToCheck
            .Select(lang => (Language: lang, Result: CheckTextQuality(text, lang)))
            .Where(x => x.Result.CorrectWordsRatio >= 0) // Exclude errors
            .OrderByDescending(x => x.Result.CorrectWordsRatio)
            .ToList();

        return results.FirstOrDefault().Language ?? "unknown";
    }

    /// <summary>
    /// Detect OCR-specific patterns that are likely scan artifacts
    /// even if they're dictionary-valid.
    /// Language-agnostic heuristics - no hardcoded word lists.
    /// </summary>
    private static bool IsSuspiciousOcrPattern(string word)
    {
        if (word.Length < 2) return false;

        // Pattern 1: Mixed case in middle of word (AbCde, WoRd, TeXt, etc.)
        // OCR sometimes produces random case changes
        // This is language-agnostic - ALL languages would be suspicious with random mid-word caps
        if (word.Length > 2)
        {
            var upperCount = word.Count(char.IsUpper);
            var lowerCount = word.Count(char.IsLower);

            // If has both upper and lower, and first char is upper
            if (upperCount > 1 && lowerCount > 0 && char.IsUpper(word[0]))
            {
                // Check if uppercase letters appear in middle/end (not just start)
                // Example: "WoRd" has uppercase at indices [0, 2] - index 2 is suspicious
                var upperIndices = word.Select((c, i) => char.IsUpper(c) ? i : -1)
                    .Where(i => i >= 0)
                    .ToList();

                // If uppercase appears in middle/end (not just start), likely OCR error
                if (upperIndices.Any(i => i > 0 && i < word.Length - 1))
                {
                    return true;
                }
            }
        }

        // Pattern 2: Single character followed by punctuation artifacts
        // OCR sometimes mistakes punctuation for letters (I', l', etc.)
        if (word.Length == 2 && !char.IsLetterOrDigit(word[1]))
        {
            return true;
        }

        // Pattern 2b: Two-letter words with mixed case (uppercase + lowercase)
        // Most valid 2-letter words are either all-caps (US, UK, OK) or all-lower (is, of, to)
        // Mixed case like "Bf", "Tn", "Df" are almost always OCR errors
        // Exception: Proper nouns at start of sentence, but those are rare in OCR contexts
        if (word.Length == 2)
        {
            var hasUpper = word.Any(char.IsUpper);
            var hasLower = word.Any(char.IsLower);

            // If it has BOTH upper and lower in just 2 letters, very likely OCR error
            if (hasUpper && hasLower)
            {
                // Common exceptions: "Dr", "Mr", "Ms", "St" (honorifics/abbreviations)
                // These should still be in dictionary, but for safety, exclude them
                var commonMixedCase = new[] { "Dr", "Mr", "Ms", "St", "Jr", "Sr", "Dn", "Mt" };
                if (!commonMixedCase.Contains(word, StringComparer.OrdinalIgnoreCase))
                {
                    return true; // Likely OCR error
                }
            }
        }

        // Pattern 3: Alternating case (aBcD, LiKe, etc.)
        // Extremely rare in real text, common in OCR errors
        if (word.Length >= 4)
        {
            var caseChanges = 0;
            for (int i = 1; i < word.Length; i++)
            {
                if (char.IsLetter(word[i]) && char.IsLetter(word[i-1]))
                {
                    if (char.IsUpper(word[i]) != char.IsUpper(word[i-1]))
                    {
                        caseChanges++;
                    }
                }
            }

            // If case changes more than twice in a short word, likely OCR error
            if (caseChanges >= 3)
            {
                return true;
            }
        }

        // Pattern 4: Non-ASCII mixed with ASCII in suspicious ways
        // OCR sometimes produces encoding errors
        if (word.Any(c => c > 127 && c < 160)) // C1 control characters range
        {
            return true; // Likely encoding error from OCR
        }

        return false;
    }

    public void Dispose()
    {
        _dictionaries.Clear();
    }
}

/// <summary>
/// Result of spell checking OCR text
/// </summary>
public record SpellCheckResult
{
    /// <summary>
    /// Ratio of correctly spelled words (0.0 - 1.0), or -1 if dictionary not available
    /// </summary>
    public double CorrectWordsRatio { get; init; }

    /// <summary>
    /// Total number of words checked
    /// </summary>
    public int TotalWords { get; init; }

    /// <summary>
    /// Number of correctly spelled words
    /// </summary>
    public int CorrectWords { get; init; }

    /// <summary>
    /// List of misspelled words
    /// </summary>
    public List<string> MisspelledWords { get; init; } = new();

    /// <summary>
    /// Spelling suggestions for misspelled words
    /// </summary>
    public Dictionary<string, List<string>> Suggestions { get; init; } = new();

    /// <summary>
    /// Language used for checking
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Indicates if text appears garbled (< 50% correct words)
    /// </summary>
    public bool IsGarbled { get; init; }

    /// <summary>
    /// Recommends escalating to ML/LLM for context-aware correction
    /// Triggered for short text with errors or uncertain quality (50-80% correct)
    /// </summary>
    public bool RecommendLlmEscalation { get; init; }

    /// <summary>
    /// Error message if spell checking failed
    /// </summary>
    public string? Error { get; init; }
}
