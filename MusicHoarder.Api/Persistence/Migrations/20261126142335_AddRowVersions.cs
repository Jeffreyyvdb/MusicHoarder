using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261126142335_AddRowVersions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateSequence(
            name: "RowVersions");

        migrationBuilder.AddColumn<long>(
            name: "ListVersion",
            table: "WishlistSources",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "ListVersion",
            table: "Songs",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "NameVersion",
            table: "Songs",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "ListVersion",
            table: "SongMusicVideos",
            type: "bigint",
            nullable: true);

        // Every row gets a version before the triggers exist, so none is left on the xmin fallback.
        migrationBuilder.Sql("""
            UPDATE "Songs" SET "ListVersion" = nextval('"RowVersions"'), "NameVersion" = nextval('"RowVersions"');
            UPDATE "SongMusicVideos" SET "ListVersion" = nextval('"RowVersions"');
            UPDATE "WishlistSources" SET "ListVersion" = nextval('"RowVersions"');
            """);

        // The functions as RowVersions generated them when this migration was written. They are
        // literals on purpose: a migration that called the generator would change under databases
        // that have already run it. To change an ignore list, add a migration with the new text.

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION "Songs_RowVersions"() RETURNS trigger LANGUAGE plpgsql
              SET search_path FROM CURRENT AS $rowversions$
            DECLARE
              was jsonb;
              becomes jsonb;
              "ListVersionIgnores" CONSTANT text[] := ARRAY['EnrichedAtUtc', 'EnrichmentLastAttemptedAtUtc', 'LastWrittenAtUtc', 'LastWrittenTagsJson', 'LibraryBuildAttempts', 'LibraryBuildLastAttemptedAtUtc', 'LyricsFetchAttempts', 'LyricsLastAttemptedAtUtc', 'LyricsNextRecheckAfterUtc', 'LyricsSyncCheckedAtUtc', 'LyricsSyncConfidence', 'LyricsSyncProbeAttempts', 'PreviousDestinationPath']::text[];
              "NameVersionIgnores" CONSTANT text[] := ARRAY['CoverUpdatedAtUtc', 'DestinationPath', 'EnrichedAtUtc', 'EnrichmentLastAttemptedAtUtc', 'HasCoverArt', 'HasNonBlankPlainLyrics', 'HasNonBlankSyncedLyrics', 'HasPlainLyrics', 'HasSyncedLyrics', 'HasTranscribedLyrics', 'LastPlayedAtUtc', 'LastWrittenAtUtc', 'LastWrittenTagsJson', 'LibraryBuildAttempts', 'LibraryBuildError', 'LibraryBuildLastAttemptedAtUtc', 'LibraryBuildStatus', 'LibraryBuiltAtUtc', 'LikeLastSyncedValue', 'LikedAtUtc', 'LyricsFetchAttempts', 'LyricsLastAttemptedAtUtc', 'LyricsNextRecheckAfterUtc', 'LyricsOrigin', 'LyricsStatus', 'LyricsSyncCheckedAtUtc', 'LyricsSyncConfidence', 'LyricsSyncIssue', 'LyricsSyncOffsetMs', 'LyricsSyncProbeAttempts', 'LyricsSyncStatus', 'LyricsTextChangedAtUtc', 'PlainLyrics', 'PlayCount', 'PreviousDestinationPath', 'SyncedLyrics', 'TranscribedAtUtc', 'TranscribedPlainLyrics', 'TranscribedSyncedLyrics', 'TranscriptionError', 'TranscriptionStatus', 'TranscriptionWordsStampUtc']::text[];
            BEGIN
              IF TG_OP = 'INSERT' THEN
                NEW."ListVersion" := nextval('"RowVersions"');
                NEW."NameVersion" := nextval('"RowVersions"');
                RETURN NEW;
              END IF;
              NEW."ListVersion" := OLD."ListVersion";
              NEW."NameVersion" := OLD."NameVersion";
              was := to_jsonb(OLD);
              becomes := to_jsonb(NEW);
              IF (becomes - "ListVersionIgnores") IS DISTINCT FROM (was - "ListVersionIgnores") THEN
                NEW."ListVersion" := nextval('"RowVersions"');
              END IF;
              IF (becomes - "NameVersionIgnores") IS DISTINCT FROM (was - "NameVersionIgnores") THEN
                NEW."NameVersion" := nextval('"RowVersions"');
              END IF;
              RETURN NEW;
            END
            $rowversions$;
            DROP TRIGGER IF EXISTS "Songs_RowVersions" ON "Songs";
            CREATE TRIGGER "Songs_RowVersions" BEFORE INSERT OR UPDATE ON "Songs"
              FOR EACH ROW EXECUTE FUNCTION "Songs_RowVersions"();
            """);

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION "SongMusicVideos_RowVersions"() RETURNS trigger LANGUAGE plpgsql
              SET search_path FROM CURRENT AS $rowversions$
            DECLARE
              was jsonb;
              becomes jsonb;
              "ListVersionIgnores" CONSTANT text[] := ARRAY['LastError', 'LetterboxFraction', 'PillarboxFraction', 'SyncConfidence', 'SyncOffsetMs', 'SyncSource']::text[];
            BEGIN
              IF TG_OP = 'INSERT' THEN
                NEW."ListVersion" := nextval('"RowVersions"');
                RETURN NEW;
              END IF;
              NEW."ListVersion" := OLD."ListVersion";
              was := to_jsonb(OLD);
              becomes := to_jsonb(NEW);
              IF (becomes - "ListVersionIgnores") IS DISTINCT FROM (was - "ListVersionIgnores") THEN
                NEW."ListVersion" := nextval('"RowVersions"');
              END IF;
              RETURN NEW;
            END
            $rowversions$;
            DROP TRIGGER IF EXISTS "SongMusicVideos_RowVersions" ON "SongMusicVideos";
            CREATE TRIGGER "SongMusicVideos_RowVersions" BEFORE INSERT OR UPDATE ON "SongMusicVideos"
              FOR EACH ROW EXECUTE FUNCTION "SongMusicVideos_RowVersions"();
            """);

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION "WishlistSources_RowVersions"() RETURNS trigger LANGUAGE plpgsql
              SET search_path FROM CURRENT AS $rowversions$
            DECLARE
              was jsonb;
              becomes jsonb;
              "ListVersionIgnores" CONSTANT text[] := ARRAY['LastSyncedAtUtc', 'RemoteChecksum', 'TracksRecordedAtUtc']::text[];
            BEGIN
              IF TG_OP = 'INSERT' THEN
                NEW."ListVersion" := nextval('"RowVersions"');
                RETURN NEW;
              END IF;
              NEW."ListVersion" := OLD."ListVersion";
              was := to_jsonb(OLD);
              becomes := to_jsonb(NEW);
              IF (becomes - "ListVersionIgnores") IS DISTINCT FROM (was - "ListVersionIgnores") THEN
                NEW."ListVersion" := nextval('"RowVersions"');
              END IF;
              RETURN NEW;
            END
            $rowversions$;
            DROP TRIGGER IF EXISTS "WishlistSources_RowVersions" ON "WishlistSources";
            CREATE TRIGGER "WishlistSources_RowVersions" BEFORE INSERT OR UPDATE ON "WishlistSources"
              FOR EACH ROW EXECUTE FUNCTION "WishlistSources_RowVersions"();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS "Songs_RowVersions" ON "Songs";
            DROP TRIGGER IF EXISTS "SongMusicVideos_RowVersions" ON "SongMusicVideos";
            DROP TRIGGER IF EXISTS "WishlistSources_RowVersions" ON "WishlistSources";
            DROP FUNCTION IF EXISTS "Songs_RowVersions"();
            DROP FUNCTION IF EXISTS "SongMusicVideos_RowVersions"();
            DROP FUNCTION IF EXISTS "WishlistSources_RowVersions"();
            """);

        migrationBuilder.DropColumn(
            name: "ListVersion",
            table: "WishlistSources");

        migrationBuilder.DropColumn(
            name: "ListVersion",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "NameVersion",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "ListVersion",
            table: "SongMusicVideos");

        migrationBuilder.DropSequence(
            name: "RowVersions");
    }
}
