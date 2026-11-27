using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.CleanEdits;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Persistence;

namespace MusicHoarder.Api.Tests.CleanEdits;

public sealed class CleanEditPostgresTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("clean-edit-pg").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [PostgresFact]
    public async Task The_sweep_and_the_list_run_on_postgres()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        int downloaded, local;
        await using (var seed = database.NewContext())
        {
            var songs = new[] { Song("Stronger"), Song("Wandered To LA") };
            seed.Songs.AddRange(songs);
            await seed.SaveChangesAsync();
            seed.WishlistItems.Add(new WishlistItem
            {
                OwnerUserId = TestUsers.OwnerId,
                Title = "Stronger",
                Artist = "Kanye West",
                DurationMs = 312_893,
                Status = WishlistItemStatus.Downloaded,
                DownloadedSongId = songs[0].Id,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
            (downloaded, local) = (songs[0].Id, songs[1].Id);
        }

        var references = new ScriptedReferences();
        var services = new ServiceCollection();
        services.AddScoped(_ => database.NewContext());
        var sweep = new CleanEditSweep(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new CleanEditChecker(new SilentDecoder(), references, NullLogger<CleanEditChecker>.Instance),
            new Lifetime(CancellationToken.None),
            NullLogger<CleanEditSweep>.Instance);
        Assert.True(sweep.TryStart(TestUsers.OwnerId));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (sweep.ProgressFor(TestUsers.OwnerId).Running)
        {
            Assert.True(DateTime.UtcNow < deadline, "the sweep did not finish");
            await Task.Delay(10);
        }

        Assert.Null(sweep.ProgressFor(TestUsers.OwnerId).StoppedReason);
        Assert.Equal(2, sweep.ProgressFor(TestUsers.OwnerId).Checked);
        var asked = references.Asked.ToDictionary(q => q.Title, q => q.DurationsSeconds);
        Assert.Equal([312.893, 5], asked["Stronger"]);
        Assert.Equal([5], asked["Wandered To LA"]);

        await using (var db = database.NewContext())
        {
            var check = await db.CleanEditChecks.IgnoreQueryFilters().SingleAsync(c => c.SongId == local);
            check.Verdict = CleanEditVerdict.CleanMaster;
            check.Margin = 0.97;
            await db.SaveChangesAsync();
        }

        await using var caller = database.NewContextFor(TestCurrentUserAccessor.OwnerUser);
        var list = Assert.IsType<Ok<CleanEditEndpoints.CleanEditListDto>>(
            await CleanEditEndpoints.List(caller, null, null, CancellationToken.None)).Value!;
        Assert.Equal(1, list.Counts["cleanMaster"]);
        Assert.Equal(1, list.Counts["noReference"]);
        Assert.Equal(2, list.Eligible);
        Assert.Equal(local, Assert.Single(list.Items).SongId);
        Assert.NotEqual(downloaded, list.Items[0].SongId);
    }

    private SongMetadata Song(string title)
    {
        var path = Path.Combine(_folder, title + ".flac");
        File.WriteAllBytes(path, new byte[4]);
        return new SongMetadata
        {
            OwnerUserId = TestUsers.OwnerId,
            SourcePath = $"/src/{title}.flac",
            FileName = title + ".flac",
            Extension = ".flac",
            FileSizeBytes = 4,
            Fingerprint = "fp",
            Artist = "Artist",
            Title = title,
            DestinationPath = path,
            LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
    }
}
