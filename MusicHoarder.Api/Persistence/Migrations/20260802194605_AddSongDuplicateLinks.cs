using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSongDuplicateLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DuplicateKeeperPinnedAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SongDuplicateLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongIdLow = table.Column<int>(type: "integer", nullable: false),
                    SongIdHigh = table.Column<int>(type: "integer", nullable: false),
                    Reasons = table.Column<int>(type: "integer", nullable: false),
                    Similarity = table.Column<double>(type: "double precision", nullable: true),
                    Confidence = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DismissedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongDuplicateLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SongDuplicateLinks_Songs_SongIdHigh",
                        column: x => x.SongIdHigh,
                        principalTable: "Songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SongDuplicateLinks_Songs_SongIdLow",
                        column: x => x.SongIdLow,
                        principalTable: "Songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SongDuplicateLinks_OwnerUserId",
                table: "SongDuplicateLinks",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SongDuplicateLinks_SongIdHigh",
                table: "SongDuplicateLinks",
                column: "SongIdHigh");

            migrationBuilder.CreateIndex(
                name: "IX_SongDuplicateLinks_SongIdLow_SongIdHigh",
                table: "SongDuplicateLinks",
                columns: new[] { "SongIdLow", "SongIdHigh" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SongDuplicateLinks");

            migrationBuilder.DropColumn(
                name: "DuplicateKeeperPinnedAtUtc",
                table: "Songs");
        }
    }
}
