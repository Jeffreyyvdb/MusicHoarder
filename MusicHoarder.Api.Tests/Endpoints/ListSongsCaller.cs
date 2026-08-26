using Microsoft.AspNetCore.Http;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Sharing;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// Calls <see cref="SongsEndpoints.ListSongs"/> with the options it takes from DI in production, so
/// tests that don't care about provenance don't have to build them. The path roots default to empty
/// (every song reads as Scanned); pass them to exercise origin resolution.
/// </summary>
internal static class ListSongsCaller
{
    internal static Task<IResult> Invoke(
        MusicHoarderDbContext db,
        string? downloadDirectory = null,
        string? syncedSourceDirectory = null,
        bool includeDeleted = false,
        string? enrichmentStatus = null,
        CurrentUser? caller = null) =>
        SongsEndpoints.ListSongs(
            db,
            TestLibraryScope.For(caller),
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/source",
                DestinationDirectory = "/dest",
                DownloadDirectory = downloadDirectory ?? string.Empty,
            }),
            Microsoft.Extensions.Options.Options.Create(new SyncOptions
            {
                SyncedSourceDirectory = syncedSourceDirectory ?? string.Empty,
            }),
            CancellationToken.None,
            includeDeleted,
            enrichmentStatus);
}
