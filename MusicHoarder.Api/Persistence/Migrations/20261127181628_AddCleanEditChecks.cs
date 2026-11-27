using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261127181628_AddCleanEditChecks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CleanEditChecks",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                SongId = table.Column<int>(type: "integer", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                Verdict = table.Column<int>(type: "integer", nullable: false),
                Margin = table.Column<double>(type: "double precision", nullable: true),
                EditedSeconds = table.Column<double>(type: "double precision", nullable: true),
                EvidenceJson = table.Column<string>(type: "text", nullable: true),
                FileStamp = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                CheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CleanEditChecks", x => x.Id);
                table.ForeignKey(
                    name: "FK_CleanEditChecks_Songs_SongId",
                    column: x => x.SongId,
                    principalTable: "Songs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CleanEditChecks_OwnerUserId_Verdict",
            table: "CleanEditChecks",
            columns: new[] { "OwnerUserId", "Verdict" });

        migrationBuilder.CreateIndex(
            name: "IX_CleanEditChecks_SongId",
            table: "CleanEditChecks",
            column: "SongId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CleanEditChecks");
    }
}
