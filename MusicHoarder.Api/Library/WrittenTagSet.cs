using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// The user-meaningful subset of tags MusicHoarder writes to a destination file, projected so a build
/// can be diffed against the previous write to produce <see cref="LibraryWriteEvent"/>s. It mirrors
/// <see cref="TagLibLibraryTagWriter.WriteTagsAsync"/>: album-IDENTITY fields come from the reconciled
/// <see cref="AlbumIdentity"/> (shared across the folder — this is where consolidation shows up),
/// track-level fields come from the <see cref="SongMetadata"/>, and the Various-Artists substitution is
/// applied for compilations. Lyrics are reduced to a presence flag so the feed reads "Lyrics added"
/// rather than dumping the whole LRC.
///
/// Each field value is normalized to a string (null/empty are equivalent for diffing). The static
/// <see cref="Fields"/> list is the single ordered source of truth for which fields the feed surfaces
/// and which are album-identity — a test asserts it matches what the writer touches, to catch drift.
/// </summary>
public sealed record WrittenTagSet(
    string? Title,
    string? Artist,
    string? Artists,
    string? ArtistMusicBrainzIds,
    string? AlbumArtist,
    string? Album,
    string? Year,
    string? TrackNumber,
    string? DiscNumber,
    string? TotalDiscs,
    string? Isrc,
    string? MusicBrainzRecordingId,
    string? MusicBrainzReleaseId,
    string? MusicBrainzReleaseGroupId,
    string? AlbumArtistMusicBrainzId,
    string? ReleaseTypes,
    string? IsCompilation,
    string? Lyrics)
{
    private const string VariousArtists = "Various Artists";

    /// <summary>Ordered field accessors with their album-identity classification — the diff iterates this.</summary>
    public static readonly IReadOnlyList<(string Name, bool IsAlbumIdentity, Func<WrittenTagSet, string?> Get)> Fields =
    [
        (nameof(Title), false, s => s.Title),
        (nameof(Artist), false, s => s.Artist),
        (nameof(Artists), false, s => s.Artists),
        (nameof(ArtistMusicBrainzIds), false, s => s.ArtistMusicBrainzIds),
        (nameof(AlbumArtist), true, s => s.AlbumArtist),
        (nameof(Album), true, s => s.Album),
        (nameof(Year), true, s => s.Year),
        (nameof(TrackNumber), false, s => s.TrackNumber),
        (nameof(DiscNumber), false, s => s.DiscNumber),
        (nameof(TotalDiscs), true, s => s.TotalDiscs),
        (nameof(Isrc), false, s => s.Isrc),
        (nameof(MusicBrainzRecordingId), false, s => s.MusicBrainzRecordingId),
        (nameof(MusicBrainzReleaseId), true, s => s.MusicBrainzReleaseId),
        (nameof(MusicBrainzReleaseGroupId), true, s => s.MusicBrainzReleaseGroupId),
        (nameof(AlbumArtistMusicBrainzId), true, s => s.AlbumArtistMusicBrainzId),
        (nameof(ReleaseTypes), true, s => s.ReleaseTypes),
        (nameof(IsCompilation), true, s => s.IsCompilation),
        ("Lyrics", false, s => s.Lyrics),
    ];

    /// <summary>The tag set that will land on disk for <paramref name="song"/> under <paramref name="identity"/>.</summary>
    public static WrittenTagSet From(SongMetadata song, AlbumIdentity identity)
    {
        var compilation = identity.IsCompilation;
        var albumArtist = compilation
            ? VariousArtists
            : NullIfEmpty(identity.AlbumArtist) ?? NullIfEmpty(song.Artist);

        return new WrittenTagSet(
            Title: NullIfEmpty(song.Title),
            // Mirror the writer: ARTIST is the single-value display credit.
            Artist: TagLibLibraryTagWriter.BuildDisplayArtist(song.Artist),
            // Mirror the writer's ARTISTS resolution: the discrete list, else the ';'-join fallback
            // (a credit without ';' yields NO ARTISTS frame, hence null here).
            Artists: NullIfEmpty(song.Artists) ?? (song.Artist?.Contains(';') == true ? NullIfEmpty(song.Artist) : null),
            ArtistMusicBrainzIds: NullIfEmpty(song.ArtistMusicBrainzIds),
            AlbumArtist: albumArtist,
            Album: NullIfEmpty(identity.Album),
            Year: PositiveOrNull(identity.Year),
            TrackNumber: PositiveOrNull(song.TrackNumber),
            DiscNumber: PositiveOrNull(song.DiscNumber),
            TotalDiscs: PositiveOrNull(identity.TotalDiscs),
            Isrc: NullIfEmpty(song.Isrc),
            MusicBrainzRecordingId: NullIfEmpty(song.MusicBrainzId),
            MusicBrainzReleaseId: NullIfEmpty(identity.MusicBrainzReleaseId),
            MusicBrainzReleaseGroupId: NullIfEmpty(identity.MusicBrainzReleaseGroupId),
            AlbumArtistMusicBrainzId: NullIfEmpty(identity.AlbumArtistMusicBrainzId),
            ReleaseTypes: NullIfEmpty(identity.ReleaseTypes),
            IsCompilation: compilation ? "true" : "false",
            Lyrics: HasLyrics(song) ? "present" : null);
    }

    /// <summary>
    /// The source-original baseline for a song's FIRST build, used as the "previous" set so first-build
    /// diffs show enriched-vs-source. Built by overlaying the captured <c>Original*</c> fields onto
    /// <paramref name="current"/>: fields we captured originals for can differ; fields we never captured
    /// an original for (the release-id family) stay equal to current and so never report a spurious
    /// "added" change. Compilation/lyrics presence we also don't reliably know at source, so they track
    /// current too.
    /// </summary>
    public static WrittenTagSet FromOriginal(SongMetadata song, WrittenTagSet current)
    {
        if (!song.OriginalMetadataCaptured)
        {
            // Nothing captured (shouldn't happen on a matched/built song) — treat current as the
            // baseline so the first build reports no changes rather than a flood of null→value.
            return current;
        }

        var albumArtist = song.OriginalIsCompilation
            ? VariousArtists
            : NullIfEmpty(song.OriginalAlbumArtist) ?? NullIfEmpty(song.OriginalArtist);

        return current with
        {
            Title = NullIfEmpty(song.OriginalTitle),
            Artist = NullIfEmpty(song.OriginalArtist),
            Artists = NullIfEmpty(song.OriginalArtists),
            AlbumArtist = albumArtist,
            Album = NullIfEmpty(song.OriginalAlbum),
            Year = PositiveOrNull(song.OriginalYear),
            TrackNumber = PositiveOrNull(song.OriginalTrackNumber),
            DiscNumber = PositiveOrNull(song.OriginalDiscNumber),
            TotalDiscs = PositiveOrNull(song.OriginalTotalDiscs),
            Isrc = NullIfEmpty(song.OriginalIsrc),
            MusicBrainzRecordingId = NullIfEmpty(song.OriginalMusicBrainzId),
            ReleaseTypes = NullIfEmpty(song.OriginalReleaseTypes),
            IsCompilation = song.OriginalIsCompilation ? "true" : "false",
            // MusicBrainzReleaseId / ReleaseGroupId / AlbumArtistMusicBrainzId / ArtistMusicBrainzIds /
            // Lyrics: no captured original — left equal to `current` so they don't report a change on
            // first build.
        };
    }

    /// <summary>
    /// Field-level changes from <paramref name="previous"/> to <paramref name="current"/>. Null/empty
    /// are treated as equal, so a no-op re-tag (e.g. FLAC padding rewrite) yields an empty list.
    /// </summary>
    public static IReadOnlyList<(string Field, string? Old, string? New, bool IsAlbumIdentity)> Diff(
        WrittenTagSet previous, WrittenTagSet current)
    {
        var changes = new List<(string, string?, string?, bool)>();
        foreach (var (name, isAlbumIdentity, get) in Fields)
        {
            var oldValue = NullIfEmpty(get(previous));
            var newValue = NullIfEmpty(get(current));
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                changes.Add((name, oldValue, newValue, isAlbumIdentity));
            }
        }

        return changes;
    }

    private static bool HasLyrics(SongMetadata song)
        => !string.IsNullOrWhiteSpace(song.EffectiveSyncedLyrics) || !string.IsNullOrWhiteSpace(song.EffectivePlainLyrics);

    private static string? PositiveOrNull(int? value) => value is > 0 ? value.Value.ToString() : null;

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
