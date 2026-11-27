using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261127195719_AddAudioAssets : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AudioAssetId",
            table: "Songs",
            type: "integer",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "AudioAssets",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                StoragePath = table.Column<string>(type: "text", nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                Container = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Codec = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Bitrate = table.Column<int>(type: "integer", nullable: true),
                SampleRate = table.Column<int>(type: "integer", nullable: true),
                DurationMs = table.Column<int>(type: "integer", nullable: true),
                Fingerprint = table.Column<string>(type: "text", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                LastReferencedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                PurgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AudioAssets", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Songs_AudioAssetId",
            table: "Songs",
            column: "AudioAssetId");

        migrationBuilder.CreateIndex(
            name: "IX_AudioAssets_Sha256",
            table: "AudioAssets",
            column: "Sha256",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AudioAssets_StoragePath",
            table: "AudioAssets",
            column: "StoragePath");

        migrationBuilder.AddForeignKey(
            name: "FK_Songs_AudioAssets_AudioAssetId",
            table: "Songs",
            column: "AudioAssetId",
            principalTable: "AudioAssets",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Songs_AudioAssets_AudioAssetId",
            table: "Songs");

        migrationBuilder.DropTable(
            name: "AudioAssets");

        migrationBuilder.DropIndex(
            name: "IX_Songs_AudioAssetId",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "AudioAssetId",
            table: "Songs");
    }
}
