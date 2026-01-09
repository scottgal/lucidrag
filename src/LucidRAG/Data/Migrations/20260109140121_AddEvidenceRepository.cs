using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEvidenceRepository : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProcessingState",
                table: "retrieval_entities",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceModalities",
                table: "retrieval_entities",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "evidence_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StorageBackend = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProducerSource = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ProducerVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_artifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_evidence_artifacts_retrieval_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "retrieval_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scanned_page_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    GroupName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    GroupingStrategy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FilenamePattern = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DirectoryPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scanned_page_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scanned_page_groups_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scanned_page_memberships",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    OriginalFilename = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scanned_page_memberships", x => new { x.GroupId, x.EntityId });
                    table.ForeignKey(
                        name: "FK_scanned_page_memberships_retrieval_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "retrieval_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scanned_page_memberships_scanned_page_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "scanned_page_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_ArtifactType",
                table: "evidence_artifacts",
                column: "ArtifactType");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_ContentHash",
                table: "evidence_artifacts",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_EntityId",
                table: "evidence_artifacts",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_EntityId_ArtifactType",
                table: "evidence_artifacts",
                columns: new[] { "EntityId", "ArtifactType" });

            migrationBuilder.CreateIndex(
                name: "IX_scanned_page_groups_CollectionId",
                table: "scanned_page_groups",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_scanned_page_groups_GroupingStrategy",
                table: "scanned_page_groups",
                column: "GroupingStrategy");

            migrationBuilder.CreateIndex(
                name: "IX_scanned_page_memberships_EntityId",
                table: "scanned_page_memberships",
                column: "EntityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evidence_artifacts");

            migrationBuilder.DropTable(
                name: "scanned_page_memberships");

            migrationBuilder.DropTable(
                name: "scanned_page_groups");

            migrationBuilder.DropColumn(
                name: "ProcessingState",
                table: "retrieval_entities");

            migrationBuilder.DropColumn(
                name: "SourceModalities",
                table: "retrieval_entities");
        }
    }
}
