namespace MusicHoarder.Api.Persistence;

/// <summary>
/// One play, appended when a client reports a track start. The listening stats are computed from
/// these rows alone.
///
/// <para>
/// <see cref="SongId"/> is a plain number on purpose: no navigation and no foreign key, so a
/// soft-deleted, re-keyed or removed song cannot change or cascade away what was listened to, and
/// nothing can be tempted to join across tenants. The title, artists, album, genre and duration are
/// copied at write time for the same reason: a play is an immutable fact, and reading the stats
/// needs only the caller's own rows.
/// </para>
/// </summary>
public class ListeningEvent
{
    public const int MaxTextLength = 512;

    public long Id { get; set; }

    /// <summary>The listener, not the song's owner.</summary>
    public Guid UserId { get; set; }

    public int SongId { get; set; }

    public DateTime PlayedAtUtc { get; set; }

    public required string Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public int? DurationSeconds { get; set; }

    public static ListeningEvent From(Guid userId, SongMetadata song, DateTime playedAtUtc) => new()
    {
        UserId = userId,
        SongId = song.Id,
        PlayedAtUtc = playedAtUtc,
        Title = Clip(song.Title) ?? Clip(song.FileName) ?? string.Empty,
        Artist = Clip(song.Artist),
        AlbumArtist = Clip(song.AlbumArtist),
        Album = Clip(song.Album),
        Genre = Clip(song.Genre),
        DurationSeconds = song.DurationSeconds,
    };

    private static string? Clip(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= MaxTextLength ? value : value[..MaxTextLength];
}
