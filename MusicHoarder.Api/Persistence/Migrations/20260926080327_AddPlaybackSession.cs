using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaybackSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaybackSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<int>(type: "integer", nullable: false),
                    QueueJson = table.Column<string>(type: "text", nullable: false),
                    QueueIndex = table.Column<int>(type: "integer", nullable: false),
                    PositionMs = table.Column<long>(type: "bigint", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    IsPlaying = table.Column<bool>(type: "boolean", nullable: false),
                    PlaybackRate = table.Column<double>(type: "double precision", nullable: false),
                    RadioSeedId = table.Column<int>(type: "integer", nullable: true),
                    Shuffle = table.Column<bool>(type: "boolean", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Artist = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Album = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ActiveDeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ActiveDeviceName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackSessions_OwnerUserId",
                table: "PlaybackSessions",
                column: "OwnerUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaybackSessions");
        }
    }
}
