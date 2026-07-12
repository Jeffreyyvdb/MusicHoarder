using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNavidromeLikeSyncFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LikeLastSyncedValue",
                table: "Songs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NavidromeSongId",
                table: "Songs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LikeLastSyncedValue",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "NavidromeSongId",
                table: "Songs");
        }
    }
}
