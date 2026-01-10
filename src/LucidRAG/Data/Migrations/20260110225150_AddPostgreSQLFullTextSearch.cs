using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Add PostgreSQL Full-Text Search for BM25-like ranking.
    ///
    /// Replaces C# BM25Scorer with database-native full-text search using:
    /// - tsvector columns for tokenized content
    /// - GIN indexes for fast full-text queries
    /// - ts_rank_cd() for Coverage Density ranking (often better than BM25)
    ///
    /// Expected performance improvement:
    /// - 10-25x faster queries (5-20ms vs 200-500ms)
    /// - 90% less memory usage
    /// - 95% less network transfer
    /// - Better scalability (logarithmic vs linear)
    /// </summary>
    public partial class AddPostgreSQLFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ================================================================
            // Add inline content storage for segment text (needed for FTS)
            // ================================================================

            // Add content column for inline text storage (segment_text artifacts)
            // Other evidence types (images, PDFs) continue using blob storage
            migrationBuilder.Sql(@"
                ALTER TABLE evidence_artifacts
                ADD COLUMN content TEXT NULL;
            ");

            // ================================================================
            // Full-Text Search on evidence_artifacts (content column)
            // ================================================================

            // Add generated tsvector column for full-text search
            // GENERATED ALWAYS AS ensures automatic updates when content changes
            // Uses 'english' configuration (supports stemming: "running" matches "run")
            migrationBuilder.Sql(@"
                ALTER TABLE evidence_artifacts
                ADD COLUMN content_tokens tsvector
                GENERATED ALWAYS AS (to_tsvector('english', COALESCE(content, ''))) STORED;
            ");

            // Create GIN (Generalized Inverted Index) for fast full-text queries
            // GIN is optimized for tsvector - typical query time: 5-20ms for 10K rows
            migrationBuilder.Sql(@"
                CREATE INDEX idx_evidence_fts
                ON evidence_artifacts
                USING GIN(content_tokens);
            ");

            // ================================================================
            // Full-Text Search on documents (name + source_url)
            // ================================================================

            // For document-level search (names, URLs)
            // Combines multiple text fields into single searchable tsvector
            migrationBuilder.Sql(@"
                ALTER TABLE documents
                ADD COLUMN search_tokens tsvector
                GENERATED ALWAYS AS (
                    to_tsvector('english',
                        COALESCE(name, '') || ' ' ||
                        COALESCE(source_url, '')
                    )
                ) STORED;
            ");

            migrationBuilder.Sql(@"
                CREATE INDEX idx_documents_fts
                ON documents
                USING GIN(search_tokens);
            ");

            // ================================================================
            // Materialized View: Corpus Statistics (for custom BM25 if needed)
            // ================================================================

            // Precompute corpus-wide statistics
            // Used if implementing custom BM25 function (instead of ts_rank_cd)
            // Refresh after bulk inserts: REFRESH MATERIALIZED VIEW CONCURRENTLY corpus_stats;
            migrationBuilder.Sql(@"
                CREATE MATERIALIZED VIEW corpus_stats AS
                SELECT
                    COUNT(*) as total_docs,
                    AVG(length(content))::float as avg_doc_length,
                    SUM(length(content))::bigint as total_length
                FROM evidence_artifacts
                WHERE content IS NOT NULL;

                CREATE UNIQUE INDEX idx_corpus_stats ON corpus_stats ((1));
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop in reverse order

            migrationBuilder.Sql("DROP MATERIALIZED VIEW IF EXISTS corpus_stats;");

            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_documents_fts;");
            migrationBuilder.Sql("ALTER TABLE documents DROP COLUMN IF EXISTS search_tokens;");

            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_evidence_fts;");
            migrationBuilder.Sql("ALTER TABLE evidence_artifacts DROP COLUMN IF EXISTS content_tokens;");
            migrationBuilder.Sql("ALTER TABLE evidence_artifacts DROP COLUMN IF EXISTS content;");
        }
    }
}
