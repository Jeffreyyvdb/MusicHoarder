using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.CleanEdits;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.CleanEdits;

public class CleanEditEndpointsTests
{
    private readonly DbContextOptions<MusicHoarderDbContext> _options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options;

    private async Task AddAsync(
        string artist, string title, CleanEditVerdict? verdict, bool deleted = false, string? evidence = null, double? margin = null)
    {
        await using var db = new MusicHoarderDbContext(_options);
        var song = new SongMetadata
        {
            OwnerUserId = TestUsers.OwnerId,
            SourcePath = $"/src/{title}.flac",
            FileName = title + ".flac",
            Extension = ".flac",
            FileSizeBytes = 1,
            Fingerprint = "fp",
            Artist = artist,
            Title = title,
            Album = "Album",
            DestinationPath = $"/dst/{title}.flac",
            DeletedAtUtc = deleted ? DateTime.UtcNow : null,
            LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        if (verdict is not { } v)
            return;
        db.CleanEditChecks.Add(new CleanEditCheck
        {
            SongId = song.Id,
            OwnerUserId = TestUsers.OwnerId,
            Verdict = v,
            Margin = margin,
            EvidenceJson = evidence,
            FileStamp = "stamp",
            CheckedAtUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
    }

    private async Task<CleanEditEndpoints.CleanEditListDto> ListAsync(int? limit = null, int? offset = null)
    {
        await using var db = new MusicHoarderDbContext(_options);
        var result = await CleanEditEndpoints.List(db, limit, offset, CancellationToken.None);
        return Assert.IsType<Ok<CleanEditEndpoints.CleanEditListDto>>(result).Value!;
    }

    [Fact]
    public async Task It_lists_the_clean_and_muted_songs_with_their_evidence_and_counts_every_verdict()
    {
        var evidence = new CleanEditEvidence(
            [new ReferenceUsed(Edition.Explicit, "deezer:1", 10, 0.97), new ReferenceUsed(Edition.Clean, "itunes:2", 10, 0.95)],
            "deezer:1~itunes:2",
            [53, 64.3]).ToJson();
        await AddAsync("Kid Cudi", "Make Her Say", CleanEditVerdict.MutedWords, evidence: evidence, margin: 0.52);
        await AddAsync("Kanye West", "Stronger", CleanEditVerdict.CleanMaster);
        await AddAsync("Kanye West", "Flashing Lights", CleanEditVerdict.Explicit);
        await AddAsync("Kanye West", "Runaway", CleanEditVerdict.Failed);
        await AddAsync("Kanye West", "Gold Digger", null);
        await AddAsync("Gone", "Deleted", CleanEditVerdict.CleanMaster, deleted: true);

        var list = await ListAsync();

        Assert.Equal(["Stronger", "Make Her Say"], list.Items.Select(i => i.Title).ToArray());
        var muted = list.Items.Single(i => i.Verdict == "mutedWords");
        Assert.Equal(0.52, muted.Margin);
        Assert.Equal(new[] { 53.0, 64.3 }, muted.MutedAtSeconds);
        Assert.Equal(["deezer:1", "itunes:2"], muted.References.Select(r => r.Id).ToArray());
        Assert.Equal(2, list.FoundTotal);
        Assert.Equal(5, list.Eligible);
        Assert.Equal(1, list.Counts["explicit"]);
        Assert.Equal(1, list.Counts["failed"]);
        Assert.Equal(1, list.Counts["cleanMaster"]);
        Assert.Equal(0, list.Counts["noReference"]);
    }

    [Fact]
    public async Task A_long_list_is_paged()
    {
        for (var i = 0; i < 5; i++)
            await AddAsync("Artist", $"Song {i}", CleanEditVerdict.CleanMaster);

        var page = await ListAsync(limit: 2, offset: 2);

        Assert.Equal(5, page.FoundTotal);
        Assert.Equal(["Song 2", "Song 3"], page.Items.Select(i => i.Title).ToArray());
    }
}
