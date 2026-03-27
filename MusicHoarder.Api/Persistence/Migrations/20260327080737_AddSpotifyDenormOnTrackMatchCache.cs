using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpotifyDenormOnTrackMatchCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SpotifyAddedAtUtc",
                table: "SpotifyTrackLibraryMatches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpotifyAlbum",
                table: "SpotifyTrackLibraryMatches",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpotifyArtist",
                table: "SpotifyTrackLibraryMatches",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpotifyDurationMs",
                table: "SpotifyTrackLibraryMatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpotifyTitle",
                table: "SpotifyTrackLibraryMatches",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpotifyAddedAtUtc",
                table: "SpotifyTrackLibraryMatches");

            migrationBuilder.DropColumn(
                name: "SpotifyAlbum",
                table: "SpotifyTrackLibraryMatches");

            migrationBuilder.DropColumn(
                name: "SpotifyArtist",
                table: "SpotifyTrackLibraryMatches");

            migrationBuilder.DropColumn(
                name: "SpotifyDurationMs",
                table: "SpotifyTrackLibraryMatches");

            migrationBuilder.DropColumn(
                name: "SpotifyTitle",
                table: "SpotifyTrackLibraryMatches");
        }
    }
}
