using System.Diagnostics;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services.Onnx;


namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// BERT-based extractive summarization using local ONNX models.
/// No LLM required - fast, deterministic, perfect citation grounding.
/// 
/// Algorithm:
/// 1. Parse document into sentences using Markdig
/// 2. Generate BERT embeddings for each sentence
/// 3. Score sentences using:
///    - Position weight (intro/conclusion more important for expository)
///    - Similarity to document centroid (representativeness)
///    - MMR diversity penalty (avoid redundancy)
/// 4. Select top-k sentences as extractive summary
/// 5. Return in document order with citations
/// </summary>
public class BertSummarizer : IDisposable
{
    private readonly OnnxEmbeddingService _embeddingService;
    private readonly MarkdigDocumentParser _parser;
    private readonly BertConfig _config;
    private readonly bool _verbose;

    public BertSummarizer(OnnxConfig onnxConfig, BertConfig bertConfig, bool verbose = false)
    {
        _embeddingService = new OnnxEmbeddingService(onnxConfig, verbose);
        _parser = new MarkdigDocumentParser();
        _config = bertConfig;
        _verbose = verbose;
    }

    /// <summary>
    /// Generate an extractive summary from markdown content
    /// </summary>
    public async Task<DocumentSummary> SummarizeAsync(
        string markdown,
        ContentType contentType = ContentType.Unknown,
        int? maxSentences = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        
        // 1. Parse document into sentences
        VerboseHelper.Log(_verbose, "[dim]Parsing document with Markdig...[/]");
        var parsedDoc = _parser.Parse(markdown);
        
        if (parsedDoc.SentenceCount == 0)
        {
            return CreateEmptySummary("No sentences found in document");
        }
        
        VerboseHelper.Log(_verbose, $"[dim]Found {parsedDoc.SentenceCount} sentences in {parsedDoc.Sections.Count} sections[/]");
        
        // 2. Apply position weights based on content type
        var sentences = parsedDoc.GetWeightedSentences(contentType);
        
        // 3. Generate embeddings for all sentences
        VerboseHelper.Log(_verbose, "[dim]Generating BERT embeddings...[/]");
        await GenerateEmbeddingsAsync(sentences, ct);
        
        // 4. Calculate document centroid (average embedding)
        var centroid = CalculateCentroid(sentences);
        
        // 5. Score sentences using MMR (Maximal Marginal Relevance)
        var targetCount = maxSentences ?? CalculateTargetSentences(parsedDoc.SentenceCount);
        VerboseHelper.Log(_verbose, $"[dim]Selecting {targetCount} sentences using MMR...[/]");
        
        var selectedSentences = SelectSentencesMMR(sentences, centroid, targetCount);
        
        // 6. Sort by original order for coherent output
        var orderedSelection = selectedSentences.OrderBy(s => s.Index).ToList();
        
        // 7. Build summary output
        stopwatch.Stop();
        return BuildSummary(parsedDoc, orderedSelection, contentType, stopwatch.Elapsed);
    }

    /// <summary>
    /// Generate embeddings for all sentences
    /// </summary>
    private async Task GenerateEmbeddingsAsync(List<SentenceInfo> sentences, CancellationToken ct)
    {
        await _embeddingService.InitializeAsync(ct);
        
        // Process in batches for memory efficiency
        const int batchSize = 32;
        var total = sentences.Count;
        
        for (int i = 0; i < total; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            
            var batch = sentences.Skip(i).Take(batchSize).ToList();
            var texts = batch.Select(s => s.Text).ToList();
            
            var embeddings = await _embeddingService.EmbedBatchAsync(texts, ct);
            
            for (int j = 0; j < batch.Count; j++)
            {
                batch[j].Embedding = embeddings[j];
            }
            
            if (_verbose && total > batchSize)
            {
                var progress = Math.Min(i + batchSize, total);
                VerboseHelper.Log(_verbose, $"[dim]  Embedded {progress}/{total} sentences[/]");
            }
        }
    }

    /// <summary>
    /// Calculate document centroid (average of all sentence embeddings)
    /// </summary>
    private static float[] CalculateCentroid(List<SentenceInfo> sentences)
    {
        if (sentences.Count == 0 || sentences[0].Embedding == null)
            return Array.Empty<float>();
        
        var dim = sentences[0].Embedding!.Length;
        var centroid = new float[dim];
        var count = 0;
        
        foreach (var sentence in sentences)
        {
            if (sentence.Embedding == null) continue;
            for (int i = 0; i < dim; i++)
            {
                centroid[i] += sentence.Embedding[i];
            }
            count++;
        }
        
        if (count > 0)
        {
            for (int i = 0; i < dim; i++)
            {
                centroid[i] /= count;
            }
        }
        
        // L2 normalize
        var norm = MathF.Sqrt(centroid.Sum(x => x * x));
        if (norm > 0)
        {
            for (int i = 0; i < dim; i++)
                centroid[i] /= norm;
        }
        
        return centroid;
    }

    /// <summary>
    /// Select sentences using Maximal Marginal Relevance (MMR).
    /// Balances relevance to document (centroid similarity) with diversity (dissimilarity to already selected).
    /// 
    /// MMR = lambda * sim(sentence, centroid) - (1 - lambda) * max(sim(sentence, selected))
    /// </summary>
    private List<SentenceInfo> SelectSentencesMMR(
        List<SentenceInfo> sentences,
        float[] centroid,
        int targetCount)
    {
        var selected = new List<SentenceInfo>();
        var candidates = new HashSet<SentenceInfo>(sentences.Where(s => s.Embedding != null));
        
        // Pre-calculate centroid similarities with position weight
        foreach (var sentence in candidates)
        {
            var baseSim = CosineSimilarity(sentence.Embedding!, centroid);
            sentence.Score = baseSim * sentence.PositionWeight;
        }
        
        while (selected.Count < targetCount && candidates.Count > 0)
        {
            SentenceInfo? best = null;
            double bestScore = double.MinValue;
            
            foreach (var candidate in candidates)
            {
                // Relevance: similarity to centroid (already includes position weight)
                var relevance = candidate.Score;
                
                // Diversity: max similarity to already selected sentences
                double maxSimToSelected = 0;
                foreach (var sel in selected)
                {
                    var sim = CosineSimilarity(candidate.Embedding!, sel.Embedding!);
                    if (sim > maxSimToSelected)
                        maxSimToSelected = sim;
                }
                
                // MMR score
                var mmrScore = _config.Lambda * relevance - (1 - _config.Lambda) * maxSimToSelected;
                
                if (mmrScore > bestScore)
                {
                    bestScore = mmrScore;
                    best = candidate;
                }
            }
            
            if (best != null)
            {
                best.Score = bestScore;
                selected.Add(best);
                candidates.Remove(best);
            }
            else
            {
                break;
            }
        }
        
        return selected;
    }

    /// <summary>
    /// Calculate cosine similarity between two embeddings
    /// </summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? dot / denom : 0;
    }

    /// <summary>
    /// Calculate target number of sentences based on document length
    /// </summary>
    private int CalculateTargetSentences(int totalSentences)
    {
        // Use configured ratio, with min/max bounds
        var target = (int)(totalSentences * _config.ExtractionRatio);
        return Math.Clamp(target, _config.MinSentences, _config.MaxSentences);
    }

    /// <summary>
    /// Build the final DocumentSummary from selected sentences
    /// </summary>
    private DocumentSummary BuildSummary(
        ParsedDocument parsedDoc,
        List<SentenceInfo> selectedSentences,
        ContentType contentType,
        TimeSpan elapsed)
    {
        // Build executive summary from top sentences
        var executiveParts = new List<string>();
        foreach (var sentence in selectedSentences.Take(5))
        {
            executiveParts.Add($"{sentence.Text} {sentence.Citation}");
        }
        var executiveSummary = string.Join(" ", executiveParts);
        
        // Group remaining sentences by section as topics
        var topicSummaries = new List<TopicSummary>();
        var groupedBySection = selectedSentences
            .GroupBy(s => s.SectionHeading)
            .Where(g => !string.IsNullOrEmpty(g.Key));
        
        foreach (var group in groupedBySection)
        {
            var sectionSentences = group.OrderBy(s => s.Index).ToList();
            var summary = string.Join(" ", sectionSentences.Select(s => $"{s.Text} {s.Citation}"));
            var sourceChunks = sectionSentences.Select(s => $"sentence-{s.Index}").ToList();
            
            topicSummaries.Add(new TopicSummary(group.Key, summary, sourceChunks));
        }
        
        // Build trace
        var trace = new SummarizationTrace(
            "bert-extractive",
            parsedDoc.SentenceCount,
            selectedSentences.Count,
            parsedDoc.Sections.Select(s => s.Heading).Where(h => !string.IsNullOrEmpty(h)).Take(10).ToList(),
            elapsed,
            CoverageScore: (double)selectedSentences.Count / parsedDoc.SentenceCount,
            CitationRate: 1.0 // BERT extraction always has perfect citations
        );
        
        // Extract entities from selected sentences (skip for expository/technical content - produces noise)
        var entities = contentType == ContentType.Expository 
            ? ExtractedEntities.Empty 
            : ExtractSimpleEntities(selectedSentences);
        
        return new DocumentSummary(
            executiveSummary,
            topicSummaries,
            OpenQuestions: new List<string>(), // BERT extraction doesn't generate questions
            trace,
            entities
        );
    }

    /// <summary>
    /// Simple entity extraction from selected sentences
    /// </summary>
    private static ExtractedEntities ExtractSimpleEntities(List<SentenceInfo> sentences)
    {
        var text = string.Join(" ", sentences.Select(s => s.Text));
        
        // Simple regex-based entity extraction
        var characters = new List<string>();
        var locations = new List<string>();
        var dates = new List<string>();
        
        // Find capitalized words that might be names (naive but fast)
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (word.Length > 2 && char.IsUpper(word[0]) && word.All(c => char.IsLetter(c)))
            {
                // Skip common words
                if (!CommonWords.Contains(word.ToLowerInvariant()))
                {
                    if (!characters.Contains(word) && characters.Count < 10)
                        characters.Add(word);
                }
            }
        }
        
        return new ExtractedEntities(characters, locations, dates, new List<string>(), new List<string>());
    }
    
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
        "as", "by", "from", "up", "about", "into", "over", "after", "this", "that", "these",
        "those", "it", "its", "is", "are", "was", "were", "be", "been", "being", "have", "has",
        "had", "do", "does", "did", "will", "would", "could", "should", "may", "might", "must",
        "shall", "can", "need", "not", "no", "yes", "all", "each", "every", "both", "few",
        "more", "most", "other", "some", "such", "only", "own", "same", "so", "than", "too",
        "very", "just", "also", "now", "here", "there", "when", "where", "why", "how", "what",
        "which", "who", "whom", "whose", "if", "then", "else", "because", "although", "though",
        "while", "whereas", "however", "therefore", "thus", "hence", "accordingly", "consequently"
    };

    private DocumentSummary CreateEmptySummary(string message)
    {
        return new DocumentSummary(
            message,
            new List<TopicSummary>(),
            new List<string>(),
            new SummarizationTrace("bert-extractive", 0, 0, new List<string>(), TimeSpan.Zero, 0, 0),
            ExtractedEntities.Empty
        );
    }

    public void Dispose()
    {
        _embeddingService.Dispose();
    }
}

/// <summary>
/// Configuration for BERT extractive summarization
/// </summary>
public class BertConfig
{
    /// <summary>
    /// MMR lambda parameter (0-1). Higher values favor relevance, lower values favor diversity.
    /// Default: 0.7 (slightly favor relevance)
    /// </summary>
    public double Lambda { get; set; } = 0.7;
    
    /// <summary>
    /// Target extraction ratio (fraction of sentences to select).
    /// Default: 0.15 (select ~15% of sentences)
    /// </summary>
    public double ExtractionRatio { get; set; } = 0.15;
    
    /// <summary>
    /// Minimum sentences to extract regardless of document size.
    /// Default: 3
    /// </summary>
    public int MinSentences { get; set; } = 3;
    
    /// <summary>
    /// Maximum sentences to extract regardless of document size.
    /// Default: 30
    /// </summary>
    public int MaxSentences { get; set; } = 30;
    
    /// <summary>
    /// Enable position-aware weighting based on document type.
    /// When true, intro/conclusion sentences get higher weights for expository content.
    /// Default: true
    /// </summary>
    public bool UsePositionWeighting { get; set; } = true;
}
