using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLyricsTranscriptionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TranscribedAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranscribedPlainLyrics",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranscribedSyncedLyrics",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranscriptionError",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranscriptionModel",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TranscriptionStatus",
                table: "Songs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TranscribedAtUtc",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "TranscribedPlainLyrics",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "TranscribedSyncedLyrics",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "TranscriptionError",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "TranscriptionModel",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "TranscriptionStatus",
                table: "Songs");
        }
    }
}
