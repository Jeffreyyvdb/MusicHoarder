using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicateDetectionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Bitrate",
                table: "Songs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DuplicateOfId",
                table: "Songs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDuplicate",
                table: "Songs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Songs_DeletedAtUtc_IsDuplicate",
                table: "Songs",
                columns: new[] { "DeletedAtUtc", "IsDuplicate" });

            migrationBuilder.CreateIndex(
                name: "IX_Songs_DuplicateOfId",
                table: "Songs",
                column: "DuplicateOfId");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_Fingerprint",
                table: "Songs",
                column: "Fingerprint");

            migrationBuilder.AddForeignKey(
                name: "FK_Songs_Songs_DuplicateOfId",
                table: "Songs",
                column: "DuplicateOfId",
                principalTable: "Songs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Songs_Songs_DuplicateOfId",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_DeletedAtUtc_IsDuplicate",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_DuplicateOfId",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_Fingerprint",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "Bitrate",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "DuplicateOfId",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "IsDuplicate",
                table: "Songs");
        }
    }
}
