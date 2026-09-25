using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// Pins the full <c>GET /api/insights</c> payload — every field, label, ordering, rounding rule and
/// tenancy boundary — through the handler, so the wire shape both clients read cannot drift. The
/// fixture seeds another owner's and the demo owner's rows beside the caller's, and the golden
/// payloads below were produced by the handler and checked by hand against the fixture.
/// </summary>
public class InsightsPayloadTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Demo = WellKnownUsers.DemoId;
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;

    private static JsonObject Payload(IResult result)
    {
        var value = ((IValueHttpResult)result).Value!;
        return JsonSerializer.SerializeToNode(value, value.GetType())!.AsObject();
    }

    /// <summary>The payload minus its wall-clock stamp, indented, for golden comparison.</summary>
    private static string Stable(JsonObject payload)
    {
        payload.Remove("generatedAtUtc");
        return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task<JsonObject> InsightsAs(DbContextOptions<MusicHoarderDbContext> options, Guid caller)
    {
        using var db = new MusicHoarderDbContext(options, new StubCurrentUser(caller));
        return Payload(await DashboardEndpoints.GetInsights(db));
    }

    private static SongMetadata Song(Guid owner, int n, string extension = ".mp3") => new()
    {
        OwnerUserId = owner,
        SourcePath = $"/src/{owner:N}/{n}{extension}",
        FileName = $"{n}{extension}",
        Extension = extension,
        FileSizeBytes = 1_000_000L * n,
        LastModifiedUtc = T0,
        IndexedAtUtc = T0.AddDays(n),
    };

    private static SongMetadata Built(SongMetadata s, string? albumArtist, string? artist, string? album)
    {
        s.LibraryBuildStatus = LibraryBuildStatus.Done;
        s.DestinationPath = $"/dest/{s.FileName}";
        s.AlbumArtist = albumArtist;
        s.Artist = artist;
        s.Album = album;
        return s;
    }

    private static WishlistItem Wish(Guid owner, WishlistSource? source, string id, WishlistItemStatus status, SongMetadata? downloaded = null) => new()
    {
        OwnerUserId = owner,
        WishlistSource = source,
        SpotifyTrackId = id,
        Title = id,
        Artist = "x",
        Status = status,
        DownloadedSong = downloaded,
    };

    private static SongProviderAttempt Attempt(SongMetadata song, EnrichmentProvider provider, ProviderAttemptStatus status) => new()
    {
        Song = song,
        Provider = provider,
        Status = status,
        AttemptedAtUtc = T0,
    };

    private static LibraryWriteEvent WriteEvent(Guid owner, LibraryWriteEventKind kind, string? folder) => new()
    {
        OwnerUserId = owner,
        Kind = kind,
        WrittenAtUtc = T0,
        AlbumFolder = folder,
        FieldName = "Cover",
        NewValue = "written",
    };

    /// <summary>
    /// A small library for <see cref="Owner"/> that exercises every branch of the payload, plus a
    /// built song, a cover write, a wishlist and a provider attempt for each of <see cref="Other"/>
    /// and <see cref="Demo"/> that must never show up in the owner's numbers.
    /// </summary>
    private static void SeedRichLibrary(DbContextOptions<MusicHoarderDbContext> options)
    {
        using var db = new MusicHoarderDbContext(options);

        // Built, fully enriched.
        var s1 = Built(Song(Owner, 1), "Artist A", "Artist A", "Album 1");
        s1.HasCoverArt = true;
        s1.LyricsStatus = LyricsStatus.Fetched;
        s1.EnrichmentStatus = EnrichmentStatus.Matched;
        s1.MatchConfidence = 0.95;
        s1.Fingerprint = "fp1";
        s1.MusicBrainzId = "mb1";
        s1.SpotifyId = "sp1";
        s1.Isrc = "isrc1";
        s1.DurationSeconds = 200;

        // Built; artist only via a padded, lower-cased Artist tag and a padded album — merges with s1.
        var s2 = Built(Song(Owner, 2, ".MP3"), null, " artist a ", "album 1 ");
        s2.EnrichmentStatus = EnrichmentStatus.Matched;
        s2.MatchConfidence = 0.80;
        s2.Fingerprint = "fp2";
        s2.SpotifyId = "sp2";
        s2.DurationSeconds = 160;

        // Built, no album; low-confidence match, instrumental, manually approved, flagged duplicate.
        var s3 = Built(Song(Owner, 3, ".flac"), "Artist B", "ignored", null);
        s3.EnrichmentStatus = EnrichmentStatus.Matched;
        s3.MatchConfidence = 0.5;
        s3.LyricsStatus = LyricsStatus.Instrumental;
        s3.IsManuallyApproved = true;
        s3.IsDuplicate = true;
        s3.Fingerprint = "fp3";
        s3.HasCoverArt = true;
        s3.DurationSeconds = 3600;

        // Not built: review, failed, pending, a Tagged row with a destination, a Done row without one.
        var s4 = Song(Owner, 4, "flac");
        s4.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        s4.Fingerprint = "fp4";
        s4.LyricsStatus = LyricsStatus.NotFound;
        s4.MusicBrainzId = "";

        var s5 = Song(Owner, 5, "");
        s5.EnrichmentStatus = EnrichmentStatus.Failed;
        s5.LyricsStatus = LyricsStatus.Failed;
        s5.Fingerprint = "";

        var s6 = Song(Owner, 6, ".ogg");
        s6.LibraryBuildStatus = LibraryBuildStatus.Tagged;
        s6.DestinationPath = "/dest/6.ogg";
        s6.EnrichmentStatus = EnrichmentStatus.Matched; // matched but no confidence recorded

        var s7 = Song(Owner, 7);
        s7.LibraryBuildStatus = LibraryBuildStatus.Done;
        s7.Isrc = "isrc7";

        // Soft-deleted built song: out of every song count.
        var s8 = Built(Song(Owner, 8), "Deleted", "Deleted", "Gone");
        s8.EnrichmentStatus = EnrichmentStatus.Matched;
        s8.MatchConfidence = 0.99;
        s8.DeletedAtUtc = T0;

        db.Songs.AddRange(s1, s2, s3, s4, s5, s6, s7, s8);

        // Inserted so that, within equal match counts, the provider with fewer attempts comes first:
        // the payload's "more attempts first" tie-break must reorder them.
        db.SongProviderAttempts.AddRange(
            Attempt(s8, EnrichmentProvider.Tracker, ProviderAttemptStatus.Matched),
            Attempt(s5, EnrichmentProvider.Deezer, ProviderAttemptStatus.NoMatch),
            Attempt(s1, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched),
            Attempt(s1, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched),
            Attempt(s2, EnrichmentProvider.AcoustID, ProviderAttemptStatus.NoMatch),
            Attempt(s2, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched),
            Attempt(s3, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Failed),
            Attempt(s4, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.NoMatch));

        db.LibraryWriteEvents.AddRange(
            WriteEvent(Owner, LibraryWriteEventKind.AlbumCoverWritten, "/dest/Artist A/Album 1"),
            WriteEvent(Owner, LibraryWriteEventKind.AlbumCoverWritten, "/dest/Artist A/Album 1"),
            WriteEvent(Owner, LibraryWriteEventKind.AlbumCoverWritten, "/dest/Artist B/Other"),
            WriteEvent(Owner, LibraryWriteEventKind.AlbumCoverWritten, null),
            WriteEvent(Owner, LibraryWriteEventKind.TrackTagsWritten, "/dest/Artist C/Tags"));

        var liked = new WishlistSource { OwnerUserId = Owner, SourceType = WishlistSourceType.LikedSongs, Name = "Liked Songs" };
        var playlist = new WishlistSource { OwnerUserId = Owner, SourceType = WishlistSourceType.Playlist, Name = "Mix", SpotifyPlaylistId = "pl" };
        db.WishlistSources.AddRange(liked, playlist);
        db.WishlistItems.AddRange(
            Wish(Owner, liked, "l1", WishlistItemStatus.Downloaded, s1),   // downloaded + in library
            Wish(Owner, liked, "l2", WishlistItemStatus.Downloaded, s4),   // downloaded, not built
            Wish(Owner, liked, "l3", WishlistItemStatus.Pending, s7),      // linked song ⇒ counts as downloaded
            Wish(Owner, liked, "l4", WishlistItemStatus.Downloaded, s8),   // linked song soft-deleted
            Wish(Owner, liked, "l5", WishlistItemStatus.SkippedOwned),
            Wish(Owner, liked, "l6", WishlistItemStatus.Downloading),
            Wish(Owner, liked, "l7", WishlistItemStatus.Pending),
            Wish(Owner, liked, "l8", WishlistItemStatus.NotFound),
            Wish(Owner, liked, "l9", WishlistItemStatus.Failed),
            Wish(Owner, playlist, "p1", WishlistItemStatus.Downloaded, s2),
            Wish(Owner, null, "n1", WishlistItemStatus.Pending));

        foreach (var foreign in new[] { Other, Demo })
        {
            var f = Built(Song(foreign, 50, ".wav"), "Foreign", "Foreign", "Foreign LP");
            f.HasCoverArt = true;
            f.LyricsStatus = LyricsStatus.Fetched;
            f.EnrichmentStatus = EnrichmentStatus.Matched;
            f.MatchConfidence = 0.99;
            f.DurationSeconds = 999;
            db.Songs.Add(f);
            db.SongProviderAttempts.Add(Attempt(f, EnrichmentProvider.AppleMusic, ProviderAttemptStatus.Matched));
            db.LibraryWriteEvents.Add(WriteEvent(foreign, LibraryWriteEventKind.AlbumCoverWritten, "/dest/Foreign"));
            var src = new WishlistSource { OwnerUserId = foreign, SourceType = WishlistSourceType.LikedSongs, Name = "Liked Songs" };
            db.WishlistSources.Add(src);
            db.WishlistItems.Add(Wish(foreign, src, $"f-{foreign:N}", WishlistItemStatus.Downloaded, f));
        }

        db.SaveChanges();
    }

    [Fact]
    public async Task Payload_ForARichLibrary_IsExactlyThePinnedJson()
    {
        var options = NewOptions();
        SeedRichLibrary(options);

        var json = Stable(await InsightsAs(options, Owner));

        Assert.Equal(RichLibraryGolden, json);
    }

    [Fact]
    public async Task Payload_AsWrittenToTheResponse_MatchesTheGolden()
    {
        var options = NewOptions();
        SeedRichLibrary(options);
        using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        var result = await DashboardEndpoints.GetInsights(db);

        // Execute the result the way ASP.NET does, through the host's JSON options.
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);

        http.Response.Body.Position = 0;
        var written = JsonNode.Parse(http.Response.Body)!.AsObject();
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.True(written.ContainsKey("generatedAtUtc"));
        written.Remove("generatedAtUtc");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(RichLibraryGolden), written));
    }

    [Fact]
    public async Task Payload_ForAnEmptyLibrary_IsAllZerosWithNullDates()
    {
        var options = NewOptions();

        var json = Stable(await InsightsAs(options, Owner));

        Assert.Equal(EmptyLibraryGolden, json);
    }

    [Fact]
    public async Task GeneratedAtUtc_IsTheRequestTime()
    {
        var before = DateTime.UtcNow;
        var payload = await InsightsAs(NewOptions(), Owner);
        var after = DateTime.UtcNow;

        var stamp = payload["generatedAtUtc"]!.GetValue<DateTime>();
        Assert.InRange(stamp, before, after);
    }

    [Fact]
    public async Task TopArtistsAndAlbums_AreTheEightLargest_TiesByName()
    {
        var options = NewOptions();
        using (var db = new MusicHoarderDbContext(options))
        {
            // Artist k has k tracks, each on album "LP k"; names chosen so name order ≠ count order.
            var n = 0;
            foreach (var (name, tracks) in new[] { ("zeta", 3), ("Alpha", 3), ("beta", 5), ("Gamma", 1), ("delta", 2), ("Eps", 4), ("eta", 6), ("Theta", 7), ("iota", 2), ("Kappa", 1) })
                for (var i = 0; i < tracks; i++)
                    db.Songs.Add(Built(Song(Owner, ++n), name, null, $"LP {name}"));
            db.SaveChanges();
        }

        var top = (await InsightsAs(options, Owner))["top"]!;

        Assert.Equal(
            ["Theta:7", "eta:6", "beta:5", "Eps:4", "Alpha:3", "zeta:3", "delta:2", "iota:2"],
            top["artists"]!.AsArray().Select(a => $"{a!["name"]}:{a["tracks"]}"));
        Assert.Equal(
            ["LP Theta/Theta:7", "LP eta/eta:6", "LP beta/beta:5", "LP Eps/Eps:4", "LP Alpha/Alpha:3", "LP zeta/zeta:3", "LP delta/delta:2", "LP iota/iota:2"],
            top["albums"]!.AsArray().Select(a => $"{a!["album"]}/{a["artist"]}:{a["tracks"]}"));
    }

    [Fact]
    public async Task EachCaller_SeesOnlyTheirOwnRows()
    {
        var options = NewOptions();
        SeedRichLibrary(options);

        foreach (var caller in new[] { Other, Demo })
        {
            var payload = await InsightsAs(options, caller);

            Assert.Equal(1, payload["source"]!["indexed"]!.GetValue<int>());
            Assert.Equal(1, payload["covers"]!["albumCoversAdded"]!.GetValue<int>());
            Assert.Equal(1, payload["wishlist"]!["sources"]!.GetValue<int>());
            Assert.Equal(1, payload["wishlist"]!["all"]!["total"]!.GetValue<int>());
            Assert.Equal(1, payload["wishlist"]!["liked"]!["inLibrary"]!.GetValue<int>());
            var provider = Assert.Single(payload["quality"]!["byProvider"]!.AsArray());
            Assert.Equal("AppleMusic", provider!["provider"]!.GetValue<string>());
            Assert.Equal("Foreign", payload["top"]!["artists"]![0]!["name"]!.GetValue<string>());
        }
    }

    private const string RichLibraryGolden = """
        {
          "source": {
            "indexed": 7,
            "inLibrary": 3,
            "inLibraryPct": 42.9,
            "notYetBuilt": 4
          },
          "funnel": [
            {
              "stage": "Indexed",
              "count": 7,
              "pct": 100
            },
            {
              "stage": "Fingerprinted",
              "count": 4,
              "pct": 57.1
            },
            {
              "stage": "Matched",
              "count": 4,
              "pct": 57.1
            },
            {
              "stage": "In library",
              "count": 3,
              "pct": 42.9
            }
          ],
          "covers": {
            "albumCoversAdded": 2,
            "builtWithCover": 2,
            "builtTracks": 3,
            "coveragePct": 66.7
          },
          "lyrics": {
            "added": 1,
            "builtWithLyrics": 1,
            "builtTracks": 3,
            "coveragePct": 33.3,
            "instrumental": 1,
            "notFound": 1,
            "breakdown": [
              {
                "status": "Fetched",
                "count": 1
              },
              {
                "status": "Instrumental",
                "count": 1
              },
              {
                "status": "Not found",
                "count": 1
              },
              {
                "status": "Not fetched",
                "count": 3
              },
              {
                "status": "Failed",
                "count": 1
              }
            ]
          },
          "wishlist": {
            "liked": {
              "total": 9,
              "downloaded": 4,
              "inLibrary": 1,
              "skippedOwned": 1
            },
            "all": {
              "total": 11,
              "downloaded": 5,
              "inLibrary": 2
            },
            "sources": 2,
            "funnel": [
              {
                "stage": "Liked / wishlisted",
                "count": 9,
                "pct": 100
              },
              {
                "stage": "Downloaded",
                "count": 4,
                "pct": 44.4
              },
              {
                "stage": "In library",
                "count": 1,
                "pct": 11.1
              }
            ],
            "statusBreakdown": [
              {
                "status": "In library",
                "count": 1
              },
              {
                "status": "Downloaded",
                "count": 3
              },
              {
                "status": "Already owned",
                "count": 1
              },
              {
                "status": "Downloading",
                "count": 1
              },
              {
                "status": "Pending",
                "count": 2
              },
              {
                "status": "Not found",
                "count": 1
              },
              {
                "status": "Failed",
                "count": 1
              }
            ]
          },
          "totals": {
            "builtTracks": 3,
            "totalHours": 1.1,
            "totalGiB": 0.01,
            "distinctArtists": 2,
            "distinctAlbums": 1,
            "duplicates": 1,
            "oldestIndexedUtc": "2024-01-02T00:00:00Z",
            "newestIndexedUtc": "2024-01-08T00:00:00Z",
            "byFormat": [
              {
                "format": "mp3",
                "count": 3
              },
              {
                "format": "flac",
                "count": 2
              },
              {
                "format": "ogg",
                "count": 1
              }
            ]
          },
          "top": {
            "artists": [
              {
                "name": "Artist A",
                "tracks": 2
              },
              {
                "name": "Artist B",
                "tracks": 1
              }
            ],
            "albums": [
              {
                "album": "Album 1",
                "artist": "Artist A",
                "tracks": 2
              }
            ]
          },
          "quality": {
            "enrichment": [
              {
                "status": "Matched",
                "count": 4
              },
              {
                "status": "Needs review",
                "count": 1
              },
              {
                "status": "Failed",
                "count": 1
              },
              {
                "status": "Pending",
                "count": 1
              }
            ],
            "confidence": [
              {
                "bucket": "90%\u002B",
                "count": 1
              },
              {
                "bucket": "75-90%",
                "count": 1
              },
              {
                "bucket": "\u003C75%",
                "count": 1
              }
            ],
            "byProvider": [
              {
                "provider": "SpotifyAPI",
                "total": 2,
                "matched": 2
              },
              {
                "provider": "AcoustID",
                "total": 2,
                "matched": 1
              },
              {
                "provider": "Tracker",
                "total": 1,
                "matched": 1
              },
              {
                "provider": "MusicBrainzWeb",
                "total": 2,
                "matched": 0
              },
              {
                "provider": "Deezer",
                "total": 1,
                "matched": 0
              }
            ],
            "manualApprovals": 1,
            "coverage": {
              "fingerprint": {
                "count": 4,
                "pct": 57.1
              },
              "musicBrainz": {
                "count": 1,
                "pct": 14.3
              },
              "spotify": {
                "count": 2,
                "pct": 28.6
              },
              "isrc": {
                "count": 2,
                "pct": 28.6
              }
            }
          }
        }
        """;

    private const string EmptyLibraryGolden = """
        {
          "source": {
            "indexed": 0,
            "inLibrary": 0,
            "inLibraryPct": 0,
            "notYetBuilt": 0
          },
          "funnel": [
            {
              "stage": "Indexed",
              "count": 0,
              "pct": 0
            },
            {
              "stage": "Fingerprinted",
              "count": 0,
              "pct": 0
            },
            {
              "stage": "Matched",
              "count": 0,
              "pct": 0
            },
            {
              "stage": "In library",
              "count": 0,
              "pct": 0
            }
          ],
          "covers": {
            "albumCoversAdded": 0,
            "builtWithCover": 0,
            "builtTracks": 0,
            "coveragePct": 0
          },
          "lyrics": {
            "added": 0,
            "builtWithLyrics": 0,
            "builtTracks": 0,
            "coveragePct": 0,
            "instrumental": 0,
            "notFound": 0,
            "breakdown": [
              {
                "status": "Fetched",
                "count": 0
              },
              {
                "status": "Instrumental",
                "count": 0
              },
              {
                "status": "Not found",
                "count": 0
              },
              {
                "status": "Not fetched",
                "count": 0
              },
              {
                "status": "Failed",
                "count": 0
              }
            ]
          },
          "wishlist": {
            "liked": {
              "total": 0,
              "downloaded": 0,
              "inLibrary": 0,
              "skippedOwned": 0
            },
            "all": {
              "total": 0,
              "downloaded": 0,
              "inLibrary": 0
            },
            "sources": 0,
            "funnel": [
              {
                "stage": "Liked / wishlisted",
                "count": 0,
                "pct": 0
              },
              {
                "stage": "Downloaded",
                "count": 0,
                "pct": 0
              },
              {
                "stage": "In library",
                "count": 0,
                "pct": 0
              }
            ],
            "statusBreakdown": [
              {
                "status": "In library",
                "count": 0
              },
              {
                "status": "Downloaded",
                "count": 0
              },
              {
                "status": "Already owned",
                "count": 0
              },
              {
                "status": "Downloading",
                "count": 0
              },
              {
                "status": "Pending",
                "count": 0
              },
              {
                "status": "Not found",
                "count": 0
              },
              {
                "status": "Failed",
                "count": 0
              }
            ]
          },
          "totals": {
            "builtTracks": 0,
            "totalHours": 0,
            "totalGiB": 0,
            "distinctArtists": 0,
            "distinctAlbums": 0,
            "duplicates": 0,
            "oldestIndexedUtc": null,
            "newestIndexedUtc": null,
            "byFormat": []
          },
          "top": {
            "artists": [],
            "albums": []
          },
          "quality": {
            "enrichment": [
              {
                "status": "Matched",
                "count": 0
              },
              {
                "status": "Needs review",
                "count": 0
              },
              {
                "status": "Failed",
                "count": 0
              },
              {
                "status": "Pending",
                "count": 0
              }
            ],
            "confidence": [
              {
                "bucket": "90%\u002B",
                "count": 0
              },
              {
                "bucket": "75-90%",
                "count": 0
              },
              {
                "bucket": "\u003C75%",
                "count": 0
              }
            ],
            "byProvider": [],
            "manualApprovals": 0,
            "coverage": {
              "fingerprint": {
                "count": 0,
                "pct": 0
              },
              "musicBrainz": {
                "count": 0,
                "pct": 0
              },
              "spotify": {
                "count": 0,
                "pct": 0
              },
              "isrc": {
                "count": 0,
                "pct": 0
              }
            }
          }
        }
        """;

    private sealed class StubCurrentUser(Guid userId) : ICurrentUserAccessor
    {
        public CurrentUser? User { get; } = new(userId, "owner@test", UserRole.Admin, "Owner");
        public Guid UserId => userId;
    }
}
