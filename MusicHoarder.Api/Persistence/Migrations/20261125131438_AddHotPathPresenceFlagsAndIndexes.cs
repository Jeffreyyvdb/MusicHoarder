using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261125131438_AddHotPathPresenceFlagsAndIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "HasFingerprint",
            table: "Songs",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "HasNonBlankPlainLyrics",
            table: "Songs",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "HasNonBlankSyncedLyrics",
            table: "Songs",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "HasPlainLyrics",
            table: "Songs",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "HasSyncedLyrics",
            table: "Songs",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "HasTranscribedLyrics",
            table: "Songs",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        // One-time backfill of stored presence flags for existing rows. Uses the exact set of 25
        // Unicode code points matched by .NET char.IsWhiteSpace (collation-independent, identical
        // under C and UTF-8 locales) so legacy rows agree with the entity setters.
        migrationBuilder.Sql("""
            UPDATE "Songs"
            SET "HasFingerprint"          = ("Fingerprint" IS NOT NULL AND "Fingerprint" <> ''),
                "HasPlainLyrics"          = ("PlainLyrics" IS NOT NULL AND "PlainLyrics" <> ''),
                "HasSyncedLyrics"         = ("SyncedLyrics" IS NOT NULL AND "SyncedLyrics" <> ''),
                "HasNonBlankPlainLyrics"  = ("PlainLyrics" IS NOT NULL AND "PlainLyrics" !~ E'^[\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]*$'),
                "HasNonBlankSyncedLyrics" = ("SyncedLyrics" IS NOT NULL AND "SyncedLyrics" !~ E'^[\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]*$'),
                "HasTranscribedLyrics"    = (("TranscribedSyncedLyrics" IS NOT NULL AND "TranscribedSyncedLyrics" <> '')
                                             OR ("TranscribedPlainLyrics" IS NOT NULL AND "TranscribedPlainLyrics" <> ''));
            """);

        migrationBuilder.CreateIndex(
            name: "IX_WishlistItems_OwnerUserId_DownloadedSongId",
            table: "WishlistItems",
            columns: new[] { "OwnerUserId", "DownloadedSongId" },
            filter: "\"DownloadedSongId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_SpotifyTrackLibraryMatches_OwnerUserId_Source",
            table: "SpotifyTrackLibraryMatches",
            columns: new[] { "OwnerUserId", "Source" },
            filter: "\"SpotifyAddedAtUtc\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_Songs_OwnerUserId_DeletedAtUtc_AcquiredAtUtc",
            table: "Songs",
            columns: new[] { "OwnerUserId", "DeletedAtUtc", "AcquiredAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Songs_OwnerUserId_DeletedAtUtc_EnrichmentStatus",
            table: "Songs",
            columns: new[] { "OwnerUserId", "DeletedAtUtc", "EnrichmentStatus" });

        migrationBuilder.CreateIndex(
            name: "IX_Songs_OwnerUserId_DeletedAtUtc_LibraryBuildStatus",
            table: "Songs",
            columns: new[] { "OwnerUserId", "DeletedAtUtc", "LibraryBuildStatus" });

        migrationBuilder.CreateIndex(
            name: "IX_SongQualityGrades_OwnerUserId_SongId_GradedAtUtc",
            table: "SongQualityGrades",
            columns: new[] { "OwnerUserId", "SongId", "GradedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_SongMetadataChanges_Source_FieldName_SongId",
            table: "SongMetadataChanges",
            columns: new[] { "Source", "FieldName", "SongId" });

        migrationBuilder.CreateIndex(
            name: "IX_SongDuplicateLinks_OwnerUserId_Status",
            table: "SongDuplicateLinks",
            columns: new[] { "OwnerUserId", "Status" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_WishlistItems_OwnerUserId_DownloadedSongId",
            table: "WishlistItems");

        migrationBuilder.DropIndex(
            name: "IX_SpotifyTrackLibraryMatches_OwnerUserId_Source",
            table: "SpotifyTrackLibraryMatches");

        migrationBuilder.DropIndex(
            name: "IX_Songs_OwnerUserId_DeletedAtUtc_AcquiredAtUtc",
            table: "Songs");

        migrationBuilder.DropIndex(
            name: "IX_Songs_OwnerUserId_DeletedAtUtc_EnrichmentStatus",
            table: "Songs");

        migrationBuilder.DropIndex(
            name: "IX_Songs_OwnerUserId_DeletedAtUtc_LibraryBuildStatus",
            table: "Songs");

        migrationBuilder.DropIndex(
            name: "IX_SongQualityGrades_OwnerUserId_SongId_GradedAtUtc",
            table: "SongQualityGrades");

        migrationBuilder.DropIndex(
            name: "IX_SongMetadataChanges_Source_FieldName_SongId",
            table: "SongMetadataChanges");

        migrationBuilder.DropIndex(
            name: "IX_SongDuplicateLinks_OwnerUserId_Status",
            table: "SongDuplicateLinks");

        migrationBuilder.DropColumn(
            name: "HasFingerprint",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "HasNonBlankPlainLyrics",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "HasNonBlankSyncedLyrics",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "HasPlainLyrics",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "HasSyncedLyrics",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "HasTranscribedLyrics",
            table: "Songs");
    }
}
