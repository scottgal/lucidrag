using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Migrations;

/// <summary>
///     Adds widget configuration, custom domains, and API key slugs:
///     - widget_configs: Per-API-key widget appearance/behaviour settings
///     - custom_domains: Custom domain mapping for white-label hosted pages
///     - api_keys.Slug: URL-friendly slug for hosted search pages
/// </summary>
public partial class AddWidgetConfigAndCustomDomains : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Add Slug to api_keys
        migrationBuilder.AddColumn<string>(
            name: "Slug",
            table: "api_keys",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_api_keys_Slug",
            table: "api_keys",
            column: "Slug",
            unique: true,
            filter: "\"Slug\" IS NOT NULL");

        // Create widget_configs table
        migrationBuilder.CreateTable(
            name: "widget_configs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                Theme = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                AccentColor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                BorderRadius = table.Column<int>(type: "integer", nullable: false, defaultValue: 8),
                FontFamily = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                CustomCss = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                LogoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                ShowBranding = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                Position = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "inline"),
                Mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "both"),
                Placeholder = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                MaxResults = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                CorpusStyle = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "hidden"),
                PageTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                PageDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                FaviconUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                WelcomeMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_widget_configs", x => x.Id);
                table.ForeignKey(
                    name: "FK_widget_configs_api_keys_ApiKeyId",
                    column: x => x.ApiKeyId,
                    principalTable: "api_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_widget_configs_ApiKeyId",
            table: "widget_configs",
            column: "ApiKeyId",
            unique: true);

        // Create custom_domains table
        migrationBuilder.CreateTable(
            name: "custom_domains",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                IsVerified = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                VerificationToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_custom_domains", x => x.Id);
                table.ForeignKey(
                    name: "FK_custom_domains_api_keys_ApiKeyId",
                    column: x => x.ApiKeyId,
                    principalTable: "api_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_custom_domains_Domain",
            table: "custom_domains",
            column: "Domain",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_custom_domains_ApiKeyId",
            table: "custom_domains",
            column: "ApiKeyId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "custom_domains");
        migrationBuilder.DropTable(name: "widget_configs");

        migrationBuilder.DropIndex(name: "IX_api_keys_Slug", table: "api_keys");
        migrationBuilder.DropColumn(name: "Slug", table: "api_keys");
    }
}
