using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLyricsTranslationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DetectedLyricsLanguage",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LyricsTranslatedAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LyricsTranslationError",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LyricsTranslationModel",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LyricsTranslationStatus",
                table: "Songs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RomanizedPlainLyrics",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RomanizedSyncedLyrics",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranslatedPlainLyrics",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranslatedSyncedLyrics",
                table: "Songs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetectedLyricsLanguage",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsTranslatedAtUtc",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsTranslationError",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsTranslationModel",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsTranslationStatus",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "RomanizedPlainLyrics",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "RomanizedSyncedLyrics",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "TranslatedPlainLyrics",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "TranslatedSyncedLyrics",
                table: "Songs");
        }
    }
}
