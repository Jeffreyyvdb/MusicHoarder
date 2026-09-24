using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicVideoMatte : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "LetterboxFraction",
                table: "SongMusicVideos",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PillarboxFraction",
                table: "SongMusicVideos",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LetterboxFraction",
                table: "SongMusicVideos");

            migrationBuilder.DropColumn(
                name: "PillarboxFraction",
                table: "SongMusicVideos");
        }
    }
}
