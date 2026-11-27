using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.CleanEdits;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.CleanEdits;

public sealed class CleanEditSweepTests : IDisposable
{
    private readonly DbContextOptions<MusicHoarderDbContext> _options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options;

    private readonly string _folder = Directory.CreateTempSubdirectory("clean-edit-sweep").FullName;
    private readonly ScriptedReferences _references = new();
    private readonly SilentDecoder _decoder = new();
    private readonly CancellationTokenSource _shutdown = new();

    public void Dispose()
    {
        _shutdown.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private CleanEditSweep NewSweep()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new MusicHoarderDbContext(_options));
        var checker = new CleanEditChecker(_decoder, _references, NullLogger<CleanEditChecker>.Instance);
        return new CleanEditSweep(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            checker,
            new Lifetime(_shutdown.Token),
            NullLogger<CleanEditSweep>.Instance);
    }

    private async Task<int> AddSongAsync(string name, Guid? owner = null, bool built = true, bool deleted = false, int bytes = 4)
    {
        string? path = null;
        if (built)
        {
            path = Path.Combine(_folder, name + ".flac");
            await File.WriteAllBytesAsync(path, new byte[bytes]);
        }
        await using var db = new MusicHoarderDbContext(_options);
        var song = new SongMetadata
        {
            OwnerUserId = owner ?? TestUsers.OwnerId,
            SourcePath = $"/src/{name}.flac",
            FileName = name + ".flac",
            Extension = ".flac",
            FileSizeBytes = bytes,
            Fingerprint = "fp",
            Artist = "Artist",
            Title = name,
            DestinationPath = path,
            DeletedAtUtc = deleted ? DateTime.UtcNow : null,
            LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        return song.Id;
    }

    private static async Task<CleanEditSweepProgress> RunToEndAsync(CleanEditSweep sweep)
    {
        Assert.True(sweep.TryStart(TestUsers.OwnerId));
        return await WaitForEndAsync(sweep);
    }

    private static async Task<CleanEditSweepProgress> WaitForEndAsync(CleanEditSweep sweep)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (sweep.ProgressFor(TestUsers.OwnerId).Running)
        {
            Assert.True(DateTime.UtcNow < deadline, "the sweep did not finish");
            await Task.Delay(10);
        }
        return sweep.ProgressFor(TestUsers.OwnerId);
    }

    private async Task<List<CleanEditCheck>> ChecksAsync()
    {
        await using var db = new MusicHoarderDbContext(_options);
        return await db.CleanEditChecks.IgnoreQueryFilters().AsNoTracking().OrderBy(c => c.SongId).ToListAsync();
    }

    [Fact]
    public async Task It_checks_the_owners_built_songs_and_nothing_else()
    {
        var mine = await AddSongAsync("mine");
        await AddSongAsync("unbuilt", built: false);
        await AddSongAsync("deleted", deleted: true);
        await AddSongAsync("someone-elses", owner: Guid.NewGuid());

        var progress = await RunToEndAsync(NewSweep());

        var checks = await ChecksAsync();
        Assert.Equal(mine, Assert.Single(checks).SongId);
        Assert.Equal(TestUsers.OwnerId, checks[0].OwnerUserId);
        Assert.Equal(CleanEditVerdict.NoReference, checks[0].Verdict);
        Assert.Equal((1, 1, 0), (progress.Total, progress.Checked, progress.Skipped));
        Assert.Null(progress.StoppedReason);
    }

    [Fact]
    public async Task A_second_run_checks_only_what_changed()
    {
        var kept = await AddSongAsync("kept");
        var replaced = await AddSongAsync("replaced");
        var sweep = NewSweep();
        await RunToEndAsync(sweep);
        var checkedBefore = _references.Lookups;

        await File.WriteAllBytesAsync(Path.Combine(_folder, "replaced.flac"), new byte[9]);
        var progress = await RunToEndAsync(sweep);

        Assert.Equal(checkedBefore + 1, _references.Lookups);
        Assert.Equal((1, 1), (progress.Checked, progress.Skipped));
        Assert.Equal([kept, replaced], (await ChecksAsync()).Select(c => c.SongId).ToArray());
    }

    [Fact]
    public async Task A_failed_check_is_tried_again_on_the_next_run()
    {
        await AddSongAsync("flaky");
        _references.Incomplete = true;
        var sweep = NewSweep();
        var first = await RunToEndAsync(sweep);
        Assert.Equal(1, first.Failed);
        Assert.Equal(CleanEditVerdict.Failed, Assert.Single(await ChecksAsync()).Verdict);

        _references.Incomplete = false;
        var second = await RunToEndAsync(sweep);

        Assert.Equal(1, second.Checked);
        Assert.Equal(CleanEditVerdict.NoReference, Assert.Single(await ChecksAsync()).Verdict);
    }

    [Fact]
    public async Task It_stops_when_the_catalogs_keep_failing()
    {
        for (var i = 0; i < CleanEditSweep.ConsecutiveFailuresBeforeStopping + 3; i++)
            await AddSongAsync($"song{i}");
        _references.Incomplete = true;

        var progress = await RunToEndAsync(NewSweep());

        Assert.Equal(CleanEditSweep.ConsecutiveFailuresBeforeStopping, progress.Failed);
        Assert.Contains("not answering", progress.StoppedReason);
    }

    [Fact]
    public async Task Files_that_cannot_be_read_do_not_stop_the_run()
    {
        for (var i = 0; i < CleanEditSweep.ConsecutiveFailuresBeforeStopping + 3; i++)
            await AddSongAsync($"broken{i}");
        var healthy = await AddSongAsync("healthy");
        _decoder.Unreadable = path => Path.GetFileName(path).StartsWith("broken", StringComparison.Ordinal);

        var progress = await RunToEndAsync(NewSweep());

        Assert.Null(progress.StoppedReason);
        Assert.Equal(CleanEditSweep.ConsecutiveFailuresBeforeStopping + 3, progress.Failed);
        Assert.Equal(CleanEditVerdict.NoReference, (await ChecksAsync()).Single(c => c.SongId == healthy).Verdict);
    }

    [Fact]
    public async Task Without_ffmpeg_the_run_stops_and_says_so()
    {
        await AddSongAsync("a");
        await AddSongAsync("b");
        _decoder.FfmpegMissing = true;

        var progress = await RunToEndAsync(NewSweep());

        Assert.Contains("ffmpeg was not found", progress.StoppedReason);
        Assert.Empty(await ChecksAsync());
    }

    [Fact]
    public async Task A_file_replaced_with_one_of_the_same_size_is_checked_again()
    {
        await AddSongAsync("same-size");
        var sweep = NewSweep();
        await RunToEndAsync(sweep);
        var file = Path.Combine(_folder, "same-size.flac");
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddHours(1));

        var progress = await RunToEndAsync(sweep);

        Assert.Equal((1, 0), (progress.Checked, progress.Skipped));
    }

    [Fact]
    public async Task Another_account_neither_sees_nor_stops_the_run()
    {
        await AddSongAsync("slow");
        _references.Block = new TaskCompletionSource();
        var sweep = NewSweep();
        var other = Guid.NewGuid();

        Assert.True(sweep.TryStart(TestUsers.OwnerId));
        await _references.WaitUntilAskedAsync();
        sweep.Stop(other);

        Assert.False(sweep.ProgressFor(other).Running);
        Assert.Null(sweep.ProgressFor(other).Current);
        Assert.True(sweep.ProgressFor(TestUsers.OwnerId).Running);
        sweep.Stop(TestUsers.OwnerId);
        await WaitForEndAsync(sweep);
    }

    [Fact]
    public async Task Stopping_ends_the_run_and_leaves_the_rest_for_next_time()
    {
        await AddSongAsync("slow");
        _references.Block = new TaskCompletionSource();
        var sweep = NewSweep();

        Assert.True(sweep.TryStart(TestUsers.OwnerId));
        Assert.False(sweep.TryStart(TestUsers.OwnerId));
        await _references.WaitUntilAskedAsync();
        sweep.Stop(TestUsers.OwnerId);
        var progress = await WaitForEndAsync(sweep);

        Assert.Equal("Stopped.", progress.StoppedReason);
        Assert.Empty(await ChecksAsync());
        Assert.False(progress.Running);
    }

    [Fact]
    public async Task It_never_changes_a_song_row_or_its_file()
    {
        var id = await AddSongAsync("untouched");
        var file = Path.Combine(_folder, "untouched.flac");
        var before = await File.ReadAllBytesAsync(file);

        await RunToEndAsync(NewSweep());

        await using var db = new MusicHoarderDbContext(_options);
        var song = await db.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id == id);
        Assert.Equal(file, song.DestinationPath);
        Assert.Equal(4, song.FileSizeBytes);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
    }
}
