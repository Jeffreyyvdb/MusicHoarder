using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261125113639_AddVideoSearchSchedule : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "EmptySearches",
            table: "SongVideoCandidateSets",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "NextSearchAtUtc",
            table: "SongVideoCandidateSets",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "EmptySearches",
            table: "SongVideoCandidateSets");

        migrationBuilder.DropColumn(
            name: "NextSearchAtUtc",
            table: "SongVideoCandidateSets");
    }
}
