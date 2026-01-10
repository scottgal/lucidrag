using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGinIndexOnEvidenceMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_Metadata",
                table: "evidence_artifacts",
                column: "Metadata")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_evidence_artifacts_Metadata",
                table: "evidence_artifacts");
        }
    }
}
