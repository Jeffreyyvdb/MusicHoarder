using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Enrichment;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// The transcribe endpoint promotes a re-sync of the song's OWN lyrics to the display/file default
/// (that is what "re-sync the timestamps" has to mean to be visible), but never promotes a
/// transcription the AI invented the words for — that must stay a side-by-side candidate.
/// </summary>
public class TranscribeLyricsEndpointTests : IDisposable
{
    private readonly DirectoryInfo _tmpDir = Directory.CreateTempSubdirectory("mh-transcribe-test-");

    public void Dispose()
    {
        try { _tmpDir.Delete(recursive: true); }
        catch { /* best-effort */ }
    }

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class FakeTranscriber(TranscriptionResult result) : ILyricsTranscriptionService
    {
        public bool IsConfigured => true;

        public Task<TranscriptionResult> TranscribeAsync(
            SongMetadata song, string audioFilePath, CancellationToken ct = default, Func<CancellationToken, Task>? beforeRetry = null)
            => Task.FromResult(result);

        public Task<IReadOnlyList<TimedWord>> TranscribeClipAsync(
            string audioFilePath, double startSeconds, double lengthSeconds, string? promptText = null, CancellationToken ct = default,
            Func<CancellationToken, Task>? beforeRetry = null)
            => Task.FromResult<IReadOnlyList<TimedWord>>(Array.Empty<TimedWord>());
    }

    private sealed class RefusingTranscriber(TimeSpan retryAfter) : ILyricsTranscriptionService
    {
        public bool IsConfigured => true;

        public Task<TranscriptionResult> TranscribeAsync(
            SongMetadata song, string audioFilePath, CancellationToken ct = default, Func<CancellationToken, Task>? beforeRetry = null)
            => throw new TranscriptionRateLimitedException("429 from the provider", retryAfter);

        public Task<IReadOnlyList<TimedWord>> TranscribeClipAsync(
            string audioFilePath, double startSeconds, double lengthSeconds, string? promptText = null, CancellationToken ct = default,
            Func<CancellationToken, Task>? beforeRetry = null)
            => throw new NotSupportedException();
    }

    /// <summary>A transcriber that does something else to the database while the audio is out.</summary>
    private sealed class MeanwhileTranscriber(Func<Task<TranscriptionResult>> transcribe) : ILyricsTranscriptionService
    {
        public bool IsConfigured => true;

        public Task<TranscriptionResult> TranscribeAsync(
            SongMetadata song, string audioFilePath, CancellationToken ct = default, Func<CancellationToken, Task>? beforeRetry = null)
            => transcribe();

        public Task<IReadOnlyList<TimedWord>> TranscribeClipAsync(
            string audioFilePath, double startSeconds, double lengthSeconds, string? promptText = null, CancellationToken ct = default,
            Func<CancellationToken, Task>? beforeRetry = null)
            => throw new NotSupportedException();
    }

    private static async Task Meanwhile(MusicHoarderDbContext db, int songId, Action<SongMetadata> change)
    {
        var options = (DbContextOptions<MusicHoarderDbContext>)db.GetService<IDbContextOptions>();
        await using var other = new MusicHoarderDbContext(options);
        change(await other.Songs.SingleAsync(s => s.Id == songId));
        await other.SaveChangesAsync();
    }

    [Fact]
    public async Task an_lrc_published_while_the_audio_was_out_is_not_hidden_behind_the_older_alignment()
    {
        await using var db = NewContext();
        var song = NewBuiltSongWithLyrics();
        song.SyncedLyrics = null;
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var transcriber = new MeanwhileTranscriber(async () =>
        {
            await Meanwhile(db, song.Id, s => s.SetOfficialLyricsText(
                "[00:01.50]official line one\n[00:05.50]official line two", s.PlainLyrics));
            return new TranscriptionResult(
                "[00:02.10]official line one\n[00:06.40]official line two",
                "official line one\nofficial line two",
                "whisper-1",
                AlignedToReference: true);
        });

        await SongsEndpoints.TranscribeLyrics(song.Id, db, transcriber, TestBudget.Over(db), CancellationToken.None);

        var saved = await db.Songs.AsNoTracking().FirstAsync(s => s.Id == song.Id);
        Assert.Equal("[00:01.50]official line one\n[00:05.50]official line two", saved.DisplaySyncedLyrics);
        Assert.Equal("[00:01.50]official line one\n[00:05.50]official line two", saved.EffectiveSyncedLyrics);
    }

    [Fact]
    public async Task a_failed_request_does_not_mark_failed_a_transcription_another_run_stored_meanwhile()
    {
        await using var db = NewContext();
        var song = NewBuiltSongWithLyrics();
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var transcriber = new MeanwhileTranscriber(async () =>
        {
            await Meanwhile(db, song.Id, s => s.ApplyTranscriptionResult("[00:02.00]official line one", "official line one", "whisper-1"));
            throw new HttpRequestException("provider error");
        });

        var response = await SongsEndpoints.TranscribeLyrics(song.Id, db, transcriber, TestBudget.Over(db), CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, (response as IStatusCodeHttpResult)?.StatusCode);
        var saved = await db.Songs.AsNoTracking().FirstAsync(s => s.Id == song.Id);
        Assert.Equal(TranscriptionStatus.Completed, saved.TranscriptionStatus);
    }

    /// <summary>A built song with LRCLIB lyrics and a real (stub) file the endpoint can resolve.</summary>
    private SongMetadata NewBuiltSongWithLyrics()
    {
        var path = Path.Combine(_tmpDir.FullName, $"{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, [0x00]);
        return new SongMetadata
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            SourcePath = path,
            FileName = Path.GetFileName(path),
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Fairuz",
            Title = "Li Beirut",
            SyncedLyrics = "[00:01.00]official line one\n[00:05.00]official line two",
            PlainLyrics = "official line one\nofficial line two",
            LyricsStatus = LyricsStatus.Fetched,
            LibraryBuildStatus = LibraryBuildStatus.Done,
            DestinationPath = Path.Combine(_tmpDir.FullName, "dest.mp3"),
        };
    }

    [Fact]
    public async Task resync_of_official_lyrics_becomes_the_default_and_requeues_the_retag()
    {
        await using var db = NewContext();
        var song = NewBuiltSongWithLyrics();
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber(new TranscriptionResult(
            "[00:02.10]official line one\n[00:06.40]official line two",
            "official line one\nofficial line two",
            "whisper-1",
            AlignedToReference: true));

        await SongsEndpoints.TranscribeLyrics(song.Id, db, transcriber, TestBudget.Over(db), CancellationToken.None);

        var saved = await db.Songs.AsNoTracking().FirstAsync(s => s.Id == song.Id);
        Assert.Equal(PreferredLyricsSource.Transcribed, saved.PreferredLyricsSource);
        // The freshly-timed LRC is what the player shows AND what gets embedded on the next build.
        Assert.Equal("[00:02.10]official line one\n[00:06.40]official line two", saved.DisplaySyncedLyrics);
        Assert.Equal("[00:02.10]official line one\n[00:06.40]official line two", saved.EffectiveSyncedLyrics);
        Assert.Equal(LibraryBuildStatus.Pending, saved.LibraryBuildStatus);
        Assert.Equal(saved.DestinationPath, saved.PreviousDestinationPath);
    }

    [Fact]
    public async Task transcription_without_reference_lyrics_never_replaces_the_curated_version()
    {
        await using var db = NewContext();
        var song = NewBuiltSongWithLyrics();
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        // No official text was available, so these are the model's own (possibly misheard) words.
        var transcriber = new FakeTranscriber(new TranscriptionResult(
            "[00:02.10]officious lion won",
            "officious lion won",
            "whisper-1",
            AlignedToReference: false));

        await SongsEndpoints.TranscribeLyrics(song.Id, db, transcriber, TestBudget.Over(db), CancellationToken.None);

        var saved = await db.Songs.AsNoTracking().FirstAsync(s => s.Id == song.Id);
        Assert.Equal(PreferredLyricsSource.Lrclib, saved.PreferredLyricsSource);
        Assert.Equal(song.SyncedLyrics, saved.DisplaySyncedLyrics);
        // Nothing the file embeds changed, so the built destination is left alone.
        Assert.Equal(LibraryBuildStatus.Done, saved.LibraryBuildStatus);
        // ...but the candidate is stored for the compare view.
        Assert.Equal("[00:02.10]officious lion won", saved.TranscribedSyncedLyrics);
    }

    [Fact]
    public async Task promotion_is_skipped_when_the_user_already_chose_the_transcription()
    {
        await using var db = NewContext();
        var song = NewBuiltSongWithLyrics();
        song.PreferredLyricsSource = PreferredLyricsSource.Transcribed;
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber(new TranscriptionResult(
            "[00:02.10]official line one", "official line one", "whisper-1", AlignedToReference: true));

        await SongsEndpoints.TranscribeLyrics(song.Id, db, transcriber, TestBudget.Over(db), CancellationToken.None);

        var saved = await db.Songs.AsNoTracking().FirstAsync(s => s.Id == song.Id);
        Assert.Equal(PreferredLyricsSource.Transcribed, saved.PreferredLyricsSource);
        // Already the default — the re-tag still fires because the embedded lyrics changed.
        Assert.Equal(LibraryBuildStatus.Pending, saved.LibraryBuildStatus);
    }

    [Fact]
    public async Task a_refused_transcription_says_when_to_retry_and_is_not_recorded_as_failed()
    {
        await using var db = NewContext();
        var song = NewBuiltSongWithLyrics();
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await SongsEndpoints.TranscribeLyrics(
            song.Id, db, new RefusingTranscriber(TimeSpan.FromMinutes(8)), TestBudget.Over(db), CancellationToken.None);

        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var body = JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(
            "The transcription quota is used up for now. Try again in about 8 minutes.",
            body.GetProperty("message").GetString());
        Assert.Equal(480, body.GetProperty("retryAfterSeconds").GetInt32());

        var saved = await db.Songs.AsNoTracking().FirstAsync(s => s.Id == song.Id);
        Assert.Equal(TranscriptionStatus.NotRequested, saved.TranscriptionStatus);
        Assert.Empty(await db.TranscriptionAudioSpends.ToListAsync());
    }
}
