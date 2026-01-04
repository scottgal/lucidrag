using System.Reflection;
using System.Text;

namespace Mostlylucid.DocSummarizer.Services.Utilities;

/// <summary>
/// Word list categories for entity extraction filtering.
/// </summary>
public enum WordListCategory
{
    Stopword,
    Honorific,
    PlaceIndicator,
    DayName,
    MonthName,
    CodeKeyword
}

/// <summary>
/// In-memory word list store for efficient querying of stopwords, honorifics, etc.
/// Supports both embedded defaults and custom user lists.
///
/// Features:
/// - Fast in-memory HashSet lookups
/// - Case-insensitive matching
/// - Category-based filtering (stopwords, honorifics, place indicators, etc.)
/// - Custom user lists that override/extend defaults
/// </summary>
public sealed class WordListStore
{
    private readonly bool _verbose;
    private bool _initialized;

    // In-memory storage by category
    private readonly Dictionary<WordListCategory, HashSet<string>> _wordLists = new();

    /// <summary>
    /// Create a word list store with in-memory storage.
    /// </summary>
    /// <param name="dbPath">Ignored (for API compatibility)</param>
    /// <param name="verbose">Enable verbose logging</param>
    public WordListStore(string? dbPath = null, bool verbose = false)
    {
        _verbose = verbose;

        // Initialize empty sets for each category
        foreach (WordListCategory category in Enum.GetValues<WordListCategory>())
        {
            _wordLists[category] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Initialize the word lists by loading default words from embedded resources.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;

        await LoadEmbeddedResourcesAsync();

        _initialized = true;

        if (_verbose)
        {
            var stats = GetStats();
            Console.WriteLine($"[WordListStore] Initialized: {stats.TotalWords} words across {stats.Categories} categories");
        }
    }

    /// <summary>
    /// Check if a word is in any stopword category (fast cached lookup).
    /// </summary>
    public bool IsStopword(string word)
    {
        EnsureInitialized();
        return _wordLists[WordListCategory.Stopword].Contains(word);
    }

    /// <summary>
    /// Check if a word is an honorific (Mr., Dr., Captain, etc.)
    /// </summary>
    public bool IsHonorific(string word)
    {
        EnsureInitialized();
        return _wordLists[WordListCategory.Honorific].Contains(word);
    }

    /// <summary>
    /// Check if a word is a place indicator (Street, Road, etc.)
    /// </summary>
    public bool IsPlaceIndicator(string word)
    {
        EnsureInitialized();
        return _wordLists[WordListCategory.PlaceIndicator].Contains(word);
    }

    /// <summary>
    /// Check if a word is a code/technical keyword
    /// </summary>
    public bool IsCodeKeyword(string word)
    {
        EnsureInitialized();
        return _wordLists[WordListCategory.CodeKeyword].Contains(word);
    }

    /// <summary>
    /// Check if a word is a day name
    /// </summary>
    public bool IsDayName(string word)
    {
        EnsureInitialized();
        return _wordLists[WordListCategory.DayName].Contains(word);
    }

    /// <summary>
    /// Check if a word is a month name
    /// </summary>
    public bool IsMonthName(string word)
    {
        EnsureInitialized();
        return _wordLists[WordListCategory.MonthName].Contains(word);
    }

    /// <summary>
    /// Check if word should be filtered (any category except honorific)
    /// </summary>
    public bool ShouldFilter(string word)
    {
        return IsStopword(word) || IsCodeKeyword(word) || IsDayName(word) || IsMonthName(word);
    }

    /// <summary>
    /// Get all words in a category.
    /// </summary>
    public Task<List<string>> GetWordsAsync(WordListCategory category)
    {
        EnsureInitialized();
        return Task.FromResult(_wordLists[category].ToList());
    }

    /// <summary>
    /// Search for words matching a pattern (case-insensitive substring match).
    /// </summary>
    public Task<List<(string Word, WordListCategory Category)>> SearchAsync(string pattern)
    {
        EnsureInitialized();

        var results = new List<(string, WordListCategory)>();
        var lowerPattern = pattern.ToLowerInvariant().Replace("%", "");

        foreach (var (category, words) in _wordLists)
        {
            var matches = words
                .Where(w => w.ToLowerInvariant().Contains(lowerPattern))
                .Select(w => (w, category));
            results.AddRange(matches);
        }

        return Task.FromResult(results.OrderBy(r => r.Item2).ThenBy(r => r.Item1).ToList());
    }

    /// <summary>
    /// Add a custom word to a category.
    /// </summary>
    public Task AddWordAsync(string word, WordListCategory category, bool isCustom = true)
    {
        EnsureInitialized();
        _wordLists[category].Add(word);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Remove a word from a category.
    /// </summary>
    public Task RemoveWordAsync(string word, WordListCategory category)
    {
        EnsureInitialized();
        _wordLists[category].Remove(word);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Import words from a text file (one word per line, # for comments).
    /// </summary>
    public async Task ImportFromFileAsync(string filePath, WordListCategory category, bool isCustom = true)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Word list file not found: {filePath}");

        var lines = await File.ReadAllLinesAsync(filePath);
        ImportLines(lines, category);
    }

    /// <summary>
    /// Export a category to a text file.
    /// </summary>
    public async Task ExportToFileAsync(string filePath, WordListCategory category)
    {
        var words = await GetWordsAsync(category);
        var content = new StringBuilder();
        content.AppendLine($"# {category} word list");
        content.AppendLine($"# Exported: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        content.AppendLine();

        foreach (var word in words.OrderBy(w => w))
        {
            content.AppendLine(word);
        }

        await File.WriteAllTextAsync(filePath, content.ToString());
    }

    /// <summary>
    /// Get statistics about the word lists.
    /// </summary>
    public (int TotalWords, int Categories, int CustomWords) GetStats()
    {
        EnsureInitialized();

        return (
            _wordLists.Values.Sum(set => set.Count),
            _wordLists.Count,
            0 // No distinction between custom/default in simplified version
        );
    }

    /// <summary>
    /// Persist the database to a file (no-op in memory-only version).
    /// </summary>
    public Task SaveToFileAsync(string filePath)
    {
        // No-op for in-memory implementation
        return Task.CompletedTask;
    }

    private async Task LoadEmbeddedResourcesAsync()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourcePrefix = "Mostlylucid.DocSummarizer.Resources.";

        var mappings = new Dictionary<string, WordListCategory>
        {
            ["stopwords.txt"] = WordListCategory.Stopword,
            ["honorifics.txt"] = WordListCategory.Honorific,
            ["place-indicators.txt"] = WordListCategory.PlaceIndicator,
            ["code-keywords.txt"] = WordListCategory.CodeKeyword,
            ["day-names.txt"] = WordListCategory.DayName,
            ["month-names.txt"] = WordListCategory.MonthName
        };

        foreach (var (fileName, category) in mappings)
        {
            var resourceName = resourcePrefix + fileName.Replace("-", "_");
            await using var stream = assembly.GetManifestResourceStream(resourceName);

            if (stream == null)
            {
                if (_verbose) Console.WriteLine($"[WordListStore] Resource not found: {resourceName}");
                continue;
            }

            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            ImportLines(lines, category);

            if (_verbose) Console.WriteLine($"[WordListStore] Loaded {lines.Length} words for {category}");
        }
    }

    private void ImportLines(IEnumerable<string> lines, WordListCategory category)
    {
        var words = lines
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var word in words)
        {
            _wordLists[category].Add(word);
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            // Synchronous initialization for hot path
            InitializeAsync().GetAwaiter().GetResult();
        }
    }
}

/// <summary>
/// Singleton accessor for the default word list store.
/// </summary>
public static class WordLists
{
    private static WordListStore? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Get the default word list store instance.
    /// </summary>
    public static WordListStore Default
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new WordListStore();
                    _instance.InitializeAsync().GetAwaiter().GetResult();
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// Replace the default instance (useful for testing or custom configs).
    /// </summary>
    public static void SetDefault(WordListStore store)
    {
        lock (_lock)
        {
            _instance = store;
        }
    }

    // Convenience methods for common checks
    public static bool IsStopword(string word) => Default.IsStopword(word);
    public static bool IsHonorific(string word) => Default.IsHonorific(word);
    public static bool IsPlaceIndicator(string word) => Default.IsPlaceIndicator(word);
    public static bool IsCodeKeyword(string word) => Default.IsCodeKeyword(word);
    public static bool ShouldFilter(string word) => Default.ShouldFilter(word);
}
