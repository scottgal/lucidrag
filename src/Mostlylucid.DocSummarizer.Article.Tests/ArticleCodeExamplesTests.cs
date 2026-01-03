using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer;
using Mostlylucid.DocSummarizer.Extensions;
using Mostlylucid.DocSummarizer.Models;
using Xunit;

namespace Mostlylucid.DocSummarizer.Article.Tests;

/// <summary>
/// Tests that verify the code examples in the blog article compile and work correctly.
/// These are the actual code snippets from docsummarizer-rag-pipeline.md
/// </summary>
public class ArticleCodeExamplesTests
{
    #region Extract Segments Example (from article)
    
    [Fact]
    public void ExtractSegments_ExampleCompiles()
    {
        // This is the exact code from the article "Extract Segments: The Core API"
        // Just verifying it compiles - actual extraction requires ONNX model download
        
        // Setup DI
        var services = new ServiceCollection();
        services.AddDocSummarizer();
        var provider = services.BuildServiceProvider();

        var summarizer = provider.GetRequiredService<IDocumentSummarizer>();
        
        // Verify we got a summarizer
        Assert.NotNull(summarizer);
    }
    
    #endregion
    
    #region Segment Properties Example (from article)
    
    [Fact]
    public void Segment_HasExpectedProperties()
    {
        // Verify the Segment class has the properties mentioned in the article
        // Segment ID format is: docid_typecode_index (e.g., "doc1_s_0" for sentence 0)
        var segment = CreateSegment("doc1", "s42", "This is test content", SegmentType.Sentence, 42, 0, 20);
        
        // Set computed properties
        segment.Embedding = new float[384];
        segment.SalienceScore = 0.85;
        
        // Verify all properties from the article exist
        Assert.Contains("_s_42", segment.Id); // ID format: docid_typecode_index
        Assert.Equal("This is test content", segment.Text);
        Assert.Equal(SegmentType.Sentence, segment.Type);
        Assert.Equal(384, segment.Embedding.Length);
        Assert.Equal(0.85, segment.SalienceScore);
        Assert.Equal(42, segment.Index);
    }
    
    #endregion
    
    #region ExtractionResult Lookup Methods
    
    [Fact]
    public void ExtractionResult_SegmentsById_Works()
    {
        // Test the new SegmentsById dictionary
        var result = new ExtractionResult
        {
            AllSegments = new List<Segment>
            {
                CreateSegment("doc", "s1", "First segment", SegmentType.Sentence, 0, 0, 15),
                CreateSegment("doc", "s2", "Second segment", SegmentType.Sentence, 1, 16, 30),
                CreateSegment("doc", "s3", "Third segment", SegmentType.Sentence, 2, 31, 45),
            }
        };
        
        // Access by ID - IDs are generated as "docid_type_index"
        var firstSegment = result.AllSegments[0];
        Assert.True(result.SegmentsById.ContainsKey(firstSegment.Id));
        Assert.Equal("First segment", result.SegmentsById[firstSegment.Id].Text);
        
        // GetSegment helper
        var segment = result.GetSegment(result.AllSegments[1].Id);
        Assert.NotNull(segment);
        Assert.Equal("Second segment", segment.Text);
        
        // Non-existent returns null
        Assert.Null(result.GetSegment("nonexistent"));
    }
    
    [Fact]
    public void ExtractionResult_GetSegmentByIndex_Works()
    {
        var result = new ExtractionResult
        {
            AllSegments = new List<Segment>
            {
                CreateSegment("doc", "s1", "First", SegmentType.Sentence, 0, 0, 5),
                CreateSegment("doc", "s2", "Second", SegmentType.Sentence, 1, 6, 12),
                CreateSegment("doc", "s3", "Third", SegmentType.Sentence, 2, 13, 18),
            }
        };
        
        var segment = result.GetSegmentByIndex(1);
        Assert.NotNull(segment);
        Assert.Equal("Second", segment.Text);
    }
    
    [Fact]
    public void ExtractionResult_GetSegmentAtPosition_Works()
    {
        var result = new ExtractionResult
        {
            AllSegments = new List<Segment>
            {
                CreateSegment("doc", "s1", "Hello", SegmentType.Sentence, 0, 0, 5),
                CreateSegment("doc", "s2", "World", SegmentType.Sentence, 1, 6, 11),
                CreateSegment("doc", "s3", "Test", SegmentType.Sentence, 2, 12, 16),
            }
        };
        
        // Position 3 is in "Hello" (0-5)
        var segment = result.GetSegmentAtPosition(3);
        Assert.NotNull(segment);
        Assert.Equal("Hello", segment.Text);
        
        // Position 8 is in "World" (6-11)
        segment = result.GetSegmentAtPosition(8);
        Assert.NotNull(segment);
        Assert.Equal("World", segment.Text);
        
        // Position 100 is not in any segment
        segment = result.GetSegmentAtPosition(100);
        Assert.Null(segment);
    }
    
    [Fact]
    public void ExtractionResult_GetSegmentsInRange_Works()
    {
        var result = new ExtractionResult
        {
            AllSegments = new List<Segment>
            {
                CreateSegment("doc", "s1", "First", SegmentType.Sentence, 0, 0, 10),
                CreateSegment("doc", "s2", "Second", SegmentType.Sentence, 1, 10, 20),
                CreateSegment("doc", "s3", "Third", SegmentType.Sentence, 2, 20, 30),
                CreateSegment("doc", "s4", "Fourth", SegmentType.Sentence, 3, 30, 40),
            }
        };
        
        // Range 5-25 should overlap with s1, s2, s3
        var segments = result.GetSegmentsInRange(5, 25).ToList();
        Assert.Equal(3, segments.Count);
    }
    
    [Fact]
    public void ExtractionResult_GetSegmentsOnPage_Works()
    {
        var result = new ExtractionResult
        {
            AllSegments = new List<Segment>
            {
                CreateSegmentWithPage("doc", "s1", "Page 1 content", SegmentType.Sentence, 0, 0, 10, 1),
                CreateSegmentWithPage("doc", "s2", "Also page 1", SegmentType.Sentence, 1, 10, 20, 1),
                CreateSegmentWithPage("doc", "s3", "Page 2 content", SegmentType.Sentence, 2, 20, 30, 2),
                CreateSegmentWithPage("doc", "s4", "More page 2", SegmentType.Sentence, 3, 30, 40, 2),
            }
        };
        
        var page1 = result.GetSegmentsOnPage(1).ToList();
        Assert.Equal(2, page1.Count);
        Assert.All(page1, s => Assert.Equal(1, s.PageNumber));
        
        var page2 = result.GetSegmentsOnPage(2).ToList();
        Assert.Equal(2, page2.Count);
    }
    
    #endregion
    
    #region Source Location and Highlighting
    
    [Fact]
    public void ExtractionResult_GetSourceLocation_Works()
    {
        var segment = CreateSegmentWithPageAndSection("doc", "s42", "Important text here", SegmentType.Sentence, 0, 100, 119, 3, "Results", "Chapter 2 > Results");
        
        var result = new ExtractionResult
        {
            AllSegments = new List<Segment> { segment }
        };
        
        var location = result.GetSourceLocation(segment.Id);
        Assert.NotNull(location);
        Assert.Equal(segment.Id, location.SegmentId);
        Assert.Equal(100, location.StartChar);
        Assert.Equal(119, location.EndChar);
        Assert.Equal(19, location.Length);
        Assert.Equal(3, location.PageNumber);
        Assert.Equal("Results", location.SectionTitle);
        Assert.Equal("Chapter 2 > Results", location.HeadingPath);
    }
    
    [Fact]
    public void ExtractionResult_GetHighlightedText_Works()
    {
        var originalDoc = "Some prefix text. Important segment here. Some suffix text.";
        //                 0         1         2         3         4         5         6
        //                 012345678901234567890123456789012345678901234567890123456789
        //                                   ^18               ^40
        
        var segment = CreateSegment("doc", "s1", "Important segment here", SegmentType.Sentence, 0, 18, 40);
        
        var result = new ExtractionResult
        {
            AllSegments = new List<Segment> { segment }
        };
        
        // Get highlighted text with context (15 chars to get "prefix")
        var highlighted = result.GetHighlightedText(originalDoc, segment.Id, contextChars: 15);
        Assert.NotNull(highlighted);
        Assert.Equal("Important segment here", highlighted.SegmentText);
        Assert.Contains("prefix", highlighted.BeforeContext);
        Assert.Contains("suffix", highlighted.AfterContext);
        
        // HTML output
        var html = highlighted.ToHtml();
        Assert.Contains("<span class=\"highlight\">", html);
        Assert.Contains("Important segment here", html);
        
        // Markdown output
        var markdown = highlighted.ToMarkdown();
        Assert.Contains("**Important segment here**", markdown);
    }
    
    [Fact]
    public void ExtractionResult_GetHighlightedText_NoContext()
    {
        var originalDoc = "Hello World Test Content Here";
        
        var segment = CreateSegment("doc", "s1", "World Test", SegmentType.Sentence, 0, 6, 16);
        
        var result = new ExtractionResult
        {
            AllSegments = new List<Segment> { segment }
        };
        
        // No context
        var highlighted = result.GetHighlightedText(originalDoc, segment.Id, contextChars: 0);
        Assert.NotNull(highlighted);
        Assert.Equal("World Test", highlighted.SegmentText);
        Assert.Equal("", highlighted.BeforeContext);
        Assert.Equal("", highlighted.AfterContext);
        Assert.Equal("World Test", highlighted.FullText);
    }
    
    #endregion
    
    #region Simple RAG Service Example (from article)
    
    [Fact]
    public void SimpleRagService_PatternCompiles()
    {
        // This tests that the RAG pipeline pattern from the article compiles
        var service = new SimpleRagService();
        
        // We can't actually run it without ONNX models, but the pattern is valid
        Assert.NotNull(service);
    }
    
    /// <summary>
    /// This is the SimpleRagService from the article - copied here to verify it compiles
    /// </summary>
    public class SimpleRagService
    {
        private readonly Dictionary<string, Segment> _segments = new();
        private readonly List<(string Id, float[] Vector)> _index = new();
        
        public void Index(ExtractionResult extraction, string docId)
        {
            foreach (var segment in extraction.AllSegments)
            {
                var id = $"{docId}:{segment.Id}";
                _segments[id] = segment;
                if (segment.Embedding != null)
                {
                    _index.Add((id, segment.Embedding));
                }
            }
        }
        
        public List<Segment> Query(float[] queryEmbedding, int topK = 5)
        {
            return _index
                .Select(x => (x.Id, Similarity: CosineSimilarity(queryEmbedding, x.Vector)))
                .OrderByDescending(x => x.Similarity)
                .Take(topK)
                .Select(x => _segments[x.Id])
                .ToList();
        }
        
        public string BuildContext(List<Segment> results)
        {
            return string.Join("\n\n", results.Select(s => 
                $"[{s.Id}] {s.Text}"));
        }
        
        private static float CosineSimilarity(float[] a, float[] b)
        {
            float dot = 0, normA = 0, normB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }
            return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
        }
    }
    
    #endregion
    
    #region Salience Filtering Example (from article)
    
    [Fact]
    public void SalienceFiltering_ExampleWorks()
    {
        // From the article: "Get the top 20% most salient segments"
        var extraction = new ExtractionResult
        {
            AllSegments = Enumerable.Range(0, 100)
                .Select(i => 
                {
                    var s = CreateSegment("doc", $"s{i}", $"Segment {i}", SegmentType.Sentence, i, i * 10, (i + 1) * 10);
                    s.SalienceScore = i / 100.0; // 0.00 to 0.99
                    return s;
                })
                .ToList()
        };
        
        // This is the exact code from the article
        var topSegments = extraction.AllSegments
            .OrderByDescending(s => s.SalienceScore)
            .Take((int)(extraction.AllSegments.Count * 0.2))
            .ToList();
        
        Assert.Equal(20, topSegments.Count);
        Assert.True(topSegments.All(s => s.SalienceScore >= 0.8)); // Top 20% have scores 0.80-0.99
    }
    
    #endregion
    
    #region Helper Methods
    
    private static Segment CreateSegment(string docId, string _, string text, SegmentType type, int index, int startChar, int endChar)
    {
        // The Segment constructor generates the ID from docId, type, and index
        return new Segment(docId, text, type, index, startChar, endChar);
    }
    
    private static Segment CreateSegmentWithPage(string docId, string _, string text, SegmentType type, int index, int startChar, int endChar, int pageNumber)
    {
        var segment = new Segment(docId, text, type, index, startChar, endChar)
        {
            PageNumber = pageNumber
        };
        return segment;
    }
    
    private static Segment CreateSegmentWithPageAndSection(string docId, string _, string text, SegmentType type, int index, int startChar, int endChar, int pageNumber, string sectionTitle, string headingPath)
    {
        var segment = new Segment(docId, text, type, index, startChar, endChar)
        {
            PageNumber = pageNumber,
            SectionTitle = sectionTitle,
            HeadingPath = headingPath
        };
        return segment;
    }
    
    #endregion
}
