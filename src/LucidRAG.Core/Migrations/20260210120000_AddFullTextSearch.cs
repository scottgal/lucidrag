using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Migrations;

/// <summary>
///     Adds PostgreSQL full-text search infrastructure:
///     - content_tokens tsvector GENERATED ALWAYS column on evidence_artifacts
///     - GIN index for fast FTS queries
///     - corpus_stats materialized view for BM25 statistics
///
///     These use raw SQL because EF Core doesn't natively support tsvector generated columns.
///     For SQLite deployments, this migration is a no-op (guarded by provider check).
/// </summary>
public partial class AddFullTextSearch : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Only apply to PostgreSQL — SQLite doesn't support tsvector
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            return;

        // Add stored generated tsvector column for full-text search
        migrationBuilder.Sql("""
            ALTER TABLE evidence_artifacts
            ADD COLUMN content_tokens tsvector
            GENERATED ALWAYS AS (to_tsvector('english', COALESCE("Content", ''))) STORED;
            """);

        // GIN index for fast FTS queries
        migrationBuilder.Sql("""
            CREATE INDEX idx_evidence_fts ON evidence_artifacts USING GIN(content_tokens);
            """);

        // Corpus stats materialized view (for BM25 statistics monitoring)
        migrationBuilder.Sql("""
            CREATE MATERIALIZED VIEW IF NOT EXISTS corpus_stats AS
            SELECT
                COUNT(*) as total_docs,
                AVG(length("Content")) as avg_doc_length
            FROM evidence_artifacts
            WHERE "Content" IS NOT NULL;
            """);

        // Unique index required for CONCURRENTLY refresh
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX IF NOT EXISTS idx_corpus_stats_unique ON corpus_stats (total_docs);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            return;

        migrationBuilder.Sql("DROP MATERIALIZED VIEW IF EXISTS corpus_stats;");
        migrationBuilder.Sql("DROP INDEX IF EXISTS idx_evidence_fts;");
        migrationBuilder.Sql("ALTER TABLE evidence_artifacts DROP COLUMN IF EXISTS content_tokens;");
    }
}
