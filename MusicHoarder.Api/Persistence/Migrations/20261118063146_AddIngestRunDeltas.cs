using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestRunDeltas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NewFailed",
                table: "IngestRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NewReview",
                table: "IngestRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NewTracks",
                table: "IngestRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NewlyBuilt",
                table: "IngestRuns",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NewFailed",
                table: "IngestRuns");

            migrationBuilder.DropColumn(
                name: "NewReview",
                table: "IngestRuns");

            migrationBuilder.DropColumn(
                name: "NewTracks",
                table: "IngestRuns");

            migrationBuilder.DropColumn(
                name: "NewlyBuilt",
                table: "IngestRuns");
        }
    }
}
