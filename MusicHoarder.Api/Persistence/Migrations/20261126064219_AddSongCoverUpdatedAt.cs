using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations;

/// <summary>
/// Adds <c>Songs.CoverUpdatedAtUtc</c>, stamped on every song of a destination album folder when a
/// cover is written there. It feeds the cover URL's <c>?v=</c> token, which a replaced
/// <c>cover.jpg</c> otherwise would not move. Existing rows stay null, which keeps their tokens.
/// </summary>
public partial class _20261126064219_AddSongCoverUpdatedAt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "CoverUpdatedAtUtc",
            table: "Songs",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CoverUpdatedAtUtc",
            table: "Songs");
    }
}
