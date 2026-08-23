using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendSongState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FriendSongStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<int>(type: "integer", nullable: false),
                    LikedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PlayCount = table.Column<int>(type: "integer", nullable: false),
                    LastPlayedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FriendSongStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FriendSongStates_Songs_SongId",
                        column: x => x.SongId,
                        principalTable: "Songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FriendSongStates_SongId",
                table: "FriendSongStates",
                column: "SongId");

            migrationBuilder.CreateIndex(
                name: "IX_FriendSongStates_UserId_LikedAtUtc",
                table: "FriendSongStates",
                columns: new[] { "UserId", "LikedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FriendSongStates_UserId_SongId",
                table: "FriendSongStates",
                columns: new[] { "UserId", "SongId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FriendSongStates");
        }
    }
}
