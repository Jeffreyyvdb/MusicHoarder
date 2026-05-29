using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropDeadRuntimeSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcoustIdScoreThreshold",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "EnrichmentWorkerConcurrency",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "LibraryBuilderWorkerConcurrency",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "SpotifyApiMatchedThreshold",
                table: "RuntimeSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AcoustIdScoreThreshold",
                table: "RuntimeSettings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EnrichmentWorkerConcurrency",
                table: "RuntimeSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LibraryBuilderWorkerConcurrency",
                table: "RuntimeSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SpotifyApiMatchedThreshold",
                table: "RuntimeSettings",
                type: "double precision",
                nullable: true);
        }
    }
}
