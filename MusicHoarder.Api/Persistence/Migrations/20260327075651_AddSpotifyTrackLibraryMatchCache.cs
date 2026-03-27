using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpotifyTrackLibraryMatchCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SpotifyLikedMatchInLibrary",
                table: "SpotifySettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpotifyLikedMatchNotInLibrary",
                table: "SpotifySettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpotifyLikedMatchPossible",
                table: "SpotifySettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SpotifyLikedMatchStatsUpdatedAtUtc",
                table: "SpotifySettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpotifyLikedMatchTotal",
                table: "SpotifySettings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SpotifyTrackLibraryMatches",
                columns: table => new
                {
                    SpotifyTrackId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MatchStatus = table.Column<int>(type: "integer", nullable: false),
                    MatchedSongId = table.Column<int>(type: "integer", nullable: true),
                    MatchConfidence = table.Column<double>(type: "double precision", nullable: true),
                    MatchedTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MatchedArtist = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MatchedEnrichmentStatus = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpotifyTrackLibraryMatches", x => x.SpotifyTrackId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpotifyTrackLibraryMatches_MatchStatus",
                table: "SpotifyTrackLibraryMatches",
                column: "MatchStatus");

            migrationBuilder.CreateIndex(
                name: "IX_SpotifyTrackLibraryMatches_UpdatedAtUtc",
                table: "SpotifyTrackLibraryMatches",
                column: "UpdatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpotifyTrackLibraryMatches");

            migrationBuilder.DropColumn(
                name: "SpotifyLikedMatchInLibrary",
                table: "SpotifySettings");

            migrationBuilder.DropColumn(
                name: "SpotifyLikedMatchNotInLibrary",
                table: "SpotifySettings");

            migrationBuilder.DropColumn(
                name: "SpotifyLikedMatchPossible",
                table: "SpotifySettings");

            migrationBuilder.DropColumn(
                name: "SpotifyLikedMatchStatsUpdatedAtUtc",
                table: "SpotifySettings");

            migrationBuilder.DropColumn(
                name: "SpotifyLikedMatchTotal",
                table: "SpotifySettings");
        }
    }
}
