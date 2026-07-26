using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSongAcquiredAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AcquiredAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill the existing rows with the best proxy the row still carries: the OLDEST of the
            // three stamps. IndexedAtUtc gets bumped by a re-index and LibraryBuiltAtUtc is cleared and
            // re-set by a rebuild, so either alone would date a long-owned track to its last bit of
            // pipeline churn; the source file's mtime is usually the closest thing to when it arrived.
            migrationBuilder.Sql("""
                UPDATE "Songs"
                SET "AcquiredAtUtc" = LEAST(
                    "LastModifiedUtc",
                    "IndexedAtUtc",
                    COALESCE("LibraryBuiltAtUtc", "IndexedAtUtc"))
                WHERE "AcquiredAtUtc" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcquiredAtUtc",
                table: "Songs");
        }
    }
}
