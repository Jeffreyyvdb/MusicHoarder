using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryWriteEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastWrittenAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastWrittenTagsJson",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LibraryWriteBaselineCompletedAtUtc",
                table: "RuntimeSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LibraryWriteEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    SongId = table.Column<int>(type: "integer", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    WrittenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DestinationPath = table.Column<string>(type: "text", nullable: true),
                    AlbumFolder = table.Column<string>(type: "text", nullable: true),
                    AlbumArtist = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Album = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FieldName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OldValue = table.Column<string>(type: "text", nullable: true),
                    NewValue = table.Column<string>(type: "text", nullable: true),
                    IsAlbumIdentityField = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryWriteEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryWriteEvents_Songs_SongId",
                        column: x => x.SongId,
                        principalTable: "Songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryWriteEvents_OwnerUserId_AlbumArtist_Album_WrittenAtU~",
                table: "LibraryWriteEvents",
                columns: new[] { "OwnerUserId", "AlbumArtist", "Album", "WrittenAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryWriteEvents_OwnerUserId_WrittenAtUtc",
                table: "LibraryWriteEvents",
                columns: new[] { "OwnerUserId", "WrittenAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryWriteEvents_SongId",
                table: "LibraryWriteEvents",
                column: "SongId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryWriteEvents");

            migrationBuilder.DropColumn(
                name: "LastWrittenAtUtc",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LastWrittenTagsJson",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "LibraryWriteBaselineCompletedAtUtc",
                table: "RuntimeSettings");
        }
    }
}
