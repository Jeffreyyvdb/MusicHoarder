using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Tests.Persistence;
using static MusicHoarder.Api.Tests.BackgroundWork.MusicVideoTestData;

namespace MusicHoarder.Api.Tests.BackgroundWork;

/// <summary>
/// The page's rows come from <see cref="IBackgroundJobInsight.SummarizeAsync"/>, which is kept cheap by loading
/// less than the sheet; the sheet repeats the row at its top. These pin that the two agree on the same data, and
/// that the SQL the cheap paths send reads the same numbers on Postgres as in memory.
/// </summary>
public class SummaryMatchesSheetTests
{
    public static TheoryData<string> Jobs => [.. BackgroundJobDefinitions.All.Select(j => j.Id)];

    [Theory]
    [MemberData(nameof(Jobs))]
    public async Task The_row_is_the_summary_the_sheet_opens_with(string job)
    {
        using var h = new BackgroundWorkHarness();
        Ready(h);
        h.Seed(db => Seed(db, h.Now));

        var row = await h.SummarizeAsync(job);
        var sheet = (await h.DescribeAsync(job)).Summary;

        AssertSame(row, sheet);
    }

    [Fact]
    public async Task The_seeded_rows_carry_work_so_the_comparison_is_not_of_empty_rows()
    {
        using var h = new BackgroundWorkHarness();
        Ready(h);
        h.Seed(db => Seed(db, h.Now));

        foreach (var job in BackgroundJobDefinitions.All.Where(j => j.Id != BackgroundJobIds.TranscriptionAllowance))
        {
            var row = await h.SummarizeAsync(job.Id);
            Assert.True(row.Headline.Value > 0 || row.Trend.Sum() > 0, $"{job.Id} shows nothing");
        }
    }

    [PostgresFact]
    public async Task Every_row_reads_the_same_on_postgres_as_in_memory()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var now = BackgroundWorkTestData.Now;
        using var postgres = new BackgroundWorkHarness(now, () => database.NewContext());
        using var memory = new BackgroundWorkHarness(now);
        foreach (var h in new[] { postgres, memory })
        {
            Ready(h);
            h.Seed(db => Seed(db, now));
        }

        foreach (var job in BackgroundJobDefinitions.All)
        {
            var row = await postgres.SummarizeAsync(job.Id);
            AssertSame(row, (await postgres.DescribeAsync(job.Id)).Summary);
            AssertSame(await memory.SummarizeAsync(job.Id), row);
        }
    }

    private static void AssertSame(BackgroundJobSummaryDto expected, BackgroundJobSummaryDto actual)
    {
        Assert.Equal(expected.Headline, actual.Headline);
        Assert.Equal(expected.Trend, actual.Trend);
        Assert.Equal(expected.LastWorkedAtUtc, actual.LastWorkedAtUtc);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.StateLabel, actual.StateLabel);
    }

    private static void Ready(BackgroundWorkHarness h)
    {
        h.Settings.AutoSync = true;
        h.Settings.AlbumCompletion = true;
        h.Settings.AutoDownloadVideos = true;
        h.Disk.Available = 200L * 1024 * 1024 * 1024;
    }

    private static void Seed(MusicHoarderDbContext db, DateTime now)
    {
        if (!db.Users.IgnoreQueryFilters().Any(u => u.Id == BackgroundWorkTestData.Owner))
            SeedAdmin(db);

        const string lrc = "[00:01.00] words";
        var songs = Enumerable.Range(1, 12).Select(n => BackgroundWorkTestData.NewSong(n, s =>
        {
            s.LibraryBuildStatus = LibraryBuildStatus.Done;
            s.DestinationPath = $"/dest/{(n - 1) / 3}/{n}.mp3";
            s.HasCoverArt = n % 3 == 1;
            s.EnrichedAtUtc = now.AddDays(-5);
        })).ToList();
        songs[0].LyricsStatus = LyricsStatus.Fetched;
        songs[0].SyncedLyrics = lrc;
        songs[1].LyricsStatus = LyricsStatus.NotFound;
        songs[2].LyricsStatus = LyricsStatus.NotFound;
        songs[2].TranscriptionStatus = TranscriptionStatus.Completed;
        songs[2].TranscribedSyncedLyrics = lrc;
        songs[2].TranscribedAtUtc = now.AddHours(-30);
        songs[3].LyricsStatus = LyricsStatus.Fetched;
        songs[3].SyncedLyrics = lrc;
        songs[3].LyricsSyncStatus = LyricsSyncStatus.Corrected;
        songs[3].LyricsSyncCheckedAtUtc = now.AddDays(-3);
        db.Songs.AddRange(songs);
        db.SaveChanges();

        db.AudioAssets.AddRange(
            new AudioAsset { Sha256 = new string('a', 64), StoragePath = "/src/1.mp3", SizeBytes = 1000, Container = "mp3", CreatedAtUtc = now.AddDays(-2), LastReferencedAtUtc = now.AddDays(-2) },
            new AudioAsset { Sha256 = new string('b', 64), StoragePath = "/src/2.mp3", SizeBytes = 1000, Container = "mp3", CreatedAtUtc = now.AddHours(-3), LastReferencedAtUtc = now.AddHours(-3) });

        db.TranscriptionAudioSpends.AddRange(
            new TranscriptionAudioSpend { AtUtc = now.AddDays(-2), AudioSeconds = 600, Purpose = TranscriptionPurpose.AutoSync },
            new TranscriptionAudioSpend { AtUtc = now.AddMinutes(-20), AudioSeconds = 30, Purpose = TranscriptionPurpose.TimingProbe });

        SongProviderAttempt Attempt(int song, EnrichmentProvider provider, ProviderAttemptStatus status, DateTime at, DateTime? next = null)
            => new() { SongId = songs[song].Id, Provider = provider, Status = status, AttemptedAtUtc = at, NextRetryAfterUtc = next };
        db.SongProviderAttempts.AddRange(
            Attempt(0, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched, now.AddMinutes(-10)),
            Attempt(0, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch, now.AddHours(-26), now.AddHours(-1)),
            Attempt(1, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Failed, now.AddDays(-3), now.AddHours(-2)),
            Attempt(1, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch, now.AddDays(-13).Date.AddHours(1), now.AddDays(4)),
            Attempt(2, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched, now.AddDays(-20)));

        WishlistItem Item(WishlistItemOrigin origin, WishlistItemStatus status, TimeSpan createdAgo, TimeSpan updatedAgo, DateTime? next = null)
            => new()
            {
                OwnerUserId = BackgroundWorkTestData.Owner, Origin = origin, Status = status, NextAttemptAtUtc = next,
                CreatedAtUtc = now - createdAgo, UpdatedAtUtc = now - updatedAgo, Title = "t", Artist = "a",
            };
        db.WishlistItems.AddRange(
            Item(WishlistItemOrigin.AlbumCompletion, WishlistItemStatus.Pending, TimeSpan.FromDays(9), TimeSpan.FromDays(9)),
            Item(WishlistItemOrigin.AlbumCompletion, WishlistItemStatus.Downloaded, TimeSpan.FromDays(9), TimeSpan.FromHours(5)),
            Item(WishlistItemOrigin.AlbumCompletion, WishlistItemStatus.Downloaded, TimeSpan.FromDays(40), TimeSpan.FromDays(30)),
            Item(WishlistItemOrigin.AlbumCompletion, WishlistItemStatus.Failed, TimeSpan.FromDays(2), TimeSpan.FromDays(1), now.AddHours(3)),
            Item(WishlistItemOrigin.ArtistMonitor, WishlistItemStatus.Downloaded, TimeSpan.FromDays(3), TimeSpan.FromDays(2)),
            Item(WishlistItemOrigin.UserRequested, WishlistItemStatus.Pending, TimeSpan.FromHours(1), TimeSpan.FromHours(1)));

        LibraryWriteEvent Cover(string folder, DateTime at, string value) => new()
        {
            OwnerUserId = BackgroundWorkTestData.Owner, Kind = LibraryWriteEventKind.AlbumCoverWritten,
            WrittenAtUtc = at, AlbumFolder = folder, NewValue = value,
        };
        db.LibraryWriteEvents.AddRange(
            Cover("/dest/0", now.AddDays(-1), "fetched:coverartarchive"),
            Cover("/dest/1", now.AddDays(-60), "fetched:spotify"));
        db.AlbumCoverFetchAttempts.Add(new AlbumCoverFetchAttempt
        {
            AlbumFolder = "/dest/2", Status = AlbumCoverFetchStatus.NotFound, AttemptCount = 1,
            LastAttemptAtUtc = now.AddHours(-4), NextRetryAfterUtc = now.AddDays(2),
        });

        db.SongVideoCandidateSets.AddRange(
            Found(songs[4], now.AddDays(-1), null, Confident()),
            Found(songs[5], now.AddDays(-9), null, Weak()),
            Nothing(songs[6], now.AddDays(-2), 1));
        db.SongMusicVideos.Add(Video(songs[7], MusicVideoStatus.Ready, MusicVideoPick.Automatic, now.AddHours(-6)));

        db.SongQualityGrades.AddRange(
            new SongQualityGrade
            {
                SongId = songs[0].Id, OwnerUserId = BackgroundWorkTestData.Owner, Verdict = SongQualityVerdict.Good, Score = 80,
                GradedAtUtc = now.AddDays(-1), Summary = "ok", PromptVersion = QualityGradingPrompt.Version, Model = "m",
            },
            new SongQualityGrade
            {
                SongId = songs[1].Id, OwnerUserId = BackgroundWorkTestData.Owner, Verdict = SongQualityVerdict.Wrong, Score = 10,
                GradedAtUtc = now.AddDays(-8), Summary = "no", PromptVersion = QualityGradingPrompt.Version, Model = "m",
            });
        db.SaveChanges();
    }
}
