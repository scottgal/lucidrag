using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentHashIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SegmentHash",
                table: "evidence_artifacts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_SegmentHash",
                table: "evidence_artifacts",
                column: "SegmentHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_evidence_artifacts_SegmentHash",
                table: "evidence_artifacts");

            migrationBuilder.DropColumn(
                name: "SegmentHash",
                table: "evidence_artifacts");
        }
    }
}
