using System.Text.RegularExpressions;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Detects and filters front matter, junk content, and identifies where the main content begins.
/// Uses heuristics and optionally a sentinel LLM for difficult cases.
/// </summary>
public class FrontMatterDetector
{
    private readonly OllamaService? _ollama;
    private readonly bool _verbose;

    // Common front matter patterns
    private static readonly Regex YamlFrontMatter = new(@"^---\s*\n.*?\n---\s*\n", 
        RegexOptions.Singleline | RegexOptions.Compiled);
    
    private static readonly Regex TomlFrontMatter = new(@"^\+\+\+\s*\n.*?\n\+\+\+\s*\n", 
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Junk patterns commonly found in PDFs (spam, watermarks, metadata)
    private static readonly string[] JunkIndicators =
    [
        // SEO/web spam commonly embedded in PDFs
        "online auction", "search engine optimization", "seo", "spam blocking",
        "web hosting", "web development", "traffic building", "voip",
        "computer hardware", "data recovery", "internet security",
        "streaming audio", "online music", "video streaming", "web design",
        "web site promotion", "broadband internet", "mobile cell phone",
        "video conferencing", "cell phone",
        // Generic web junk
        "click here", "subscribe now", "advertisement", "sponsored",
        "all rights reserved", "copyright ©", "terms of service",
        "privacy policy", "cookie policy", "powered by",
        // PDF watermarks
        "watermark", "trial version", "evaluation copy", "unregistered"
    ];

    // Multi-word junk sequences that indicate spam (case insensitive)
    private static readonly string[] JunkSequences =
    [
        "search engine optimization spam",
        "streaming audio online music",
        "web hosting web site",
        "computer hardware data recovery",
        "voip computer hardware"
    ];

    // Regex pattern to detect "keyword stuffing" - many capitalized words with no sentence structure
    // This pattern catches spam like "Online Auction Search Engine Optimization Spam Blocking..."
    private static readonly Regex KeywordStuffingPattern = new(
        @"(?:[A-Z][a-z]+\s+){5,}", // 5+ capitalized words in a row
        RegexOptions.Compiled);

    // Pattern to detect lines that are mostly just a list of unrelated topics/categories
    private static readonly Regex TopicListPattern = new(
        @"^(?:\s*[A-Z][a-zA-Z\s]+){6,}\s*$", // 6+ phrase-like segments on one line
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Academic front matter indicators (thesis, papers)
    private static readonly string[] AcademicFrontMatterIndicators =
    [
        "in partial fulfillment", "requirements for the degree",
        "doctor of philosophy", "master of science", "bachelor of",
        "submitted to", "approved by", "committee member",
        "dissertation", "thesis committee", "graduate school",
        "acknowledgments", "acknowledgements", "dedication",
        "abstract", "table of contents", "list of figures", "list of tables"
    ];

    // Book front matter indicators
    private static readonly string[] BookFrontMatterIndicators =
    [
        "published by", "first published", "printing history",
        "isbn", "library of congress", "cataloging-in-publication",
        "all rights reserved", "no part of this book",
        "cover design", "interior design", "typeset",
        "printed in", "manufactured in", "about the author",
        "other books by", "also by", "dedication", "epigraph",
        "foreword", "preface", "prologue", "introduction"
    ];

    // Main content start indicators
    private static readonly string[] MainContentIndicators =
    [
        // Academic
        "chapter 1", "chapter one", "1. introduction", "1 introduction",
        "section 1", "part 1", "part one",
        // Technical
        "getting started", "overview", "quick start", "installation",
        // Fiction
        "chapter 1", "chapter one", "part one", "book one",
        // General
        "introduction", "background"
    ];

    public FrontMatterDetector(OllamaService? ollama = null, bool verbose = false)
    {
        _ollama = ollama;
        _verbose = verbose;
    }

    /// <summary>
    /// Analyze markdown content and return a profile with filtering recommendations
    /// </summary>
    public async Task<DocumentProfile> AnalyzeAsync(string markdown, CancellationToken ct = default)
    {
        var profile = new DocumentProfile();

        // Step 1: Strip YAML/TOML front matter (always safe)
        markdown = StripMetadataFrontMatter(markdown, profile);

        // Step 2: Detect document type from early content
        var earlyContent = markdown.Length > 5000 ? markdown[..5000] : markdown;
        profile.DetectedType = DetectDocumentType(earlyContent);

        // Step 3: Find junk sections to skip
        var junkRanges = FindJunkRanges(markdown);
        profile.JunkRanges.AddRange(junkRanges);

        // Step 4: Find where main content starts
        profile.MainContentStartIndex = FindMainContentStart(markdown, profile.DetectedType);

        // Step 5: If we have LLM and content is ambiguous, ask sentinel for help
        if (_ollama != null && profile.MainContentStartIndex == 0 && HasAmbiguousFrontMatter(earlyContent))
        {
            var llmProfile = await AnalyzeWithSentinelAsync(earlyContent, ct);
            if (llmProfile != null)
            {
                profile.MainContentStartIndex = llmProfile.MainContentStartIndex;
                profile.SkipPatterns.AddRange(llmProfile.SkipPatterns);
                profile.SentinelUsed = true;
            }
        }

        // Step 6: Build skip patterns based on document type
        AddTypeSpecificSkipPatterns(profile);

        if (_verbose)
        {
            Console.WriteLine($"[FrontMatter] Type: {profile.DetectedType}");
            Console.WriteLine($"[FrontMatter] Main content starts at: {profile.MainContentStartIndex}");
            Console.WriteLine($"[FrontMatter] Junk ranges: {profile.JunkRanges.Count}");
            Console.WriteLine($"[FrontMatter] Skip patterns: {profile.SkipPatterns.Count}");
        }

        return profile;
    }

    /// <summary>
    /// Apply the profile to filter markdown content.
    /// NOTE: The profile indices are relative to markdown AFTER metadata stripping,
    /// since AnalyzeAsync strips metadata before calculating indices.
    /// </summary>
    public string ApplyProfile(string markdown, DocumentProfile profile)
    {
        // Strip metadata front matter first (same as AnalyzeAsync does before calculating indices)
        markdown = StripMetadataFrontMatter(markdown, null);

        // Skip to main content start (index is relative to post-metadata-stripped content)
        if (profile.MainContentStartIndex > 0 && profile.MainContentStartIndex < markdown.Length)
        {
            if (_verbose)
            {
                var skipped = markdown[..profile.MainContentStartIndex];
                var lines = skipped.Split('\n').Length;
                Console.WriteLine($"[FrontMatter] Skipping {lines} lines of front matter");
            }
            markdown = markdown[profile.MainContentStartIndex..];
        }

        // Remove junk ranges (work backwards to preserve indices)
        foreach (var (start, end) in profile.JunkRanges.OrderByDescending(r => r.start))
        {
            if (start >= 0 && end <= markdown.Length && start < end)
            {
                markdown = markdown[..start] + markdown[end..];
            }
        }

        // Apply skip patterns
        foreach (var pattern in profile.SkipPatterns)
        {
            try
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                markdown = regex.Replace(markdown, "");
            }
            catch
            {
                // Invalid regex, skip
            }
        }

        // Apply text cleaning (ligatures, unicode quotes, etc.)
        markdown = TextCleaner.CleanForSummarization(markdown);

        return markdown.Trim();
    }

    /// <summary>
    /// Quick check if content has potential front matter issues
    /// </summary>
    public bool HasFrontMatterToFilter(string markdown)
    {
        // Check more content for junk detection (junk can be anywhere in early pages)
        var earlyLength = Math.Min(markdown.Length, 10000);
        var early = markdown[..earlyLength].ToLowerInvariant();
        var earlyOriginal = markdown[..earlyLength]; // Keep original case for regex checks

        // Check for YAML/TOML front matter
        if (YamlFrontMatter.IsMatch(markdown) || TomlFrontMatter.IsMatch(markdown))
            return true;

        // Check for junk indicators
        if (JunkIndicators.Any(j => early.Contains(j)))
            return true;

        // Check for junk sequences (multi-word spam patterns)
        if (JunkSequences.Any(j => early.Contains(j)))
            return true;
        
        // Check for keyword stuffing (many capitalized words in a row - spam signature)
        if (KeywordStuffingPattern.IsMatch(earlyOriginal))
        {
            var matches = KeywordStuffingPattern.Matches(earlyOriginal);
            // If any match has 6+ capitalized words, it's likely spam
            if (matches.Any(m => m.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 6))
                return true;
        }

        // Check for academic/book front matter
        if (AcademicFrontMatterIndicators.Any(a => early.Contains(a)))
            return true;

        if (BookFrontMatterIndicators.Any(b => early.Contains(b)))
            return true;

        return false;
    }

    private string StripMetadataFrontMatter(string markdown, DocumentProfile? profile)
    {
        var original = markdown;

        // Strip YAML front matter
        var yamlMatch = YamlFrontMatter.Match(markdown);
        if (yamlMatch.Success)
        {
            markdown = markdown[yamlMatch.Length..];
            profile?.MetadataStripped.Add("yaml");
        }

        // Strip TOML front matter
        var tomlMatch = TomlFrontMatter.Match(markdown);
        if (tomlMatch.Success)
        {
            markdown = markdown[tomlMatch.Length..];
            profile?.MetadataStripped.Add("toml");
        }

        return markdown;
    }

    private DocumentProfileType DetectDocumentType(string earlyContent)
    {
        var lower = earlyContent.ToLowerInvariant();
        var scores = new Dictionary<DocumentProfileType, int>
        {
            [DocumentProfileType.Unknown] = 0,
            [DocumentProfileType.AcademicThesis] = 0,
            [DocumentProfileType.AcademicPaper] = 0,
            [DocumentProfileType.TechnicalManual] = 0,
            [DocumentProfileType.Fiction] = 0,
            [DocumentProfileType.NonFictionBook] = 0,
            [DocumentProfileType.WebContent] = 0
        };

        // Academic thesis indicators
        if (lower.Contains("in partial fulfillment") || lower.Contains("requirements for the degree"))
            scores[DocumentProfileType.AcademicThesis] += 10;
        if (lower.Contains("dissertation") || lower.Contains("thesis committee"))
            scores[DocumentProfileType.AcademicThesis] += 5;
        if (lower.Contains("acknowledgments") || lower.Contains("acknowledgements"))
            scores[DocumentProfileType.AcademicThesis] += 3;

        // Academic paper indicators
        if (lower.Contains("abstract") && lower.Contains("keywords"))
            scores[DocumentProfileType.AcademicPaper] += 8;
        if (Regex.IsMatch(lower, @"\[\d+\]")) // Citation style [1], [2]
            scores[DocumentProfileType.AcademicPaper] += 3;
        if (lower.Contains("references") || lower.Contains("bibliography"))
            scores[DocumentProfileType.AcademicPaper] += 2;

        // Technical manual indicators
        if (lower.Contains("installation") || lower.Contains("getting started"))
            scores[DocumentProfileType.TechnicalManual] += 5;
        if (lower.Contains("```") || lower.Contains("code"))
            scores[DocumentProfileType.TechnicalManual] += 3;
        if (lower.Contains("api") || lower.Contains("configuration"))
            scores[DocumentProfileType.TechnicalManual] += 2;

        // Fiction indicators
        if (Regex.IsMatch(lower, @"\bchapter\s+(one|1|i)\b"))
            scores[DocumentProfileType.Fiction] += 5;
        if (lower.Contains("\"") && Regex.IsMatch(lower, @"""[^""]+,""\s*\w+\s+said"))
            scores[DocumentProfileType.Fiction] += 5;
        if (Regex.IsMatch(lower, @"\b(he|she)\s+(walked|looked|said|thought|felt)\b"))
            scores[DocumentProfileType.Fiction] += 3;

        // Non-fiction book indicators
        if (lower.Contains("isbn") || lower.Contains("published by"))
            scores[DocumentProfileType.NonFictionBook] += 5;
        if (lower.Contains("foreword") || lower.Contains("preface"))
            scores[DocumentProfileType.NonFictionBook] += 3;

        // Web content indicators (junk)
        var junkCount = JunkIndicators.Count(j => lower.Contains(j));
        if (junkCount >= 3)
            scores[DocumentProfileType.WebContent] += 10;
        else if (junkCount >= 1)
            scores[DocumentProfileType.WebContent] += junkCount * 2;

        // Return highest scoring type
        var best = scores.OrderByDescending(kv => kv.Value).First();
        return best.Value >= 3 ? best.Key : DocumentProfileType.Unknown;
    }

    private List<(int start, int end)> FindJunkRanges(string markdown)
    {
        var ranges = new List<(int start, int end)>();
        var lines = markdown.Split('\n');
        var currentPos = 0;
        var junkStart = -1;
        var junkLineCount = 0;

        foreach (var line in lines)
        {
            var lineLower = line.ToLowerInvariant();
            var isJunk = false;
            
            // Check 1: Single junk indicator keywords
            if (JunkIndicators.Any(j => lineLower.Contains(j)))
            {
                isJunk = true;
            }
            
            // Check 2: Multiple junk indicators in one line (high density spam)
            if (!isJunk)
            {
                var junkWordCount = JunkIndicators.Count(j => lineLower.Contains(j));
                if (junkWordCount >= 2)
                {
                    isJunk = true;
                }
            }
            
            // Check 3: Junk sequences (multi-word spam patterns)
            if (!isJunk && JunkSequences.Any(s => lineLower.Contains(s)))
            {
                isJunk = true;
            }
            
            // Check 4: Keyword stuffing pattern - many capitalized words in a row
            // This catches spam like "Online Auction Search Engine Optimization Spam Blocking..."
            if (!isJunk && line.Length > 30 && KeywordStuffingPattern.IsMatch(line))
            {
                // Additional validation: count how many capitalized word sequences
                var matches = KeywordStuffingPattern.Matches(line);
                var totalCapitalizedWords = matches.Sum(m => m.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                // If more than 40% of the line is keyword-stuffed capitalized words, it's spam
                var lineWordCount = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (lineWordCount > 0 && (double)totalCapitalizedWords / lineWordCount > 0.4)
                {
                    isJunk = true;
                    if (_verbose) Console.WriteLine($"[FrontMatter] Keyword stuffing detected: {line[..Math.Min(60, line.Length)]}...");
                }
            }
            
            // Check 5: Topic list pattern - lines that are just category lists
            if (!isJunk && line.Length > 50 && TopicListPattern.IsMatch(line))
            {
                isJunk = true;
                if (_verbose) Console.WriteLine($"[FrontMatter] Topic list spam detected: {line[..Math.Min(60, line.Length)]}...");
            }

            if (isJunk)
            {
                if (junkStart < 0) junkStart = currentPos;
                junkLineCount++;
            }
            else if (junkStart >= 0)
            {
                // End of junk section - mark even single lines if they're clearly spam
                if (junkLineCount >= 1)
                {
                    ranges.Add((junkStart, currentPos));
                }
                junkStart = -1;
                junkLineCount = 0;
            }

            currentPos += line.Length + 1; // +1 for newline
        }

        // Handle trailing junk
        if (junkStart >= 0 && junkLineCount >= 1)
        {
            ranges.Add((junkStart, markdown.Length));
        }

        return ranges;
    }

    private int FindMainContentStart(string markdown, DocumentProfileType docType)
    {
        var lines = markdown.Split('\n');
        var currentPos = 0;

        // Type-specific main content patterns
        var patterns = docType switch
        {
            DocumentProfileType.AcademicThesis => new[]
            {
                @"^#+\s*chapter\s*(1|one|i)\b",
                @"^#+\s*1\.?\s*introduction\b",
                @"^#+\s*introduction\b",
                @"^1\.\s*introduction\b"
            },
            DocumentProfileType.AcademicPaper => new[]
            {
                @"^#+\s*1\.?\s*introduction\b",
                @"^#+\s*introduction\b",
                @"^#+\s*background\b",
                @"^1\.\s*introduction\b"
            },
            DocumentProfileType.Fiction => new[]
            {
                @"^#+\s*chapter\s*(1|one|i)\b",
                @"^#+\s*part\s*(1|one|i)\b",
                @"^#+\s*book\s*(1|one|i)\b",
                @"^chapter\s*(1|one|i)\b"
            },
            DocumentProfileType.NonFictionBook => new[]
            {
                @"^#+\s*chapter\s*(1|one|i)\b",
                @"^#+\s*introduction\b",
                @"^#+\s*part\s*(1|one|i)\b"
            },
            DocumentProfileType.TechnicalManual => new[]
            {
                @"^#+\s*getting\s+started\b",
                @"^#+\s*introduction\b",
                @"^#+\s*overview\b",
                @"^#+\s*1\.?\s*"
            },
            _ => MainContentIndicators.Select(i => $@"^#+?\s*{Regex.Escape(i)}").ToArray()
        };

        // Also check for academic front matter end markers
        var frontMatterEndPatterns = new[]
        {
            @"^#+\s*chapter\b",
            @"^\*{3,}$", // *** divider
            @"^-{3,}$",  // --- divider
            @"^={3,}$"   // === divider
        };

        // Track if we're still in obvious front matter
        var inFrontMatter = true;
        var frontMatterEndIndex = 0;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim().ToLowerInvariant();

            // Check if we've passed obvious front matter sections
            if (inFrontMatter)
            {
                var isFrontMatterContent = 
                    AcademicFrontMatterIndicators.Any(a => trimmedLine.Contains(a)) ||
                    BookFrontMatterIndicators.Any(b => trimmedLine.Contains(b));

                if (isFrontMatterContent)
                {
                    frontMatterEndIndex = currentPos + line.Length + 1;
                }

                // Check for front matter end markers
                foreach (var endPattern in frontMatterEndPatterns)
                {
                    if (Regex.IsMatch(line, endPattern, RegexOptions.IgnoreCase))
                    {
                        // Don't mark as main content start yet, but note we left front matter
                        inFrontMatter = false;
                        break;
                    }
                }
            }

            // Check for main content start
            foreach (var pattern in patterns)
            {
                if (Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase))
                {
                    if (_verbose) Console.WriteLine($"[FrontMatter] Found main content start: '{line.Trim()[..Math.Min(50, line.Trim().Length)]}'");
                    return currentPos;
                }
            }

            currentPos += line.Length + 1;
        }

        // If we tracked front matter, return its end
        if (frontMatterEndIndex > 0)
        {
            if (_verbose) Console.WriteLine($"[FrontMatter] Using front matter end index: {frontMatterEndIndex}");
            return frontMatterEndIndex;
        }

        return 0; // No front matter detected
    }

    private bool HasAmbiguousFrontMatter(string earlyContent)
    {
        var lower = earlyContent.ToLowerInvariant();

        // Has some front matter indicators but no clear main content start
        var hasFrontMatterHints = 
            AcademicFrontMatterIndicators.Any(a => lower.Contains(a)) ||
            BookFrontMatterIndicators.Any(b => lower.Contains(b)) ||
            JunkIndicators.Any(j => lower.Contains(j));

        var hasMainContentStart = MainContentIndicators.Any(m => 
            Regex.IsMatch(lower, $@"^#+?\s*{Regex.Escape(m)}", RegexOptions.Multiline));

        return hasFrontMatterHints && !hasMainContentStart;
    }

    private async Task<DocumentProfile?> AnalyzeWithSentinelAsync(string earlyContent, CancellationToken ct)
    {
        if (_ollama == null) return null;

        try
        {
            // Limit content for small model context
            var sample = earlyContent.Length > 1500 ? earlyContent[..1500] : earlyContent;

            var prompt = $"""
                Analyze this document excerpt. Answer these questions briefly:
                1. TYPE: Is this a THESIS, PAPER, MANUAL, FICTION, NONFICTION, or OTHER?
                2. SKIP: What sections should be skipped? List any: copyright, dedication, acknowledgments, table of contents, etc.
                3. START: What phrase or heading marks where the main content begins?

                EXCERPT:
                {sample}

                Answer format (one line each):
                TYPE: [type]
                SKIP: [comma-separated list or "none"]
                START: [phrase or "unknown"]
                """;

            var classifierModel = _ollama.ClassifierModel;
            if (_verbose) Console.WriteLine($"[FrontMatter] Asking sentinel ({classifierModel}) for help...");

            var response = await _ollama.GenerateWithModelAsync(classifierModel, prompt, temperature: 0.1);

            // Parse response
            var profile = new DocumentProfile { SentinelUsed = true };
            var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var upper = line.ToUpperInvariant();
                
                if (upper.StartsWith("TYPE:"))
                {
                    var typeStr = line[5..].Trim().ToUpperInvariant();
                    profile.DetectedType = typeStr switch
                    {
                        var t when t.Contains("THESIS") => DocumentProfileType.AcademicThesis,
                        var t when t.Contains("PAPER") => DocumentProfileType.AcademicPaper,
                        var t when t.Contains("MANUAL") => DocumentProfileType.TechnicalManual,
                        var t when t.Contains("FICTION") => DocumentProfileType.Fiction,
                        var t when t.Contains("NONFICTION") || t.Contains("NON-FICTION") => DocumentProfileType.NonFictionBook,
                        _ => DocumentProfileType.Unknown
                    };
                }
                else if (upper.StartsWith("SKIP:"))
                {
                    var skipStr = line[5..].Trim();
                    if (!skipStr.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        var skipItems = skipStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var item in skipItems)
                        {
                            var clean = item.Trim().ToLowerInvariant();
                            if (!string.IsNullOrEmpty(clean))
                            {
                                // Convert to regex pattern
                                profile.SkipPatterns.Add($@"^#+\s*{Regex.Escape(clean)}.*?(?=^#+|\Z)");
                            }
                        }
                    }
                }
                else if (upper.StartsWith("START:"))
                {
                    var startStr = line[6..].Trim();
                    if (!startStr.Equals("unknown", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(startStr))
                    {
                        // Find this phrase in the content
                        var idx = earlyContent.IndexOf(startStr, StringComparison.OrdinalIgnoreCase);
                        if (idx > 0)
                        {
                            profile.MainContentStartIndex = idx;
                            if (_verbose) Console.WriteLine($"[FrontMatter] Sentinel found start at: '{startStr}'");
                        }
                    }
                }
            }

            if (_verbose) Console.WriteLine($"[FrontMatter] Sentinel detected: {profile.DetectedType}");
            return profile;
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"[FrontMatter] Sentinel failed: {ex.Message}");
            return null;
        }
    }

    private void AddTypeSpecificSkipPatterns(DocumentProfile profile)
    {
        switch (profile.DetectedType)
        {
            case DocumentProfileType.AcademicThesis:
                // Skip common thesis front matter sections
                profile.SkipPatterns.Add(@"^#+\s*acknowledgments?.*?(?=^#+|\Z)");
                profile.SkipPatterns.Add(@"^#+\s*dedication.*?(?=^#+|\Z)");
                profile.SkipPatterns.Add(@"^#+\s*vita.*?(?=^#+|\Z)");
                profile.SkipPatterns.Add(@"^#+\s*curriculum\s+vitae.*?(?=^#+|\Z)");
                break;

            case DocumentProfileType.AcademicPaper:
                // Skip abstract (already summarized), keywords
                profile.SkipPatterns.Add(@"^#+\s*keywords?:.*$");
                break;

            case DocumentProfileType.NonFictionBook:
            case DocumentProfileType.Fiction:
                // Skip publication info, about author at end
                profile.SkipPatterns.Add(@"^#+\s*about\s+the\s+author.*?(?=^#+|\Z)");
                profile.SkipPatterns.Add(@"^#+\s*also\s+by.*?(?=^#+|\Z)");
                profile.SkipPatterns.Add(@"^#+\s*other\s+books.*?(?=^#+|\Z)");
                break;

            case DocumentProfileType.WebContent:
                // Aggressively filter web junk
                profile.SkipPatterns.Add(@"(?i)subscribe\s+to\s+our.*$");
                profile.SkipPatterns.Add(@"(?i)follow\s+us\s+on.*$");
                profile.SkipPatterns.Add(@"(?i)share\s+this\s+article.*$");
                break;
        }
    }
}

/// <summary>
/// Document type profile for filtering decisions
/// </summary>
public enum DocumentProfileType
{
    Unknown,
    AcademicThesis,
    AcademicPaper,
    TechnicalManual,
    Fiction,
    NonFictionBook,
    WebContent
}

/// <summary>
/// Profile containing front matter detection results and filtering rules
/// </summary>
public class DocumentProfile
{
    public DocumentProfileType DetectedType { get; set; } = DocumentProfileType.Unknown;
    public int MainContentStartIndex { get; set; } = 0;
    public List<(int start, int end)> JunkRanges { get; } = new();
    public List<string> SkipPatterns { get; } = new();
    public List<string> MetadataStripped { get; } = new();
    public bool SentinelUsed { get; set; }
}
