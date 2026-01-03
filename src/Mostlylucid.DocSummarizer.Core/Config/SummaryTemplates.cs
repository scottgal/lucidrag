using System.Text.Json.Serialization;

namespace Mostlylucid.DocSummarizer.Config;

/// <summary>
///     Summary template configuration - controls LLM prompts and output format
/// </summary>
public class SummaryTemplate
{
    /// <summary>
    ///     Template name for identification
    /// </summary>
    public string Name { get; set; } = "default";

    /// <summary>
    ///     Description of what this template produces
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    ///     Target word count for executive summary (0 = no limit)
    /// </summary>
    public int TargetWords { get; set; }

    /// <summary>
    ///     Maximum bullet points for bullet-style output (0 = no limit)
    /// </summary>
    public int MaxBullets { get; set; }

    /// <summary>
    ///     Number of paragraphs for executive summary (0 = auto)
    /// </summary>
    public int Paragraphs { get; set; }

    /// <summary>
    ///     Output style: Prose, Bullets, Mixed, CitationsOnly
    /// </summary>
    public OutputStyle OutputStyle { get; set; } = OutputStyle.Prose;

    /// <summary>
    ///     Include topic breakdowns
    /// </summary>
    public bool IncludeTopics { get; set; } = true;

    /// <summary>
    ///     Include source citations [chunk-X]
    /// </summary>
    public bool IncludeCitations { get; set; } = true;

    /// <summary>
    ///     Include open questions / areas for follow-up
    /// </summary>
    public bool IncludeQuestions { get; set; }

    /// <summary>
    ///     Include processing trace/metadata
    /// </summary>
    public bool IncludeTrace { get; set; }

    /// <summary>
    ///     Custom prompt prefix for executive summary generation
    /// </summary>
    public string? ExecutivePrompt { get; set; }

    /// <summary>
    ///     Custom prompt for topic synthesis
    /// </summary>
    public string? TopicPrompt { get; set; }

    /// <summary>
    ///     Custom prompt for chunk summarization
    /// </summary>
    public string? ChunkPrompt { get; set; }

    /// <summary>
    ///     Tone: Professional, Casual, Academic, Technical
    /// </summary>
    public SummaryTone Tone { get; set; } = SummaryTone.Professional;

    /// <summary>
    ///     Audience level: Executive, Technical, General
    /// </summary>
    public AudienceLevel Audience { get; set; } = AudienceLevel.General;

    /// <summary>
    ///     Include coverage metadata (disclaimer + footer).
    ///     Set to false for clean prose output without any metadata.
    /// </summary>
    public bool IncludeCoverageMetadata { get; set; } = true;

    /// <summary>
    ///     Get the LLM prompt for executive summary based on template settings
    /// </summary>
    public string GetExecutivePrompt(string topicSummaries, string? focus)
    {
        if (!string.IsNullOrEmpty(ExecutivePrompt))
            return ExecutivePrompt
                .Replace("{topics}", topicSummaries)
                .Replace("{focus}", focus ?? "");

        var wordGuide = TargetWords > 0 ? $"in approximately {TargetWords} words" : "";
        var paragraphGuide = Paragraphs > 0 ? $"using {Paragraphs} paragraph(s)" : "";
        var styleGuide = OutputStyle switch
        {
            OutputStyle.Bullets => "as bullet points",
            OutputStyle.Mixed => "with a brief intro followed by bullet points",
            OutputStyle.CitationsOnly => "listing only the key citations and their relevance",
            _ => "in prose form"
        };
        var toneGuide = Tone switch
        {
            SummaryTone.Casual => "Use a conversational, accessible tone.",
            SummaryTone.Academic => "Use formal academic language with precise terminology.",
            SummaryTone.Technical => "Use technical language appropriate for domain experts.",
            _ => "Use clear, professional language."
        };
        var audienceGuide = Audience switch
        {
            AudienceLevel.Executive => "Write for busy executives who need key takeaways quickly.",
            AudienceLevel.Technical => "Include technical details relevant to practitioners.",
            _ => "Write for a general professional audience."
        };

        var citationGuide = IncludeCitations
            ? "IMPORTANT: Include [chunk-N] citations after each key fact to show sources."
            : "";

        return $"""
                {(focus != null ? $"Focus: {focus}\n" : "")}Topics covered:
                {topicSummaries}

                Write an executive summary {wordGuide} {paragraphGuide} {styleGuide}.
                {toneGuide}
                {audienceGuide}
                {citationGuide}
                """;
    }

    /// <summary>
    ///     Get the LLM prompt for topic synthesis
    /// </summary>
    public string GetTopicPrompt(string topic, string context, string? focus)
    {
        if (!string.IsNullOrEmpty(TopicPrompt))
            return TopicPrompt
                .Replace("{topic}", topic)
                .Replace("{context}", context)
                .Replace("{focus}", focus ?? "");

        var bulletGuide = MaxBullets > 0 ? $"Write {MaxBullets} bullet points" : "Write 2-3 bullet points";
        var citationGuide = IncludeCitations
            ? "End each bullet with the source citation in format [chunk-N]."
            : "";

        return $"""
                Topic: {topic}
                {(focus != null ? $"Focus: {focus}\n" : "")}
                Sources:
                {context}

                {bulletGuide} summarizing this topic.
                {citationGuide}
                """;
    }

    /// <summary>
    ///     Get the LLM prompt for chunk summarization
    /// </summary>
    public string GetChunkPrompt(string heading, string content)
    {
        if (!string.IsNullOrEmpty(ChunkPrompt))
            return ChunkPrompt
                .Replace("{heading}", heading)
                .Replace("{content}", content);

        if (Name.Equals("bookreport", StringComparison.OrdinalIgnoreCase))
        {
            return $"""
                    Section: {heading}

                    Content:
                    {content}

                    Summarize this section in 2-3 tight sentences (≤90 words). Write in third person, no dialogue, no first-person voice. Cover only the key beats and named characters; avoid paraphrasing line-by-line. Keep prose flowing (no bullets) and be concise.
                    """;
        }

        var bulletGuide = MaxBullets > 0 ? $"{MaxBullets} bullet points" : "2-4 bullet points";

        return $"""
                Section: {heading}

                Content:
                {content}

                Summarize this section in {bulletGuide}. Be specific and preserve key facts.
                """;
    }


    /// <summary>
    ///     Built-in templates
    /// </summary>
    public static class Presets
    {
        /// <summary>
        ///     Default template - balanced prose with topics, auto-adapts to document type
        /// </summary>
        public static SummaryTemplate Default => new()
        {
            Name = "default",
            Description = "Balanced summary with executive overview and topic breakdowns",
            TargetWords = 300,
            Paragraphs = 2,
            OutputStyle = OutputStyle.Prose,
            IncludeTopics = true,
            IncludeCitations = true,
            IncludeQuestions = false,
            IncludeTrace = false,
            Tone = SummaryTone.Professional,
            Audience = AudienceLevel.General,
            ExecutivePrompt = """
                              {topics}

                              Summarize in 2 paragraphs using ONLY facts above:

                              Paragraph 1: What is this? Main subject, key actors/components. Be specific.
                              Paragraph 2: What happens/what does it do? Key events, findings, or functions.

                              Rules:
                              - Use exact names from text (people, places, functions, classes)
                              - NO "this document discusses" or "the significance lies in"
                              - NO interpretation or outside knowledge
                              - Cite [chunk-N] for key claims
                              """
        };

        /// <summary>
        ///     Brief - quick 2-3 sentence summary
        /// </summary>
        public static SummaryTemplate Brief => new()
        {
            Name = "brief",
            Description = "Quick 2-3 sentence summary for fast scanning",
            TargetWords = 50,
            Paragraphs = 1,
            OutputStyle = OutputStyle.Prose,
            IncludeTopics = false,
            IncludeCitations = false,
            IncludeQuestions = false,
            IncludeTrace = false,
            IncludeCoverageMetadata = false,
            Tone = SummaryTone.Professional,
            Audience = AudienceLevel.Executive,
            ExecutivePrompt = """
                              {topics}

                              Write 2-3 sentences summarizing the main point of this document.
                              Use ONLY facts from the text above. Be specific. No hedging.
                              STRICT: Maximum 50 words total.
                              """
        };

        /// <summary>
        ///     One-liner - single sentence
        /// </summary>
        public static SummaryTemplate OneLiner => new()
        {
            Name = "oneliner",
            Description = "Single sentence summary",
            TargetWords = 25,
            Paragraphs = 1,
            OutputStyle = OutputStyle.Prose,
            IncludeTopics = false,
            IncludeCitations = false,
            IncludeQuestions = false,
            IncludeTrace = false,
            IncludeCoverageMetadata = false,
            Tone = SummaryTone.Professional,
            Audience = AudienceLevel.Executive,
            ExecutivePrompt = """
                              {topics}

                              Write ONE sentence (maximum 25 words) capturing the main point.
                              Use specific facts from text. No hedging. No "this document discusses".
                              """
        };

        /// <summary>
        ///     Prose - clean multi-paragraph summary without any metadata or formatting
        /// </summary>
        public static SummaryTemplate Prose => new()
        {
            Name = "prose",
            Description = "Clean multi-paragraph prose summary - no metadata, just content",
            TargetWords = 400,
            Paragraphs = 4,
            OutputStyle = OutputStyle.Prose,
            IncludeTopics = false,
            IncludeCitations = false,
            IncludeQuestions = false,
            IncludeTrace = false,
            IncludeCoverageMetadata = false,
            Tone = SummaryTone.Professional,
            Audience = AudienceLevel.General,
            ExecutivePrompt = """
                              {topics}

                              Write a clear, well-structured summary in 3-5 paragraphs (~400 words).

                              Structure:
                              - Paragraph 1: What is this about? The main subject and context.
                              - Paragraph 2-3: Key points, findings, or events. What matters most.
                              - Paragraph 4: Conclusions, implications, or outcomes (if applicable).

                              Rules:
                              - Write flowing prose, not bullet points
                              - Use specific names, terms, and facts from the text
                              - No citations, references, or metadata
                              - No "this document discusses" or "the author states"
                              - Write as if explaining to someone who hasn't read the source
                              - Be informative and direct
                              """
        };

        /// <summary>
        ///     Bullets - key points only
        /// </summary>
        public static SummaryTemplate Bullets => new()
        {
            Name = "bullets",
            Description = "Bullet point list of key takeaways",
            MaxBullets = 7,
            OutputStyle = OutputStyle.Bullets,
            IncludeTopics = false,
            IncludeCitations = true,
            IncludeQuestions = false,
            IncludeTrace = false,
            Tone = SummaryTone.Professional,
            Audience = AudienceLevel.General,
            ExecutivePrompt = """
                              {topics}

                              List 5-7 key points as bullets. For each:
                              - Start with action verb
                              - State specific fact from text (names, numbers, outcomes)
                              - End with [chunk-N] citation

                              ONLY facts from text above. No interpretation. No hedging.
                              """
        };

        /// <summary>
        ///     Executive - for leadership briefings
        /// </summary>
        public static SummaryTemplate Executive => new()
        {
            Name = "executive",
            Description = "Executive briefing format with recommendations",
            TargetWords = 150,
            Paragraphs = 1,
            MaxBullets = 3,
            OutputStyle = OutputStyle.Mixed,
            IncludeTopics = false,
            IncludeCitations = false,
            IncludeQuestions = false,
            IncludeTrace = false,
            Tone = SummaryTone.Professional,
            Audience = AudienceLevel.Executive,
            ExecutivePrompt = """
                              {topics}

                              Write an executive briefing with:
                              1. One paragraph overview (50 words max)
                              2. Three key takeaways as bullets
                              3. One recommended action

                              Be direct and actionable. No jargon.
                              """
        };

        /// <summary>
        ///     Detailed - comprehensive with all sections
        /// </summary>
        public static SummaryTemplate Detailed => new()
        {
            Name = "detailed",
            Description = "Comprehensive summary with full topic breakdowns",
            TargetWords = 1000,
            Paragraphs = 5,
            OutputStyle = OutputStyle.Prose,
            IncludeTopics = true,
            IncludeCitations = true,
            IncludeQuestions = true,
            IncludeTrace = true,
            Tone = SummaryTone.Professional,
            Audience = AudienceLevel.General
        };

        /// <summary>
        ///     Technical - for technical documentation, code, APIs
        /// </summary>
        public static SummaryTemplate Technical => new()
        {
            Name = "technical",
            Description = "Technical summary preserving implementation details",
            TargetWords = 350,
            OutputStyle = OutputStyle.Mixed,
            IncludeTopics = true,
            IncludeCitations = true,
            IncludeQuestions = false,
            IncludeTrace = false,
            Tone = SummaryTone.Technical,
            Audience = AudienceLevel.Technical,
            ExecutivePrompt = """
                              {topics}

                              Technical summary using ONLY information from text above:

                              **Purpose**: What does this do? What problem does it solve? (2-3 sentences)

                              **Components**: Key classes, functions, APIs, or features mentioned:
                              - [component] → [what it does]
                              - (list 3-5 from text)

                              **Usage**: How to use it (if described). Config options, parameters.

                              **Requirements**: Dependencies, prerequisites, limitations mentioned.

                              Use exact names from text. Include code terms. Cite [chunk-N].
                              NO assumptions about implementation not stated in text.
                              """
        };

        /// <summary>
        ///     Academic - formal scholarly style
        /// </summary>
        public static SummaryTemplate Academic => new()
        {
            Name = "academic",
            Description = "Academic abstract format",
            TargetWords = 250,
            Paragraphs = 1,
            OutputStyle = OutputStyle.Prose,
            IncludeTopics = false,
            IncludeCitations = true,
            IncludeQuestions = false,
            IncludeTrace = false,
            Tone = SummaryTone.Academic,
            Audience = AudienceLevel.Technical,
            ExecutivePrompt = """
                              {topics}

                              Write an academic abstract using ONLY claims from text above:

                              Background: Context and motivation stated in text. (1-2 sentences)
                              Objective: Purpose or research question. (1 sentence)
                              Method: Approach or methodology described. (1-2 sentences)
                              Results: Key findings with specific data/outcomes. (2-3 sentences)
                              Conclusion: Implications or significance claimed. (1-2 sentences)

                              Formal language. Cite [chunk-N]. NO claims not in source text.
                              """
        };

        /// <summary>
        ///     Citations - just the references
        /// </summary>
        public static SummaryTemplate Citations => new()
        {
            Name = "citations",
            Description = "List of key passages with citations only",
            OutputStyle = OutputStyle.CitationsOnly,
            IncludeTopics = false,
            IncludeCitations = true,
            IncludeQuestions = false,
            IncludeTrace = false,
            Tone = SummaryTone.Professional,
            Audience = AudienceLevel.General,
            ExecutivePrompt = """
                              {topics}

                              List the 10 most important quotes or facts from this document.
                              Format each as: "Quote or fact" [chunk-X]
                              Order by importance. Include the source citation for each.
                              """
        };

        /// <summary>
        ///     Book Report - classic book report style summary
        /// </summary>
        public static SummaryTemplate BookReport => new()
        {
            Name = "bookreport",
            Description = "Human-style book report with setting, characters, plot, and themes",
            TargetWords = 500,
            Paragraphs = 5,
            OutputStyle = OutputStyle.Prose,
            IncludeTopics = false,
            IncludeCitations = false,
            IncludeQuestions = false,
            IncludeTrace = false,
            IncludeCoverageMetadata = false,
            Tone = SummaryTone.Casual,
            Audience = AudienceLevel.General,
            ExecutivePrompt = """
                              {topics}

                              Write a book report based ONLY on the text segments above.

                              CRITICAL ANTI-HALLUCINATION RULES:
                              - You have LIMITED excerpts, not the full book
                              - ONLY describe scenes/events explicitly shown in segments above
                              - ONLY mention characters whose names appear in the text above
                              - If you don't see enough plot details, say "the excerpts show..." 
                              - Do NOT fill gaps with knowledge of the book from elsewhere
                              - Do NOT invent character relationships not stated above
                              - If unsure about a detail, OMIT it rather than guess

                              Structure (~400 words):
                              1. Overview: Title/author if visible. Genre based on content. (2 sentences)
                              2. Setting: Only locations/times explicitly mentioned. (2 sentences)
                              3. Characters: Only those named in text above with roles shown. (1 paragraph)
                              4. What happens: Describe ONLY scenes shown in excerpts. (2 paragraphs)
                              5. Themes: Based on what's visible in excerpts. (1 paragraph)

                              Write in third person past tense. No dialogue quotes.
                              """
        };

        /// <summary>
        ///     Meeting Notes - formatted for meeting follow-up
        /// </summary>
        public static SummaryTemplate MeetingNotes => new()
        {
            Name = "meeting",
            Description = "Meeting notes format with decisions, actions, and follow-ups",
            TargetWords = 200,
            MaxBullets = 10,
            OutputStyle = OutputStyle.Mixed,
            IncludeTopics = false,
            IncludeCitations = true,
            IncludeQuestions = true,
            IncludeTrace = false,
            Tone = SummaryTone.Professional,
            Audience = AudienceLevel.General,
            ExecutivePrompt = """
                              {topics}

                              Format this as meeting notes:

                              **Summary**: One paragraph overview of what was discussed.

                              **Key Decisions**:
                              - List any decisions made or conclusions reached

                              **Action Items**:
                              - List any tasks, assignments, or next steps mentioned
                              - Include who is responsible if mentioned

                              **Open Questions**:
                              - List any unresolved issues or questions that need follow-up

                              Be concise and actionable. Include source citations.
                              """
        };

        /// <summary>
        ///     Strict - token-efficient with hard constraints, no hedging
        /// </summary>
        public static SummaryTemplate Strict => new()
        {
            Name = "strict",
            Description = "Token-efficient summary with hard constraints - 3 bullets max, no hedging",
            TargetWords = 60,
            MaxBullets = 3,
            Paragraphs = 0,
            OutputStyle = OutputStyle.Bullets,
            IncludeTopics = false,
            IncludeCitations = true,
            IncludeQuestions = false,
            IncludeTrace = false,
            IncludeCoverageMetadata = false,
            Tone = SummaryTone.Professional,
            Audience = AudienceLevel.Executive,
            ExecutivePrompt = """
                              {topics}

                              OUTPUT CONSTRAINTS (MUST follow exactly):
                              - EXACTLY 3 bullet points
                              - Each bullet ≤20 words
                              - Total ≤60 words
                              - NO repeated names
                              - Each bullet = ONE distinct insight

                              CONTENT RULES:
                              - Lead with highest-confidence facts only
                              - If unsure, OMIT - do NOT hedge
                              - NO phrases like "appears to", "seems", "possibly", "likely", "assuming"
                              - Synthesize insights, do NOT restate facts
                              """
        };

        /// <summary>
        ///     List all available template names
        /// </summary>
        public static IReadOnlyList<string> AvailableTemplates => new[]
        {
            "default", "prose", "brief", "oneliner", "bullets", "executive", "detailed", "technical", "academic", "citations", "bookreport", "meeting", "strict"
        };

        /// <summary>
        ///     Get template by name
        /// </summary>
        public static SummaryTemplate GetByName(string name)
        {
            return name.ToLowerInvariant() switch
            {
                "brief" => Brief,
                "oneliner" or "one-liner" => OneLiner,
                "prose" or "plain" or "clean" => Prose,
                "bullets" or "bullet" => Bullets,
                "executive" or "exec" => Executive,
                "detailed" or "full" => Detailed,
                "technical" or "tech" => Technical,
                "academic" => Academic,
                "citations" or "refs" => Citations,
                "bookreport" or "book-report" or "book" => BookReport,
                "meeting" or "notes" or "meetingnotes" => MeetingNotes,
                "strict" or "efficient" or "tight" => Strict,
                _ => Default
            };
        }
    }
}

/// <summary>
///     Output style for summaries
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutputStyle>))]
public enum OutputStyle
{
    /// <summary>
    ///     Flowing prose paragraphs
    /// </summary>
    Prose,

    /// <summary>
    ///     Bullet point list
    /// </summary>
    Bullets,

    /// <summary>
    ///     Mix of prose intro with bullet points
    /// </summary>
    Mixed,

    /// <summary>
    ///     Just citations/references
    /// </summary>
    CitationsOnly
}

/// <summary>
///     Tone for summary writing
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SummaryTone>))]
public enum SummaryTone
{
    /// <summary>
    ///     Clear, professional business language
    /// </summary>
    Professional,

    /// <summary>
    ///     Conversational, accessible
    /// </summary>
    Casual,

    /// <summary>
    ///     Formal academic language
    /// </summary>
    Academic,

    /// <summary>
    ///     Technical/domain-specific language
    /// </summary>
    Technical
}

/// <summary>
///     Target audience for summary
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AudienceLevel>))]
public enum AudienceLevel
{
    /// <summary>
    ///     General professional audience
    /// </summary>
    General,

    /// <summary>
    ///     Senior leadership / executives
    /// </summary>
    Executive,

    /// <summary>
    ///     Technical practitioners
    /// </summary>
    Technical
}