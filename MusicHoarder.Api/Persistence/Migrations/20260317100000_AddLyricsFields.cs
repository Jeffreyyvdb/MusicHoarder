using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLyricsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsInstrumental",
                table: "Songs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LyricsStatus",
                table: "Songs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PlainLyrics",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncedLyrics",
                table: "Songs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsInstrumental",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LyricsStatus",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "PlainLyrics",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "SyncedLyrics",
                table: "Songs");
        }
    }
}
