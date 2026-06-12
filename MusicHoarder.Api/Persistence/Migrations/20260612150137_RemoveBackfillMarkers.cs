using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveBackfillMarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverArtBackfillCompletedAtUtc",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "LibraryWriteBaselineCompletedAtUtc",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "LyricsEmbedBackfillCompletedAtUtc",
                table: "RuntimeSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CoverArtBackfillCompletedAtUtc",
                table: "RuntimeSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LibraryWriteBaselineCompletedAtUtc",
                table: "RuntimeSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LyricsEmbedBackfillCompletedAtUtc",
                table: "RuntimeSettings",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
