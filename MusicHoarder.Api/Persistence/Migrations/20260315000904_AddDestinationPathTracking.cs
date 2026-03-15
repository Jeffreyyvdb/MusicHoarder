using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDestinationPathTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestinationPath",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousDestinationPath",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Songs_DestinationPath",
                table: "Songs",
                column: "DestinationPath");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_DestinationPath",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "DestinationPath",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "PreviousDestinationPath",
                table: "Songs");
        }
    }
}
