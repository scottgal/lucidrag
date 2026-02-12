using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Migrations;

/// <summary>
///     Creates API key management tables for SaaS:
///     - api_keys: API key storage with SHA256 hash validation
///     - api_key_indexing_sources: Per-key crawl source with scheduling fields
///     - api_key_read_domains: Allowed domains per key (up to 5)
/// </summary>
public partial class AddApiKeyTables : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "api_keys",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                KeyPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                NormalizedOwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                Plan = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                AllowChat = table.Column<bool>(type: "boolean", nullable: false),
                AllowSearch = table.Column<bool>(type: "boolean", nullable: false),
                RateLimitPerMinute = table.Column<int>(type: "integer", nullable: false),
                RateLimitPerDay = table.Column<int>(type: "integer", nullable: false),
                TotalRequests = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CollectionId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_keys", x => x.Id);
                table.ForeignKey(
                    name: "FK_api_keys_collections_CollectionId",
                    column: x => x.CollectionId,
                    principalTable: "collections",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "api_key_indexing_sources",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                SourceValue = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                MaxDocuments = table.Column<int>(type: "integer", nullable: false),
                DocumentCount = table.Column<int>(type: "integer", nullable: false),
                LastCrawledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CrawlStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                // Background indexer scheduling
                NextScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                TriggerCrawlNow = table.Column<bool>(type: "boolean", nullable: false),
                ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                LastError = table.Column<string>(type: "text", nullable: true),
                // Conditional re-crawl (ETag / If-Modified-Since)
                ETag = table.Column<string>(type: "text", nullable: true),
                LastModifiedHeader = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_key_indexing_sources", x => x.Id);
                table.ForeignKey(
                    name: "FK_api_key_indexing_sources_api_keys_ApiKeyId",
                    column: x => x.ApiKeyId,
                    principalTable: "api_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "api_key_read_domains",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_key_read_domains", x => x.Id);
                table.ForeignKey(
                    name: "FK_api_key_read_domains_api_keys_ApiKeyId",
                    column: x => x.ApiKeyId,
                    principalTable: "api_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        // Indexes for api_keys
        migrationBuilder.CreateIndex(
            name: "IX_api_keys_CollectionId",
            table: "api_keys",
            column: "CollectionId");

        migrationBuilder.CreateIndex(
            name: "IX_api_keys_KeyHash",
            table: "api_keys",
            column: "KeyHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_api_keys_KeyPrefix",
            table: "api_keys",
            column: "KeyPrefix",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_api_keys_NormalizedOwnerEmail",
            table: "api_keys",
            column: "NormalizedOwnerEmail");

        migrationBuilder.CreateIndex(
            name: "IX_api_keys_UserId",
            table: "api_keys",
            column: "UserId");

        // Indexes for api_key_indexing_sources
        migrationBuilder.CreateIndex(
            name: "IX_api_key_indexing_sources_ApiKeyId",
            table: "api_key_indexing_sources",
            column: "ApiKeyId",
            unique: true);

        // Indexes for api_key_read_domains
        migrationBuilder.CreateIndex(
            name: "IX_api_key_read_domains_ApiKeyId_Domain",
            table: "api_key_read_domains",
            columns: new[] { "ApiKeyId", "Domain" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "api_key_read_domains");
        migrationBuilder.DropTable(name: "api_key_indexing_sources");
        migrationBuilder.DropTable(name: "api_keys");
    }
}
