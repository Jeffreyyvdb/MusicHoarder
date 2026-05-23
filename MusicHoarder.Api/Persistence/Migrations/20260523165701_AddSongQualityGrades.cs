using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSongQualityGrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SongQualityGrades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SongId = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    Verdict = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    IssuesJson = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PromptVersion = table.Column<int>(type: "integer", nullable: false),
                    InputFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EnrichmentStatusAtGrade = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DestinationPathPreview = table.Column<string>(type: "text", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    RawResponseJson = table.Column<string>(type: "text", nullable: true),
                    GradedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongQualityGrades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SongQualityGrades_Songs_SongId",
                        column: x => x.SongId,
                        principalTable: "Songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SongQualityGrades_OwnerUserId_GradedAtUtc",
                table: "SongQualityGrades",
                columns: new[] { "OwnerUserId", "GradedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SongQualityGrades_SongId_GradedAtUtc",
                table: "SongQualityGrades",
                columns: new[] { "SongId", "GradedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SongQualityGrades_Verdict",
                table: "SongQualityGrades",
                column: "Verdict");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SongQualityGrades");
        }
    }
}
