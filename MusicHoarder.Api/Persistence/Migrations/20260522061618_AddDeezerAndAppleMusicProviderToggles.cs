using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeezerAndAppleMusicProviderToggles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableAppleMusicProvider",
                table: "RuntimeSettings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableDeezerProvider",
                table: "RuntimeSettings",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableAppleMusicProvider",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "EnableDeezerProvider",
                table: "RuntimeSettings");
        }
    }
}
