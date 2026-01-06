using System.Text.RegularExpressions;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Lightweight extractive summarizer for short texts (OCR output, captions, etc.)
/// Uses TF-IDF scoring without requiring BERT/ONNX models.
/// Optimized for texts under 2000 chars where full BERT is overkill.
/// </summary>
public static class ShortTextSummarizer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
        "by", "from", "as", "is", "was", "are", "were", "been", "be", "have", "has", "had",
        "do", "does", "did", "will", "would", "could", "should", "may", "might", "must",
        "this", "that", "these", "those", "it", "its", "he", "she", "they", "we", "you", "i"
    };

    /// <summary>
    /// Summarize short text to a target length using extractive summarization.
    /// Selects the most important sentences based on TF-IDF scoring.
    /// </summary>
    /// <param name="text">Text to summarize</param>
    /// <param name="maxLength">Maximum output length in characters</param>
    /// <returns>Summarized text containing the most important sentences</returns>
    public static string Summarize(string text, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // If already short enough, return as-is
        if (text.Length <= maxLength)
            return text;

        // Split into sentences
        var sentences = SplitSentences(text);
        if (sentences.Count == 0)
            return text.Length > maxLength ? text[..(maxLength - 3)] + "..." : text;

        // If only one sentence, truncate it
        if (sentences.Count == 1)
        {
            var sentence = sentences[0];
            if (sentence.Length <= maxLength)
                return sentence;
            return TruncateAtWordBoundary(sentence, maxLength - 3) + "...";
        }

        // Score sentences using TF-IDF
        var scoredSentences = ScoreSentences(sentences);

        // Select top sentences that fit within maxLength
        var selected = SelectSentences(scoredSentences, sentences, maxLength);

        // Return selected sentences in original order with ellipsis if truncated
        var result = string.Join(" ", selected);
        if (selected.Count < sentences.Count)
            result += "...";

        return result;
    }

    private static List<string> SplitSentences(string text)
    {
        // Split on sentence boundaries (. ! ?) followed by space or end
        var pattern = @"(?<=[.!?])\s+";
        var sentences = Regex.Split(text.Trim(), pattern)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s) && s.Length > 3)
            .ToList();

        return sentences;
    }

    private static List<double> ScoreSentences(List<string> sentences)
    {
        // Build document term frequencies
        var documentTermFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sentenceTermFreqs = new List<Dictionary<string, int>>();

        foreach (var sentence in sentences)
        {
            var terms = Tokenize(sentence);
            var termFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var term in terms)
            {
                termFreq.TryGetValue(term, out var count);
                termFreq[term] = count + 1;

                if (!documentTermFreq.ContainsKey(term))
                    documentTermFreq[term] = 0;
            }

            // Count document frequency (unique per sentence)
            foreach (var term in termFreq.Keys)
            {
                documentTermFreq[term]++;
            }

            sentenceTermFreqs.Add(termFreq);
        }

        // Calculate TF-IDF scores for each sentence
        var scores = new List<double>();
        var n = sentences.Count;

        for (var i = 0; i < sentences.Count; i++)
        {
            var termFreq = sentenceTermFreqs[i];
            var score = 0.0;

            foreach (var (term, tf) in termFreq)
            {
                var df = documentTermFreq[term];
                var idf = Math.Log((double)n / df + 1);
                score += tf * idf;
            }

            // Position bonus: first and last sentences often more important
            if (i == 0) score *= 1.2;
            else if (i == sentences.Count - 1) score *= 1.1;

            // Length normalization
            score /= Math.Sqrt(sentences[i].Length);

            scores.Add(score);
        }

        return scores;
    }

    private static List<string> Tokenize(string text)
    {
        return Regex.Matches(text.ToLowerInvariant(), @"\b[a-z]{2,}\b")
            .Cast<Match>()
            .Select(m => m.Value)
            .Where(w => !StopWords.Contains(w))
            .ToList();
    }

    private static List<string> SelectSentences(List<double> scores, List<string> sentences, int maxLength)
    {
        // Create indexed list and sort by score descending
        var indexed = scores
            .Select((score, idx) => (Index: idx, Score: score, Sentence: sentences[idx]))
            .OrderByDescending(x => x.Score)
            .ToList();

        var selected = new List<(int Index, string Sentence)>();
        var currentLength = 0;

        foreach (var item in indexed)
        {
            var sentenceLength = item.Sentence.Length;

            // Account for space between sentences
            var neededLength = currentLength > 0 ? sentenceLength + 1 : sentenceLength;

            // Leave room for "..." suffix
            if (currentLength + neededLength > maxLength - 3 && selected.Count > 0)
                break;

            selected.Add((item.Index, item.Sentence));
            currentLength += neededLength;

            // Stop if we've used most of the budget
            if (currentLength >= maxLength - 10)
                break;
        }

        // Return in original document order
        return selected
            .OrderBy(x => x.Index)
            .Select(x => x.Sentence)
            .ToList();
    }

    private static string TruncateAtWordBoundary(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;

        var truncated = text[..maxLength];
        var lastSpace = truncated.LastIndexOf(' ');

        if (lastSpace > maxLength / 2)
            return truncated[..lastSpace].TrimEnd();

        return truncated.TrimEnd();
    }
}
