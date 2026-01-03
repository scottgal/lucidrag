using Mostlylucid.GraphRag;

namespace LucidRAG.Config;

public class RagDocumentsConfig
{
    public const string SectionName = "RagDocuments";

    public bool RequireApiKey { get; set; } = false;
    public string ApiKey { get; set; } = "";
    public string UploadPath { get; set; } = "./uploads";
    public int MaxFileSizeMB { get; set; } = 100;
    public string[] AllowedExtensions { get; set; } = [".pdf", ".docx", ".md", ".txt", ".html"];

    /// <summary>
    /// Web crawler configuration
    /// </summary>
    public CrawlerConfig Crawler { get; set; } = new();

    /// <summary>
    /// Entity extraction mode for GraphRAG.
    /// - Heuristic: Fast, no LLM calls (default)
    /// - Hybrid: Heuristic candidates + LLM enhancement per document
    /// - Llm: Full MSFT GraphRAG style (2 LLM calls per chunk)
    /// </summary>
    public ExtractionMode ExtractionMode { get; set; } = ExtractionMode.Heuristic;

    /// <summary>
    /// Demo mode configuration for public deployments like lucidrag.com
    /// </summary>
    public DemoModeConfig DemoMode { get; set; } = new();
}

/// <summary>
/// Configuration for demo mode - read-only demo with pre-loaded RAG blog articles
/// </summary>
public class DemoModeConfig
{
    /// <summary>
    /// Enable demo mode (read-only, pre-loaded content, no uploads)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Path to demo content directory containing markdown files to pre-load
    /// </summary>
    public string ContentPath { get; set; } = "./demo-content";

    /// <summary>
    /// URLs to fetch blog articles from (RSS or direct markdown URLs)
    /// </summary>
    public string[] BlogArticleUrls { get; set; } = [];

    /// <summary>
    /// Message shown to users in demo mode
    /// </summary>
    public string BannerMessage { get; set; } = "Demo Mode: Explore RAG with pre-loaded articles from mostlylucid.net";

    /// <summary>
    /// Minimum relevance score (0-1) required for search results.
    /// If no results exceed this threshold, return an off-topic response.
    /// Lower values are more permissive, higher values are stricter.
    /// Only enforced when demo mode is enabled.
    /// </summary>
    public float MinRelevanceScore { get; set; } = 0.3f;

    /// <summary>
    /// Message returned when user asks off-topic questions in demo mode
    /// </summary>
    public string OffTopicMessage { get; set; } = "This demo answers questions about the indexed documents. Try asking about topics covered in the available content.";
}

/// <summary>
/// Configuration for web crawler
/// </summary>
public class CrawlerConfig
{
    /// <summary>
    /// User-Agent string for HTTP requests. Include contact info for politeness.
    /// </summary>
    public string UserAgent { get; set; } = "LucidRAG/1.0 (+https://github.com/scottgal/mostlylucidweb)";

    /// <summary>
    /// Delay between requests to the same host (milliseconds)
    /// </summary>
    public int RequestDelayMs { get; set; } = 1000;

    /// <summary>
    /// Request timeout (seconds)
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to respect robots.txt
    /// </summary>
    public bool RespectRobotsTxt { get; set; } = true;
}

public class PromptsConfig
{
    public const string SectionName = "Prompts";

    public Dictionary<string, string> SystemPrompts { get; set; } = new()
    {
        ["Default"] = "You are a helpful assistant that answers questions based on the provided documents. Always cite your sources.",
        ["Technical"] = "You are a technical documentation expert. Provide precise, code-focused answers with examples.",
        ["Research"] = "You are a research assistant. Provide comprehensive analysis with multiple perspectives.",
        ["Concise"] = "You are a concise assistant. Provide brief, direct answers in 1-2 sentences."
    };

    public QueryClarificationConfig QueryClarification { get; set; } = new();
    public QueryDecompositionConfig QueryDecomposition { get; set; } = new();
    public SelfCorrectionConfig SelfCorrection { get; set; } = new();
    public ResponseSynthesisConfig ResponseSynthesis { get; set; } = new();
}

public class QueryClarificationConfig
{
    public bool Enabled { get; set; } = true;
    public string Prompt { get; set; } = """
        Analyze this query and determine if clarification is needed:

        Query: {query}
        Context: {context}

        Respond with JSON:
        {"needsClarification": bool, "clarificationQuestion": string | null, "rewrittenQuery": string}
        """;
    public double AmbiguityThreshold { get; set; } = 0.7;
}

public class QueryDecompositionConfig
{
    public bool Enabled { get; set; } = true;
    public string Prompt { get; set; } = """
        Break down this complex query into simpler sub-queries:

        Query: {query}

        Respond with JSON array of sub-queries.
        """;
    public int MaxSubQueries { get; set; } = 5;
}

public class SelfCorrectionConfig
{
    public bool Enabled { get; set; } = true;
    public string Prompt { get; set; } = """
        Evaluate if these search results adequately answer the query:

        Query: {query}
        Results: {results}

        Respond with JSON:
        {"adequate": bool, "missingInfo": string | null, "refinedQuery": string | null}
        """;
    public int MaxRetries { get; set; } = 2;
}

public class ResponseSynthesisConfig
{
    public string Prompt { get; set; } = """
        Based on the following sources, answer the user's question.

        {systemPrompt}

        Sources:
        {sources}

        Question: {query}

        Provide a comprehensive answer with citations [1], [2], etc.
        """;
    public bool IncludeSources { get; set; } = true;
    public int MaxSourcesPerResponse { get; set; } = 10;
}
