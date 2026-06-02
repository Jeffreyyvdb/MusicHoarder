using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoverArtBackfillMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CoverArtBackfillCompletedAtUtc",
                table: "RuntimeSettings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverArtBackfillCompletedAtUtc",
                table: "RuntimeSettings");
        }
    }
}
