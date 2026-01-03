using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Mostlylucid.DocSummarizer.Mcp.Tools;

[McpServerToolType]
public static class DocSummarizerTools
{
    private static readonly string DefaultModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3.2:3b";
    private static readonly string DefaultEmbedModel = Environment.GetEnvironmentVariable("OLLAMA_EMBED_MODEL") ?? "nomic-embed-text";
    private static readonly string DefaultBaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";

    /// <summary>
    /// Check if Ollama is available and list models.
    /// </summary>
    [McpServerTool(Name = "check_ollama")]
    [Description("Check if Ollama LLM service is available and list installed models. Returns availability status and model information.")]
    public static async Task<string> CheckOllamaAsync()
    {
        var ollama = new OllamaService(DefaultModel, DefaultEmbedModel, DefaultBaseUrl);
        var available = await ollama.IsAvailableAsync();
        
        var result = new Dictionary<string, object>
        {
            ["available"] = available,
            ["base_url"] = DefaultBaseUrl,
            ["default_model"] = DefaultModel,
            ["embed_model"] = DefaultEmbedModel
        };

        if (available)
        {
            var models = await ollama.GetAvailableModelsAsync();
            var modelInfos = new List<object>();

            foreach (var modelName in models.Take(15))
            {
                var info = await ollama.GetModelInfoAsync(modelName);
                if (info != null)
                {
                    modelInfos.Add(new
                    {
                        name = info.Name,
                        family = info.Family,
                        parameters = info.ParameterCount,
                        quantization = info.QuantizationLevel,
                        context_window = info.ContextWindow
                    });
                }
            }

            result["models"] = modelInfos;
            result["model_count"] = models.Count;
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Generate text using Ollama LLM.
    /// </summary>
    [McpServerTool(Name = "generate_text")]
    [Description("Generate text using a local Ollama LLM. Useful for text generation, completion, summarization, and transformation tasks.")]
    public static async Task<string> GenerateTextAsync(
        [Description("Prompt for text generation")] string prompt,
        [Description("Temperature for generation (0.0-1.0, lower is more focused, default: 0.3)")] double temperature = 0.3,
        [Description("Ollama model to use (default from env or llama3.2:3b)")] string? model = null)
    {
        try
        {
            var ollama = new OllamaService(
                model: model ?? DefaultModel,
                embedModel: DefaultEmbedModel,
                baseUrl: DefaultBaseUrl);

            var response = await ollama.GenerateAsync(prompt, temperature);

            var result = new
            {
                success = true,
                prompt_length = prompt.Length,
                response,
                response_length = response.Length,
                model = model ?? DefaultModel,
                temperature
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Generate embeddings for text using Ollama.
    /// </summary>
    [McpServerTool(Name = "generate_embedding")]
    [Description("Generate vector embeddings for text using Ollama. Returns embedding dimensions and a sample of the vector. Useful for semantic search and similarity comparisons.")]
    public static async Task<string> GenerateEmbeddingAsync(
        [Description("Text to generate embeddings for")] string text,
        [Description("Embedding model to use (default from env or nomic-embed-text)")] string? embedModel = null)
    {
        try
        {
            var ollama = new OllamaService(
                model: DefaultModel,
                embedModel: embedModel ?? DefaultEmbedModel,
                baseUrl: DefaultBaseUrl);

            var embedding = await ollama.EmbedAsync(text);

            var result = new
            {
                success = true,
                text_length = text.Length,
                embedding_dimensions = embedding.Length,
                embedding_sample = embedding.Take(10).ToArray(),
                embedding_min = embedding.Min(),
                embedding_max = embedding.Max(),
                embedding_mean = embedding.Average(),
                model = embedModel ?? DefaultEmbedModel
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Calculate cosine similarity between two texts.
    /// </summary>
    [McpServerTool(Name = "calculate_similarity")]
    [Description("Calculate the cosine similarity between two texts using embeddings. Returns a score from -1 to 1, where 1 means identical meaning.")]
    public static async Task<string> CalculateSimilarityAsync(
        [Description("First text to compare")] string text1,
        [Description("Second text to compare")] string text2,
        [Description("Embedding model to use (default from env or nomic-embed-text)")] string? embedModel = null)
    {
        try
        {
            var ollama = new OllamaService(
                model: DefaultModel,
                embedModel: embedModel ?? DefaultEmbedModel,
                baseUrl: DefaultBaseUrl);

            var embedding1 = await ollama.EmbedAsync(text1);
            var embedding2 = await ollama.EmbedAsync(text2);

            var similarity = CosineSimilarity(embedding1, embedding2);

            var result = new
            {
                success = true,
                text1_length = text1.Length,
                text2_length = text2.Length,
                similarity_score = Math.Round(similarity, 4),
                interpretation = similarity switch
                {
                    > 0.9 => "Very similar / nearly identical meaning",
                    > 0.7 => "Similar / related topics",
                    > 0.5 => "Somewhat related",
                    > 0.3 => "Loosely related",
                    _ => "Different / unrelated topics"
                },
                model = embedModel ?? DefaultEmbedModel
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Summarize text using Ollama LLM.
    /// </summary>
    [McpServerTool(Name = "summarize_text")]
    [Description("Summarize text using a local Ollama LLM. Provides a concise summary of the input text.")]
    public static async Task<string> SummarizeTextAsync(
        [Description("Text to summarize")] string text,
        [Description("Target summary length: 'brief' (1-2 sentences), 'medium' (paragraph), 'detailed' (multiple paragraphs)")] string length = "medium",
        [Description("Ollama model to use (default from env or llama3.2:3b)")] string? model = null)
    {
        try
        {
            var lengthInstruction = length.ToLower() switch
            {
                "brief" => "Provide a 1-2 sentence summary.",
                "detailed" => "Provide a detailed summary covering all main points in multiple paragraphs.",
                _ => "Provide a concise paragraph summary."
            };

            var prompt = $"""
                Summarize the following text. {lengthInstruction}

                TEXT:
                {text}

                SUMMARY:
                """;

            var ollama = new OllamaService(
                model: model ?? DefaultModel,
                embedModel: DefaultEmbedModel,
                baseUrl: DefaultBaseUrl);

            var summary = await ollama.GenerateAsync(prompt, 0.3);

            var result = new
            {
                success = true,
                original_length = text.Length,
                summary,
                summary_length = summary.Length,
                compression_ratio = Math.Round((double)summary.Length / text.Length, 2),
                model = model ?? DefaultModel
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Read and summarize a file.
    /// </summary>
    [McpServerTool(Name = "summarize_file")]
    [Description("Read a text file (txt, md, etc.) and generate a summary using local Ollama LLM.")]
    public static async Task<string> SummarizeFileAsync(
        [Description("Path to the text file to summarize")] string filePath,
        [Description("Target summary length: 'brief', 'medium', or 'detailed'")] string length = "medium",
        [Description("Ollama model to use (default from env or llama3.2:3b)")] string? model = null)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return JsonSerializer.Serialize(new { success = false, error = $"File not found: {filePath}" });
            }

            var text = await File.ReadAllTextAsync(filePath);
            
            // Truncate if too long
            const int maxChars = 50000;
            if (text.Length > maxChars)
            {
                text = text[..maxChars] + "\n\n[Content truncated...]";
            }

            var lengthInstruction = length.ToLower() switch
            {
                "brief" => "Provide a 1-2 sentence summary.",
                "detailed" => "Provide a detailed summary covering all main points.",
                _ => "Provide a concise paragraph summary."
            };

            var prompt = $"""
                Summarize the following document. {lengthInstruction}

                DOCUMENT:
                {text}

                SUMMARY:
                """;

            var ollama = new OllamaService(
                model: model ?? DefaultModel,
                embedModel: DefaultEmbedModel,
                baseUrl: DefaultBaseUrl);

            var summary = await ollama.GenerateAsync(prompt, 0.3);

            var result = new
            {
                success = true,
                file = Path.GetFileName(filePath),
                file_size_bytes = new FileInfo(filePath).Length,
                original_length = text.Length,
                summary,
                summary_length = summary.Length,
                model = model ?? DefaultModel
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message, file = filePath });
        }
    }

    /// <summary>
    /// Answer a question about text content.
    /// </summary>
    [McpServerTool(Name = "ask_about_text")]
    [Description("Answer a question about provided text using a local Ollama LLM.")]
    public static async Task<string> AskAboutTextAsync(
        [Description("Text content to ask about")] string text,
        [Description("Question to ask about the text")] string question,
        [Description("Ollama model to use (default from env or llama3.2:3b)")] string? model = null)
    {
        try
        {
            var prompt = $"""
                Based on the following text, answer the question. If the answer is not in the text, say so.

                TEXT:
                {text}

                QUESTION: {question}

                ANSWER:
                """;

            var ollama = new OllamaService(
                model: model ?? DefaultModel,
                embedModel: DefaultEmbedModel,
                baseUrl: DefaultBaseUrl);

            var answer = await ollama.GenerateAsync(prompt, 0.2);

            var result = new
            {
                success = true,
                question,
                answer,
                text_length = text.Length,
                model = model ?? DefaultModel
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have the same length");

        double dotProduct = 0;
        double normA = 0;
        double normB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
