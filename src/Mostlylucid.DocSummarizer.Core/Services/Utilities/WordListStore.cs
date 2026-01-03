using System.Reflection;
using System.Text;
using DuckDB.NET.Data;

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
/// DuckDB-backed word list store for efficient querying of stopwords, honorifics, etc.
/// Supports both embedded defaults and custom user lists.
/// 
/// Features:
/// - Single DuckDB file for all word lists
/// - Case-insensitive lookups via indexed lowercase column
/// - Category-based filtering (stopwords, honorifics, place indicators, etc.)
/// - Optional weights for smarter filtering
/// - Custom user lists that override/extend defaults
/// </summary>
public sealed class WordListStore : IDisposable
{
    private readonly DuckDBConnection _connection;
    private readonly string _dbPath;
    private readonly bool _verbose;
    private bool _initialized;
    
    // In-memory cache for hot path lookups
    private HashSet<string>? _stopwordsCache;
    private HashSet<string>? _honorificsCache;
    private HashSet<string>? _placeIndicatorsCache;
    private HashSet<string>? _codeKeywordsCache;
    private HashSet<string>? _dayNamesCache;
    private HashSet<string>? _monthNamesCache;

    /// <summary>
    /// Create a word list store. Uses in-memory DB by default, or persists to file.
    /// </summary>
    /// <param name="dbPath">Path to DuckDB file, or null for in-memory</param>
    /// <param name="verbose">Enable verbose logging</param>
    public WordListStore(string? dbPath = null, bool verbose = false)
    {
        _dbPath = dbPath ?? ":memory:";
        _verbose = verbose;
        _connection = new DuckDBConnection($"Data Source={_dbPath}");
        _connection.Open();
    }

    /// <summary>
    /// Initialize the database schema and load default word lists from embedded resources.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;

        await CreateSchemaAsync();
        await LoadEmbeddedResourcesAsync();
        await BuildCachesAsync();
        
        _initialized = true;
        
        if (_verbose)
        {
            var stats = await GetStatsAsync();
            Console.WriteLine($"[WordListStore] Initialized: {stats.TotalWords} words across {stats.Categories} categories");
        }
    }

    /// <summary>
    /// Check if a word is in any stopword category (fast cached lookup).
    /// </summary>
    public bool IsStopword(string word)
    {
        EnsureInitialized();
        return _stopwordsCache?.Contains(word.ToLowerInvariant()) ?? false;
    }

    /// <summary>
    /// Check if a word is an honorific (Mr., Dr., Captain, etc.)
    /// </summary>
    public bool IsHonorific(string word)
    {
        EnsureInitialized();
        return _honorificsCache?.Contains(word.ToLowerInvariant()) ?? false;
    }

    /// <summary>
    /// Check if a word is a place indicator (Street, Road, etc.)
    /// </summary>
    public bool IsPlaceIndicator(string word)
    {
        EnsureInitialized();
        return _placeIndicatorsCache?.Contains(word.ToLowerInvariant()) ?? false;
    }

    /// <summary>
    /// Check if a word is a code/technical keyword
    /// </summary>
    public bool IsCodeKeyword(string word)
    {
        EnsureInitialized();
        return _codeKeywordsCache?.Contains(word.ToLowerInvariant()) ?? false;
    }

    /// <summary>
    /// Check if a word is a day name
    /// </summary>
    public bool IsDayName(string word)
    {
        EnsureInitialized();
        return _dayNamesCache?.Contains(word.ToLowerInvariant()) ?? false;
    }

    /// <summary>
    /// Check if a word is a month name
    /// </summary>
    public bool IsMonthName(string word)
    {
        EnsureInitialized();
        return _monthNamesCache?.Contains(word.ToLowerInvariant()) ?? false;
    }

    /// <summary>
    /// Check if word should be filtered (any category except honorific)
    /// </summary>
    public bool ShouldFilter(string word)
    {
        var lower = word.ToLowerInvariant();
        return IsStopword(lower) || IsCodeKeyword(lower) || IsDayName(lower) || IsMonthName(lower);
    }

    /// <summary>
    /// Get all words in a category.
    /// </summary>
    public async Task<List<string>> GetWordsAsync(WordListCategory category)
    {
        EnsureInitialized();
        
        var words = new List<string>();
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT word FROM wordlists WHERE category = $category ORDER BY word";
        cmd.Parameters.Add(new DuckDBParameter("category", category.ToString()));
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            words.Add(reader.GetString(0));
        }
        
        return words;
    }

    /// <summary>
    /// Search for words matching a pattern (SQL LIKE syntax).
    /// </summary>
    public async Task<List<(string Word, WordListCategory Category)>> SearchAsync(string pattern)
    {
        EnsureInitialized();
        
        var results = new List<(string, WordListCategory)>();
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT word, category FROM wordlists WHERE word_lower LIKE $pattern ORDER BY category, word";
        cmd.Parameters.Add(new DuckDBParameter("pattern", pattern.ToLowerInvariant()));
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var word = reader.GetString(0);
            var category = Enum.Parse<WordListCategory>(reader.GetString(1));
            results.Add((word, category));
        }
        
        return results;
    }

    /// <summary>
    /// Add a custom word to a category.
    /// </summary>
    public async Task AddWordAsync(string word, WordListCategory category, bool isCustom = true)
    {
        EnsureInitialized();
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO wordlists (word, word_lower, category, is_custom)
            VALUES ($word, $word_lower, $category, $is_custom)
            ON CONFLICT (word_lower, category) DO UPDATE SET is_custom = $is_custom
            """;
        cmd.Parameters.Add(new DuckDBParameter("word", word));
        cmd.Parameters.Add(new DuckDBParameter("word_lower", word.ToLowerInvariant()));
        cmd.Parameters.Add(new DuckDBParameter("category", category.ToString()));
        cmd.Parameters.Add(new DuckDBParameter("is_custom", isCustom));
        
        await cmd.ExecuteNonQueryAsync();
        
        // Invalidate cache
        await BuildCachesAsync();
    }

    /// <summary>
    /// Remove a word from a category.
    /// </summary>
    public async Task RemoveWordAsync(string word, WordListCategory category)
    {
        EnsureInitialized();
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM wordlists WHERE word_lower = $word_lower AND category = $category";
        cmd.Parameters.Add(new DuckDBParameter("word_lower", word.ToLowerInvariant()));
        cmd.Parameters.Add(new DuckDBParameter("category", category.ToString()));
        
        await cmd.ExecuteNonQueryAsync();
        
        // Invalidate cache
        await BuildCachesAsync();
    }

    /// <summary>
    /// Import words from a text file (one word per line, # for comments).
    /// </summary>
    public async Task ImportFromFileAsync(string filePath, WordListCategory category, bool isCustom = true)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Word list file not found: {filePath}");
        
        var lines = await File.ReadAllLinesAsync(filePath);
        await ImportLinesAsync(lines, category, isCustom);
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
        
        foreach (var word in words)
        {
            content.AppendLine(word);
        }
        
        await File.WriteAllTextAsync(filePath, content.ToString());
    }

    /// <summary>
    /// Get statistics about the word lists.
    /// </summary>
    public async Task<(int TotalWords, int Categories, int CustomWords)> GetStatsAsync()
    {
        EnsureInitialized();
        
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT 
                COUNT(*) as total,
                COUNT(DISTINCT category) as categories,
                SUM(CASE WHEN is_custom THEN 1 ELSE 0 END) as custom
            FROM wordlists
            """;
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2)
            );
        }
        
        return (0, 0, 0);
    }

    /// <summary>
    /// Persist the database to a file (useful if started in-memory).
    /// </summary>
    public async Task SaveToFileAsync(string filePath)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"EXPORT DATABASE '{filePath}' (FORMAT PARQUET)";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateSchemaAsync()
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS wordlists (
                id INTEGER PRIMARY KEY,
                word VARCHAR NOT NULL,
                word_lower VARCHAR NOT NULL,
                category VARCHAR NOT NULL,
                is_custom BOOLEAN DEFAULT FALSE,
                weight FLOAT DEFAULT 1.0,
                notes VARCHAR,
                UNIQUE(word_lower, category)
            );
            
            CREATE INDEX IF NOT EXISTS idx_wordlists_lower ON wordlists(word_lower);
            CREATE INDEX IF NOT EXISTS idx_wordlists_category ON wordlists(category);
            """;
        await cmd.ExecuteNonQueryAsync();
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
            
            await ImportLinesAsync(lines, category, isCustom: false);
            
            if (_verbose) Console.WriteLine($"[WordListStore] Loaded {lines.Length} words for {category}");
        }
    }

    private async Task ImportLinesAsync(IEnumerable<string> lines, WordListCategory category, bool isCustom)
    {
        var words = lines
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (words.Count == 0) return;

        // Batch insert for performance
        await using var cmd = _connection.CreateCommand();
        var sb = new StringBuilder();
        sb.AppendLine("INSERT OR IGNORE INTO wordlists (word, word_lower, category, is_custom) VALUES");
        
        var parameters = new List<DuckDBParameter>();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.AppendLine($"($w{i}, $wl{i}, $cat{i}, $cust{i})");
            parameters.Add(new DuckDBParameter($"w{i}", words[i]));
            parameters.Add(new DuckDBParameter($"wl{i}", words[i].ToLowerInvariant()));
            parameters.Add(new DuckDBParameter($"cat{i}", category.ToString()));
            parameters.Add(new DuckDBParameter($"cust{i}", isCustom));
        }
        
        cmd.CommandText = sb.ToString();
        foreach (var p in parameters) cmd.Parameters.Add(p);
        
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task BuildCachesAsync()
    {
        _stopwordsCache = (await GetWordsAsync(WordListCategory.Stopword))
            .Select(w => w.ToLowerInvariant()).ToHashSet();
        _honorificsCache = (await GetWordsAsync(WordListCategory.Honorific))
            .Select(w => w.ToLowerInvariant()).ToHashSet();
        _placeIndicatorsCache = (await GetWordsAsync(WordListCategory.PlaceIndicator))
            .Select(w => w.ToLowerInvariant()).ToHashSet();
        _codeKeywordsCache = (await GetWordsAsync(WordListCategory.CodeKeyword))
            .Select(w => w.ToLowerInvariant()).ToHashSet();
        _dayNamesCache = (await GetWordsAsync(WordListCategory.DayName))
            .Select(w => w.ToLowerInvariant()).ToHashSet();
        _monthNamesCache = (await GetWordsAsync(WordListCategory.MonthName))
            .Select(w => w.ToLowerInvariant()).ToHashSet();
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            // Synchronous initialization for hot path
            InitializeAsync().GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
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
            _instance?.Dispose();
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
