using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderAttemptRateLimitedSince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RateLimitedSinceUtc",
                table: "SongProviderAttempts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RateLimitedSinceUtc",
                table: "SongProviderAttempts");
        }
    }
}
