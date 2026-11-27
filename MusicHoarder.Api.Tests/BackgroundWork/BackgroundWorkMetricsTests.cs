using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public sealed class BackgroundWorkMetricsTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    private readonly MutableClock _clock = new(Today);
    private readonly string _database = Guid.NewGuid().ToString("N");
    private readonly List<BackgroundJobSummaryDto> _rows = [];
    private readonly HashSet<string> _broken = [];
    private readonly List<string> _readsAs = [];
    private TaskCompletionSource? _gate;
    private bool _insightsUnresolvable;
    private ServiceProvider? _provider;

    public void Dispose() => _provider?.Dispose();

    private BackgroundWorkMetrics Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<IHostApplicationLifetime, ApplicationLifetime>();
        services.AddHttpContextAccessor();
        services.AddScoped<HttpContextCurrentUserAccessor>();
        services.AddScoped<ICurrentUserAccessor>(sp => sp.GetRequiredService<HttpContextCurrentUserAccessor>());
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(_database).Options;
        services.AddScoped(sp => new MusicHoarderDbContext(options, sp.GetRequiredService<ICurrentUserAccessor>()));
        foreach (var id in BackgroundJobIds.All)
            services.AddScoped<IBackgroundJobInsight>(sp => _insightsUnresolvable
                ? throw new InvalidOperationException("no scope")
                : new FakeInsight(id, this, sp.GetRequiredService<ICurrentUserAccessor>()));
        services.AddSingleton<BackgroundWorkMetrics>();
        _provider = services.BuildServiceProvider();
        return _provider.GetRequiredService<BackgroundWorkMetrics>();
    }

    private sealed class FakeInsight(string id, BackgroundWorkMetricsTests owner, ICurrentUserAccessor user) : IBackgroundJobInsight
    {
        public string Id => id;

        public async Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct)
        {
            owner._readsAs.Add(user.UserId.ToString());
            if (owner._gate is { } gate)
                await gate.Task;
            if (owner._broken.Contains(id))
                throw new InvalidOperationException("boom");
            return owner._rows.Single(r => r.Id == id);
        }

        public Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private static BackgroundJobSummaryDto Row(
        string id, string state, double value, double? of = null, DateTime? lastWorked = null, DateTime? eta = null, double today = 3)
    {
        var trend = new double[14];
        trend[13] = today;
        return new BackgroundJobSummaryDto(
            id, id, "", state, "label with free text", lastWorked, null,
            new BackgroundHeadlineDto(value, of, BackgroundFormat.Count, "caption", eta, null), trend, "per day");
    }

    private void SeedRows()
    {
        foreach (var id in BackgroundJobIds.All)
            _rows.Add(Row(id, BackgroundJobState.Idle, 1));
        _rows[0] = Row(BackgroundJobIds.All[0], BackgroundJobState.Failing, 10, of: 40,
            lastWorked: Today.AddHours(-2), eta: Today.AddDays(2), today: 7);
    }

    private sealed record Point(string Instrument, double Value, Dictionary<string, string?> Tags);

    private List<Point> Collect(BackgroundWorkMetrics _)
    {
        var points = new List<Point>();
        var factory = _provider!.GetRequiredService<IMeterFactory>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BackgroundWorkMetrics.MeterName && instrument.Meter.Scope == factory)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            points.Add(new Point(instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString()))));
        listener.Start();
        listener.RecordObservableInstruments();
        return points;
    }

    private async Task Refreshed(BackgroundWorkMetrics metrics)
    {
        Assert.NotNull(metrics.Refresh);
        await metrics.Refresh!;
    }

    [Fact]
    public async Task Nothing_is_reported_until_the_first_refresh_finishes_then_every_job_is()
    {
        SeedRows();
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metrics = Build();

        Assert.Empty(Collect(metrics));
        _gate.SetResult();
        await Refreshed(metrics);
        var points = Collect(metrics);

        var first = BackgroundJobIds.All[0];
        var states = points.Where(p => p.Instrument == "mh.background.job.state").ToList();
        Assert.Equal(BackgroundJobIds.All.Count * BackgroundJobState.All.Count, states.Count);
        var firstStates = states.Where(s => s.Tags["background_job"] == first).ToDictionary(s => s.Tags["state"]!, s => s.Value);
        Assert.Equal(1, firstStates[BackgroundJobState.Failing]);
        Assert.Equal(BackgroundJobState.All.Count - 1, firstStates.Values.Count(v => v == 0));

        double Of(string name) => points.Single(p => p.Instrument == name && p.Tags["background_job"] == first).Value;
        Assert.Equal(10, Of("mh.background.job.headline"));
        Assert.Equal(40, Of("mh.background.job.headline_of"));
        Assert.Equal(7, Of("mh.background.job.done_today"));
        Assert.Equal(new DateTimeOffset(Today.AddHours(-2)).ToUnixTimeSeconds(), Of("mh.background.job.last_worked_timestamp_seconds"));
        Assert.Equal(new DateTimeOffset(Today.AddDays(2)).ToUnixTimeSeconds(), Of("mh.background.job.eta_timestamp_seconds"));
    }

    [Fact]
    public async Task Optional_figures_are_left_out_for_jobs_that_have_none()
    {
        SeedRows();
        var metrics = Build();
        Collect(metrics);
        await Refreshed(metrics);

        var points = Collect(metrics);

        var second = BackgroundJobIds.All[1];
        Assert.Equal(BackgroundJobIds.All.Count, points.Count(p => p.Instrument == "mh.background.job.headline"));
        foreach (var name in new[] { "mh.background.job.headline_of", "mh.background.job.last_worked_timestamp_seconds", "mh.background.job.eta_timestamp_seconds" })
        {
            var series = points.Where(p => p.Instrument == name).ToList();
            Assert.Single(series);
            Assert.DoesNotContain(series, p => p.Tags["background_job"] == second);
        }

        Assert.All(points, p => Assert.All(p.Tags.Keys, k => Assert.Contains(k, new[] { "background_job", "state" })));
    }

    [Fact]
    public async Task A_fresh_snapshot_is_not_refreshed_again_until_it_is_five_minutes_old()
    {
        SeedRows();
        var metrics = Build();
        Collect(metrics);
        await Refreshed(metrics);
        var reads = _readsAs.Count;
        var firstRefresh = metrics.Refresh;

        _clock.Advance(TimeSpan.FromMinutes(4));
        Collect(metrics);
        Assert.Same(firstRefresh, metrics.Refresh);
        Assert.Equal(reads, _readsAs.Count);

        _rows[0] = Row(BackgroundJobIds.All[0], BackgroundJobState.Working, 11);
        _clock.Advance(TimeSpan.FromMinutes(2));
        Collect(metrics);
        await Refreshed(metrics);

        Assert.True(_readsAs.Count > reads);
        var fresh = Collect(metrics);
        Assert.Equal(1, fresh.Single(p => p.Instrument == "mh.background.job.state"
            && p.Tags["background_job"] == BackgroundJobIds.All[0] && p.Tags["state"] == BackgroundJobState.Working).Value);
    }

    [Fact]
    public async Task A_job_that_cannot_be_read_reports_unknown_and_no_placeholder_figures()
    {
        SeedRows();
        _broken.Add(BackgroundJobIds.CoverArt);
        var metrics = Build();
        Collect(metrics);
        await Refreshed(metrics);

        var points = Collect(metrics);

        var unknown = points.Single(p => p.Instrument == "mh.background.job.state"
            && p.Tags["background_job"] == BackgroundJobIds.CoverArt && p.Tags["state"] == BackgroundJobState.Unknown);
        Assert.Equal(1, unknown.Value);
        Assert.DoesNotContain(points, p => p.Instrument != "mh.background.job.state" && p.Tags["background_job"] == BackgroundJobIds.CoverArt);
        Assert.Equal(BackgroundJobIds.All.Count - 1, points.Count(p => p.Instrument == "mh.background.job.headline"));
    }

    [Fact]
    public async Task A_refresh_in_flight_is_not_started_again()
    {
        SeedRows();
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metrics = Build();
        Collect(metrics);
        var inFlight = metrics.Refresh;

        _clock.Advance(TimeSpan.FromMinutes(10));
        Collect(metrics);

        Assert.Same(inFlight, metrics.Refresh);
        _gate.SetResult();
        await Refreshed(metrics);
        Assert.Equal(BackgroundJobIds.All.Count, _readsAs.Count);
    }

    [Fact]
    public async Task A_failed_refresh_is_retried_once_five_minutes_have_passed_not_on_every_collection()
    {
        SeedRows();
        _insightsUnresolvable = true;
        var metrics = Build();
        Collect(metrics);
        await Refreshed(metrics);
        var failed = metrics.Refresh;

        Assert.Empty(Collect(metrics));
        Assert.Same(failed, metrics.Refresh);

        _insightsUnresolvable = false;
        _clock.Advance(TimeSpan.FromMinutes(6));
        Collect(metrics);
        Assert.NotSame(failed, metrics.Refresh);
        await Refreshed(metrics);
        Assert.NotEmpty(Collect(metrics));
    }

    [Fact]
    public async Task The_refresh_reads_as_the_library_owner()
    {
        SeedRows();
        var metrics = Build();
        Collect(metrics);
        await Refreshed(metrics);

        Assert.All(_readsAs, id => Assert.Equal(WellKnownUsers.OwnerId.ToString(), id));
    }

    [Fact]
    public async Task ActAs_gives_a_scope_without_a_request_the_owners_rows_and_only_theirs()
    {
        Build();
        var other = Guid.NewGuid();
        using (var scope = _provider!.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            db.Songs.Add(BackgroundWorkTestData.NewSong(1));
            db.Songs.Add(BackgroundWorkTestData.NewSong(2, s => s.OwnerUserId = other));
            await db.SaveChangesAsync();
        }

        using (var scope = _provider!.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            Assert.Equal(0, await db.Songs.CountAsync());
        }

        using (var scope = _provider!.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<HttpContextCurrentUserAccessor>()
                .ActAs(new CurrentUser(WellKnownUsers.OwnerId, "", UserRole.Admin, null));
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            var songs = await db.Songs.ToListAsync();
            Assert.Equal(WellKnownUsers.OwnerId, Assert.Single(songs).OwnerUserId);
        }
    }

    [Fact]
    public void A_request_user_wins_over_ActAs()
    {
        var requestUser = new CurrentUser(Guid.NewGuid(), "a@test.local", UserRole.Member, null);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Items[HttpContextCurrentUserAccessor.HttpContextItemKey] = requestUser;
        var accessor = new HttpContextCurrentUserAccessor(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http });

        accessor.ActAs(new CurrentUser(WellKnownUsers.OwnerId, "", UserRole.Admin, null));

        Assert.Equal(requestUser.Id, accessor.UserId);
    }

    [Fact]
    public void An_anonymous_request_is_not_given_the_ActAs_user()
    {
        var accessor = new HttpContextCurrentUserAccessor(
            new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() });

        accessor.ActAs(new CurrentUser(WellKnownUsers.OwnerId, "", UserRole.Admin, null));

        Assert.Null(accessor.User);
    }

    private sealed class MutableClock(DateTime now) : TimeProvider
    {
        private DateTimeOffset _now = new(now, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
