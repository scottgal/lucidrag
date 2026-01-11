using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Migrations
{
    /// <inheritdoc />
    public partial class AddCommunityCollectionLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CollectionId",
                table: "communities",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_communities_CollectionId",
                table: "communities",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_communities_CollectionId_Name",
                table: "communities",
                columns: new[] { "CollectionId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_communities_collections_CollectionId",
                table: "communities",
                column: "CollectionId",
                principalTable: "collections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_communities_collections_CollectionId",
                table: "communities");

            migrationBuilder.DropIndex(
                name: "IX_communities_CollectionId",
                table: "communities");

            migrationBuilder.DropIndex(
                name: "IX_communities_CollectionId_Name",
                table: "communities");

            migrationBuilder.DropColumn(
                name: "CollectionId",
                table: "communities");
        }
    }
}
