using System.Text.Json;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.BackgroundWork;

internal static class MusicVideoTestData
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void SeedAdmin(MusicHoarderDbContext db)
    {
        db.Users.Add(new User
        {
            Id = BackgroundWorkTestData.Owner, Email = "a@example.com", EmailNormalized = "A@EXAMPLE.COM", Role = UserRole.Admin,
        });
        db.SaveChanges();
    }

    public static CachedVideoCandidate Confident(long bytes = 60_000_000, int score = 90) =>
        new("vid-confident", "Artist - Song (Official Video)", "Artist", 200, score, true, MusicVideoMotion.RealVideo, bytes, false);

    public static CachedVideoCandidate Weak(int score = 30) =>
        new("vid-weak", "Artist - Song (Lyrics)", "Fan", 200, score, true, MusicVideoMotion.Static, 5_000_000, false);

    public static string Json(params CachedVideoCandidate[] candidates) => JsonSerializer.Serialize(candidates, Web);

    public static SongVideoCandidateSet Set(
        SongMetadata song, string json, DateTime searchedAt, DateTime? next = null, int emptySearches = 0, string? error = null)
        => new()
        {
            SongId = song.Id,
            QueryKey = MusicVideoCandidateCache.QueryKeyFor(song),
            SearchedAtUtc = searchedAt,
            CandidatesJson = json,
            Error = error,
            NextSearchAtUtc = next ?? searchedAt.AddDays(30),
            EmptySearches = emptySearches,
        };

    public static SongVideoCandidateSet Found(SongMetadata song, DateTime searchedAt, DateTime? next = null, params CachedVideoCandidate[] candidates)
        => Set(song, Json(candidates), searchedAt, next);

    public static SongVideoCandidateSet Nothing(SongMetadata song, DateTime searchedAt, int emptySearches, DateTime? next = null)
        => Set(song, "[]", searchedAt, next, emptySearches, MusicVideoCandidateCache.NoCandidatesError);

    public static SongMusicVideo Video(
        SongMetadata song, MusicVideoStatus status, MusicVideoPick pick, DateTime? fetchedAt = null)
        => new() { SongId = song.Id, Status = status, Pick = pick, FetchedAtUtc = fetchedAt ?? default, FilePath = status == MusicVideoStatus.Ready ? $"/v/{song.Id}.mp4" : null };
}
