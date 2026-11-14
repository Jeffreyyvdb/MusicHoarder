using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artists;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Deezer;

namespace MusicHoarder.Api.Tests.Artists;

/// <summary>Shared fixtures for the artist discography and follow tests.</summary>
internal static class ArtistTestKit
{
    public static Guid Owner => WellKnownUsers.OwnerId;

    public static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    public static MusicEnricherOptions EnricherOptions(Action<MusicEnricherOptions>? configure = null)
    {
        var opts = new MusicEnricherOptions
        {
            SourceDirectory = "/src",
            DestinationDirectory = "/dst",
            EnableWishlistDownloads = true,
        };
        configure?.Invoke(opts);
        return opts;
    }

    public static SongMetadata Song(
        string title, string album, string albumArtist = "Daft Punk", string? artists = null,
        SongAcquisitionIntent intent = SongAcquisitionIntent.Explicit, int? track = null) => new()
        {
            OwnerUserId = Owner,
            SourcePath = $"/src/{albumArtist}/{album}/{title}.flac",
            FileName = $"{title}.flac",
            Extension = ".flac",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            EnrichmentStatus = EnrichmentStatus.Matched,
            Title = title,
            Album = album,
            AlbumArtist = albumArtist,
            Artist = albumArtist,
            Artists = artists,
            TrackNumber = track,
            AcquisitionIntent = intent,
        };

    public static DeezerArtistRelease Release(
        string id, string title, string recordType = "album", string? date = "2020-01-01", bool isExplicit = false) =>
        new(id, title, recordType, date is null ? null : DateOnly.Parse(date), $"https://cdn/{id}.jpg", isExplicit);

    public static DeezerAlbumDetail Album(string id, string title, string artist, params string[] tracks) =>
        new(id, title, artist, 2020, $"https://cdn/{id}.jpg",
            tracks.Select((t, i) => new DeezerAlbumTrackItem(1, i + 1, t, 200_000, $"{id}-{i + 1}")).ToList());

    public static ArtistReleaseQueuer Queuer(
        MusicHoarderDbContext db, FakeDeezerCatalogService deezer, MusicEnricherOptions? opts = null) =>
        new(db, deezer, new NoIdentities(), Microsoft.Extensions.Options.Options.Create(opts ?? EnricherOptions()),
            NullLogger<ArtistReleaseQueuer>.Instance);

    /// <summary>Spotify is not reachable in tests; every track stays unidentified.</summary>
    public sealed class NoIdentities : IAlbumCompletionIdentityResolver
    {
        public Task<IReadOnlyDictionary<int, string>> ResolveSpotifyTrackIdsAsync(
            CanonicalAlbum album, IReadOnlyList<CanonicalAlbumTrack> tracks, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    }

    public sealed class RecordingNotifier : IArtistReleaseNotifier
    {
        public List<NewReleaseNotice> Sent { get; } = [];

        public Task NotifyAsync(NewReleaseNotice notice, CancellationToken ct)
        {
            Sent.Add(notice);
            return Task.CompletedTask;
        }
    }
}
