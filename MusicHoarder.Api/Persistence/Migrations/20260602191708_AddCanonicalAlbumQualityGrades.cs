using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCanonicalAlbumQualityGrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CanonicalAlbumQualityGrades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CanonicalAlbumId = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    Verdict = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    IssuesJson = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PromptVersion = table.Column<int>(type: "integer", nullable: false),
                    InputFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OwnedTrackCount = table.Column<int>(type: "integer", nullable: false),
                    CanonicalTrackCount = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    RawResponseJson = table.Column<string>(type: "text", nullable: true),
                    GradedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanonicalAlbumQualityGrades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CanonicalAlbumQualityGrades_CanonicalAlbums_CanonicalAlbumId",
                        column: x => x.CanonicalAlbumId,
                        principalTable: "CanonicalAlbums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalAlbumQualityGrades_CanonicalAlbumId_GradedAtUtc",
                table: "CanonicalAlbumQualityGrades",
                columns: new[] { "CanonicalAlbumId", "GradedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalAlbumQualityGrades_OwnerUserId_GradedAtUtc",
                table: "CanonicalAlbumQualityGrades",
                columns: new[] { "OwnerUserId", "GradedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalAlbumQualityGrades_Verdict",
                table: "CanonicalAlbumQualityGrades",
                column: "Verdict");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CanonicalAlbumQualityGrades");
        }
    }
}
