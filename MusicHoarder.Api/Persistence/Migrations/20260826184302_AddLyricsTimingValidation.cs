using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLyricsTimingValidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "LrclibDurationSeconds",
                table: "Songs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LyricsSyncCheckedAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LyricsSyncConfidence",
                table: "Songs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LyricsSyncIssue",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LyricsSyncOffsetMs",
                table: "Songs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LyricsSyncProbeAttempts",
                table: "Songs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LyricsSyncStatus",
                table: "Songs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "TranscriptionAlignedToReference",
                table: "Songs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LrclibDurationSeconds",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsSyncCheckedAtUtc",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsSyncConfidence",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsSyncIssue",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsSyncOffsetMs",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsSyncProbeAttempts",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsSyncStatus",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "TranscriptionAlignedToReference",
                table: "Songs");
        }
    }
}
