using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryBuildStatusFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LibraryBuildError",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LibraryBuildLastAttemptedAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LibraryBuildStatus",
                table: "Songs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LibraryBuiltAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Songs_DeletedAtUtc_EnrichmentStatus_LibraryBuildStatus_Id",
                table: "Songs",
                columns: new[] { "DeletedAtUtc", "EnrichmentStatus", "LibraryBuildStatus", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_DeletedAtUtc_EnrichmentStatus_LibraryBuildStatus_Id",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LibraryBuildError",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LibraryBuildLastAttemptedAtUtc",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LibraryBuildStatus",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LibraryBuiltAtUtc",
                table: "Songs");
        }
    }
}
