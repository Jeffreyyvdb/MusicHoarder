using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLyricsRecheckSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LyricsFetchAttempts",
                table: "Songs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LyricsLastAttemptedAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LyricsNextRecheckAfterUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Songs_DeletedAtUtc_LyricsStatus_LyricsNextRecheckAfterUtc",
                table: "Songs",
                columns: new[] { "DeletedAtUtc", "LyricsStatus", "LyricsNextRecheckAfterUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_DeletedAtUtc_LyricsStatus_LyricsNextRecheckAfterUtc",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsFetchAttempts",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsLastAttemptedAtUtc",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsNextRecheckAfterUtc",
                table: "Songs");
        }
    }
}
