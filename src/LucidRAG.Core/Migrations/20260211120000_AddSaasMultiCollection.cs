using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Migrations;

/// <summary>
///     Adds SaaS multi-collection support, HMAC signing secrets, query logging, and usage rollups:
///     - api_keys.SigningSecret: Per-key HMAC signing secret
///     - api_key_collection_links: Many-to-many between keys and collections
///     - saas_query_logs: Per-query audit log
///     - saas_usage_rollups: Pre-aggregated daily stats
///     - AspNetUsers.HasCompletedOnboarding: Onboarding tracking
/// </summary>
public partial class AddSaasMultiCollection : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Add SigningSecret to api_keys
        migrationBuilder.AddColumn<string>(
            name: "SigningSecret",
            table: "api_keys",
            type: "text",
            nullable: true);

        // Add HasCompletedOnboarding to AspNetUsers
        migrationBuilder.AddColumn<bool>(
            name: "HasCompletedOnboarding",
            table: "AspNetUsers",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        // Create api_key_collection_links table
        migrationBuilder.CreateTable(
            name: "api_key_collection_links",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_key_collection_links", x => x.Id);
                table.ForeignKey(
                    name: "FK_api_key_collection_links_api_keys_ApiKeyId",
                    column: x => x.ApiKeyId,
                    principalTable: "api_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_api_key_collection_links_collections_CollectionId",
                    column: x => x.CollectionId,
                    principalTable: "collections",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_api_key_collection_links_ApiKeyId_CollectionId",
            table: "api_key_collection_links",
            columns: new[] { "ApiKeyId", "CollectionId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_api_key_collection_links_CollectionId",
            table: "api_key_collection_links",
            column: "CollectionId");

        // Create saas_query_logs table
        migrationBuilder.CreateTable(
            name: "saas_query_logs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                QueryText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                QueryType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                SearchMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                ResultCount = table.Column<int>(type: "integer", nullable: false),
                TotalTimeMs = table.Column<int>(type: "integer", nullable: false),
                RetrievalTimeMs = table.Column<int>(type: "integer", nullable: true),
                LlmTimeMs = table.Column<int>(type: "integer", nullable: true),
                Success = table.Column<bool>(type: "boolean", nullable: false),
                ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                RequestDomain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_saas_query_logs", x => x.Id);
                table.ForeignKey(
                    name: "FK_saas_query_logs_api_keys_ApiKeyId",
                    column: x => x.ApiKeyId,
                    principalTable: "api_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_saas_query_logs_ApiKeyId_CreatedAt",
            table: "saas_query_logs",
            columns: new[] { "ApiKeyId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_saas_query_logs_CountryCode",
            table: "saas_query_logs",
            column: "CountryCode");

        migrationBuilder.CreateIndex(
            name: "IX_saas_query_logs_QueryType",
            table: "saas_query_logs",
            column: "QueryType");

        // Create saas_usage_rollups table
        migrationBuilder.CreateTable(
            name: "saas_usage_rollups",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                Date = table.Column<DateOnly>(type: "date", nullable: false),
                SearchCount = table.Column<long>(type: "bigint", nullable: false),
                ChatCount = table.Column<long>(type: "bigint", nullable: false),
                AutocompleteCount = table.Column<long>(type: "bigint", nullable: false),
                FailedCount = table.Column<long>(type: "bigint", nullable: false),
                AvgResponseTimeMs = table.Column<int>(type: "integer", nullable: false),
                P95ResponseTimeMs = table.Column<int>(type: "integer", nullable: false),
                P99ResponseTimeMs = table.Column<int>(type: "integer", nullable: false),
                AggregatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_saas_usage_rollups", x => x.Id);
                table.ForeignKey(
                    name: "FK_saas_usage_rollups_api_keys_ApiKeyId",
                    column: x => x.ApiKeyId,
                    principalTable: "api_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_saas_usage_rollups_ApiKeyId_Date",
            table: "saas_usage_rollups",
            columns: new[] { "ApiKeyId", "Date" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "saas_usage_rollups");
        migrationBuilder.DropTable(name: "saas_query_logs");
        migrationBuilder.DropTable(name: "api_key_collection_links");

        migrationBuilder.DropColumn(name: "SigningSecret", table: "api_keys");
        migrationBuilder.DropColumn(name: "HasCompletedOnboarding", table: "AspNetUsers");
    }
}
