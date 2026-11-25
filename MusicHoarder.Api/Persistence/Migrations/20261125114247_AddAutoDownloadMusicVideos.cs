using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261125114247_AddAutoDownloadMusicVideos : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Pick",
            table: "SongMusicVideos",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<bool>(
            name: "AutoDownloadMusicVideos",
            table: "RuntimeSettings",
            type: "boolean",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Pick",
            table: "SongMusicVideos");

        migrationBuilder.DropColumn(
            name: "AutoDownloadMusicVideos",
            table: "RuntimeSettings");
    }
}
