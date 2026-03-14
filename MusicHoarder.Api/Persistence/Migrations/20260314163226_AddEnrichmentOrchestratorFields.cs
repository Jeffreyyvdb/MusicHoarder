using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentOrchestratorFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EnrichedAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnrichmentError",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EnrichmentLastAttemptedAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EnrichmentStatus",
                table: "Songs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "MatchConfidence",
                table: "Songs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchedBy",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalAlbum",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalArtist",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalIsrc",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OriginalMetadataCaptured",
                table: "Songs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "OriginalMetadataCapturedAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalMusicBrainzId",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalSpotifyId",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalTitle",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalTrackNumber",
                table: "Songs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalYear",
                table: "Songs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Songs_DeletedAtUtc_EnrichmentStatus_Id",
                table: "Songs",
                columns: new[] { "DeletedAtUtc", "EnrichmentStatus", "Id" });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_DeletedAtUtc_EnrichmentStatus_Id",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "EnrichedAtUtc",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "EnrichmentError",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "EnrichmentLastAttemptedAtUtc",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "EnrichmentStatus",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "MatchConfidence",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "MatchedBy",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalAlbum",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalArtist",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalIsrc",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalMetadataCaptured",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalMetadataCapturedAtUtc",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalMusicBrainzId",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalSpotifyId",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalTitle",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalTrackNumber",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalYear",
                table: "Songs");
        }
    }
}
