using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCrossModalRetrievalEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "retrieval_entities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TextContent = table.Column<string>(type: "text", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    QualityScore = table.Column<double>(type: "double precision", nullable: false),
                    ContentConfidence = table.Column<double>(type: "double precision", nullable: false),
                    NeedsReview = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Tags = table.Column<string>(type: "jsonb", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CustomMetadata = table.Column<string>(type: "jsonb", nullable: true),
                    Signals = table.Column<string>(type: "jsonb", nullable: true),
                    ExtractedEntities = table.Column<string>(type: "jsonb", nullable: true),
                    Relationships = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retrieval_entities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_retrieval_entities_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "entity_embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    Vector = table.Column<string>(type: "jsonb", nullable: true),
                    VectorBinary = table.Column<byte[]>(type: "bytea", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entity_embeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_entity_embeddings_retrieval_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "retrieval_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_entity_embeddings_EntityId",
                table: "entity_embeddings",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_entity_embeddings_EntityId_Name",
                table: "entity_embeddings",
                columns: new[] { "EntityId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_CollectionId",
                table: "retrieval_entities",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_CollectionId_ContentType",
                table: "retrieval_entities",
                columns: new[] { "CollectionId", "ContentType" });

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_ContentHash",
                table: "retrieval_entities",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_ContentType",
                table: "retrieval_entities",
                column: "ContentType");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_CreatedAt",
                table: "retrieval_entities",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_NeedsReview",
                table: "retrieval_entities",
                column: "NeedsReview");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entity_embeddings");

            migrationBuilder.DropTable(
                name: "retrieval_entities");
        }
    }
}
