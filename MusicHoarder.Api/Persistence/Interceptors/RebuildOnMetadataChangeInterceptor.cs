using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Persistence.Interceptors;

/// <summary>
/// Keeps already-built destination files in sync with the database: when a tag-relevant column on a
/// <see cref="SongMetadata"/> that is currently <see cref="LibraryBuildStatus.Done"/> is modified,
/// reset its library build so the <c>LibraryBuilderBackgroundService</c> sweep re-copies and re-tags
/// the file. Without this, metadata fetched <em>after</em> a song is built — most notably lyrics,
/// which land asynchronously after enrichment — never reaches the file on disk, so external players
/// (Navidrome) read stale tags even though the app shows the fresh value from the DB.
///
/// Gated on <see cref="MusicEnricherOptions.AutoStartPipeline"/>: when auto-build is off the builder
/// only runs on explicit triggers, so nulling <c>DestinationPath</c> here would strand the file out
/// of the Destination view indefinitely.
/// </summary>
public sealed class RebuildOnMetadataChangeInterceptor(IOptionsMonitor<MusicEnricherOptions> options)
    : SaveChangesInterceptor
{
    /// <summary>
    /// Columns whose value is embedded into the destination file by
    /// <c>TagLibLibraryTagWriter.WriteTagsAsync</c> / <c>WriteExtendedTags</c>. Keep this in sync
    /// with that writer — it is the source of truth for what ends up in the file's tags.
    /// <c>IsInstrumental</c> is intentionally absent: it isn't written to a tag directly, and turning
    /// a track instrumental also nulls <c>SyncedLyrics</c>/<c>PlainLyrics</c>, which is detected.
    /// </summary>
    private static readonly string[] TagRelevantProperties =
    [
        nameof(SongMetadata.Title),
        nameof(SongMetadata.Album),
        nameof(SongMetadata.Artist),
        nameof(SongMetadata.AlbumArtist),
        nameof(SongMetadata.Year),
        nameof(SongMetadata.TrackNumber),
        nameof(SongMetadata.TotalTracks),
        nameof(SongMetadata.DiscNumber),
        nameof(SongMetadata.TotalDiscs),
        nameof(SongMetadata.Isrc),
        nameof(SongMetadata.MusicBrainzId),
        nameof(SongMetadata.MusicBrainzReleaseId),
        nameof(SongMetadata.MusicBrainzReleaseGroupId),
        nameof(SongMetadata.AlbumArtistMusicBrainzId),
        nameof(SongMetadata.ArtistMusicBrainzIds),
        nameof(SongMetadata.Artists),
        nameof(SongMetadata.ReleaseTypes),
        nameof(SongMetadata.IsCompilation),
        nameof(SongMetadata.SyncedLyrics),
        nameof(SongMetadata.PlainLyrics),
    ];

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ResetBuildsForChangedMetadata(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ResetBuildsForChangedMetadata(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ResetBuildsForChangedMetadata(DbContext? context)
    {
        if (context is null || !options.CurrentValue.AutoStartPipeline)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<SongMetadata>())
        {
            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            // Only nudge songs that are currently built. Any explicit ResetLibraryBuild() caller has
            // already moved the song off Done within this same unit of work, so this guard both skips
            // those (avoiding a double reset that would clobber PreviousDestinationPath) and prevents
            // a feedback loop — MarkBuildDone sets Done but touches no tag column.
            if (entry.Entity.LibraryBuildStatus != LibraryBuildStatus.Done)
            {
                continue;
            }

            foreach (var property in TagRelevantProperties)
            {
                if (entry.Property(property).IsModified)
                {
                    entry.Entity.ResetLibraryBuild();
                    break;
                }
            }
        }
    }
}
