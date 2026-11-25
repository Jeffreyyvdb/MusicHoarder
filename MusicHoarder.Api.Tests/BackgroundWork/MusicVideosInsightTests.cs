using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class MusicVideosInsightTests
{
    [Fact]
    public async Task The_headline_is_the_ready_videos_and_the_breakdown_tells_the_states_apart()
    {
        using var h = new BackgroundWorkHarness();
        h.Seed(db =>
        {
            var songs = Enumerable.Range(1, 4).Select(n => BackgroundWorkTestData.NewSong(n)).ToList();
            db.Songs.AddRange(songs);
            db.SaveChanges();
            db.SongMusicVideos.AddRange(
                new SongMusicVideo { SongId = songs[0].Id, Status = MusicVideoStatus.Ready, FetchedAtUtc = h.Now.AddHours(-2) },
                new SongMusicVideo { SongId = songs[1].Id, Status = MusicVideoStatus.Ready, FetchedAtUtc = h.Now.AddDays(-1) },
                new SongMusicVideo { SongId = songs[2].Id, Status = MusicVideoStatus.Fetching, FetchedAtUtc = h.Now },
                new SongMusicVideo { SongId = songs[3].Id, Status = MusicVideoStatus.Failed, FetchedAtUtc = h.Now });
        });

        var detail = await h.DescribeAsync(BackgroundJobIds.MusicVideos);

        Assert.Equal(2, detail.Summary.Headline.Value);
        Assert.Equal([1.0, 1], detail.Summary.Trend.TakeLast(2).Reverse());
        var panel = detail.Panel<BreakdownPanelDto>("Music videos by state");
        Assert.Equal(4, panel.Total);
        Assert.Equal(1, panel.Segment("Fetching"));
        Assert.Equal(1, panel.Segment("Failed"));
    }

    [Fact]
    public async Task Until_the_prefetch_service_reports_the_state_is_unknown()
    {
        using var h = new BackgroundWorkHarness();

        Assert.Equal(BackgroundJobState.Unknown, (await h.SummarizeAsync(BackgroundJobIds.MusicVideos)).State);
    }
}
