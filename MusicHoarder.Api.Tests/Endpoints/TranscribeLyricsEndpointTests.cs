using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

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
            SongMetadata song, string audioFilePath, CancellationToken ct = default)
            => Task.FromResult(result);

        public Task<IReadOnlyList<TimedWord>> TranscribeClipAsync(
            string audioFilePath, double startSeconds, double lengthSeconds, string? promptText = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimedWord>>(Array.Empty<TimedWord>());
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

        await SongsEndpoints.TranscribeLyrics(song.Id, db, transcriber, CancellationToken.None);

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

        await SongsEndpoints.TranscribeLyrics(song.Id, db, transcriber, CancellationToken.None);

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

        await SongsEndpoints.TranscribeLyrics(song.Id, db, transcriber, CancellationToken.None);

        var saved = await db.Songs.AsNoTracking().FirstAsync(s => s.Id == song.Id);
        Assert.Equal(PreferredLyricsSource.Transcribed, saved.PreferredLyricsSource);
        // Already the default — the re-tag still fires because the embedded lyrics changed.
        Assert.Equal(LibraryBuildStatus.Pending, saved.LibraryBuildStatus);
    }
}
