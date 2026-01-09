using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestionEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ingestion_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Location = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    FilePattern = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Recursive = table.Column<bool>(type: "boolean", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Options = table.Column<string>(type: "jsonb", nullable: true),
                    Credentials = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TotalItemsIngested = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ingestion_sources_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ItemsDiscovered = table.Column<int>(type: "integer", nullable: false),
                    ItemsProcessed = table.Column<int>(type: "integer", nullable: false),
                    ItemsFailed = table.Column<int>(type: "integer", nullable: false),
                    ItemsSkipped = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    Errors = table.Column<string>(type: "jsonb", nullable: true),
                    IncrementalSync = table.Column<bool>(type: "boolean", nullable: false),
                    MaxItems = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ingestion_jobs_ingestion_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "ingestion_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_jobs_CreatedAt",
                table: "ingestion_jobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_jobs_SourceId",
                table: "ingestion_jobs",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_jobs_Status",
                table: "ingestion_jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_sources_CollectionId",
                table: "ingestion_sources",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_sources_IsEnabled",
                table: "ingestion_sources",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_sources_Name",
                table: "ingestion_sources",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_sources_SourceType",
                table: "ingestion_sources",
                column: "SourceType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ingestion_jobs");

            migrationBuilder.DropTable(
                name: "ingestion_sources");
        }
    }
}
