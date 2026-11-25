using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Persistence;
using static MusicHoarder.Api.Tests.BackgroundWork.MusicVideoTestData;

namespace MusicHoarder.Api.Tests.BackgroundWork;

/// <summary>The two music-video insights' queries on a real server: the scope subqueries, the grouping and the memoised read of each list.</summary>
public class MusicVideoInsightsPostgresTests
{
    [PostgresFact]
    public async Task Both_sheets_read_the_library_through_the_providers_sql()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var now = BackgroundWorkTestData.Now;
        await using (var seed = database.NewContext())
        {
            var searched = now.AddDays(-2);
            var songs = Enumerable.Range(1, 8).Select(n => BackgroundWorkTestData.NewSong(n)).ToList();
            seed.Songs.AddRange(songs);
            seed.SaveChanges();
            seed.SongVideoCandidateSets.AddRange(
                Found(songs[0], searched, now.AddDays(5), Confident()),
                Found(songs[1], searched, null, Weak(60)),
                Nothing(songs[2], searched, 2, now.AddDays(2)),
                Set(songs[3], "[]", searched, now.AddDays(1), emptySearches: 1, error: "yt-dlp exited with 1"));
            seed.SongMusicVideos.AddRange(
                Video(songs[4], MusicVideoStatus.Ready, MusicVideoPick.Automatic, now.AddHours(-3)),
                Video(songs[5], MusicVideoStatus.Failed, MusicVideoPick.Automatic));
            seed.SaveChanges();
        }

        using var h = new BackgroundWorkHarness(now, () => database.NewContext());
        h.Disk.Available = 100L * 1024 * 1024 * 1024;

        var search = await h.DescribeAsync(BackgroundJobIds.MusicVideos);
        var downloads = await h.DescribeAsync(BackgroundJobIds.MusicVideoDownloads);

        var breakdown = search.Panel<BreakdownPanelDto>("What the last search found");
        Assert.Equal(8, breakdown.Total);
        Assert.Equal(1, breakdown.Segment("clip"));
        Assert.Equal(1, breakdown.Segment("no-clip"));
        Assert.Equal(2, breakdown.Segment("empty"));
        Assert.Equal(1, breakdown.Segment("video-automatic"));
        Assert.Equal(3, breakdown.Segment("never"));
        Assert.Equal(4, search.Summary.Headline.Value);
        Assert.Equal(7, search.Summary.Headline.Of);

        Assert.Equal(1, downloads.Summary.Headline.Value);
        Assert.Equal(1, downloads.Panel<FiguresPanelDto>("Downloads in numbers").Item("Confident picks waiting").Value);
        Assert.Equal(1, downloads.Panel<BreakdownPanelDto>("Where every video stands").Segment("failed"));
    }
}
