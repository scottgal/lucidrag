using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommunities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VectorStoreDocId",
                table: "documents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "communities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Features = table.Column<string>(type: "jsonb", nullable: true),
                    Algorithm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    ParentCommunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityCount = table.Column<int>(type: "integer", nullable: false),
                    Cohesion = table.Column<float>(type: "real", nullable: false),
                    Embedding = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_communities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_communities_communities_ParentCommunityId",
                        column: x => x.ParentCommunityId,
                        principalTable: "communities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "community_memberships",
                columns: table => new
                {
                    CommunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Centrality = table.Column<float>(type: "real", nullable: false),
                    IsRepresentative = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_community_memberships", x => new { x.CommunityId, x.EntityId });
                    table.ForeignKey(
                        name: "FK_community_memberships_communities_CommunityId",
                        column: x => x.CommunityId,
                        principalTable: "communities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_community_memberships_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_communities_EntityCount",
                table: "communities",
                column: "EntityCount");

            migrationBuilder.CreateIndex(
                name: "IX_communities_Level",
                table: "communities",
                column: "Level");

            migrationBuilder.CreateIndex(
                name: "IX_communities_ParentCommunityId",
                table: "communities",
                column: "ParentCommunityId");

            migrationBuilder.CreateIndex(
                name: "IX_community_memberships_Centrality",
                table: "community_memberships",
                column: "Centrality");

            migrationBuilder.CreateIndex(
                name: "IX_community_memberships_EntityId",
                table: "community_memberships",
                column: "EntityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "community_memberships");

            migrationBuilder.DropTable(
                name: "communities");

            migrationBuilder.DropColumn(
                name: "VectorStoreDocId",
                table: "documents");
        }
    }
}
