using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261125073700_AddLyricsAutoSync : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "TranscriptionWordsStampUtc",
            table: "Songs",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "LyricsAutoSyncEnabled",
            table: "RuntimeSettings",
            type: "boolean",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TranscriptionWordsStampUtc",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "LyricsAutoSyncEnabled",
            table: "RuntimeSettings");
    }
}
