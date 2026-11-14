using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Audio;

/// <summary>
/// anonymous share-link <c>?format=wav</c> streams read slowly must not be able to take the
/// decode slots the owner's and members' wav playback needs. ffmpeg/ffprobe are stubbed by scripts
/// (ffmpeg writes zeros forever); the share streams are held open by a response body that never drains.
/// </summary>
public sealed class ShareStreamDecodePoolTests : IDisposable
{
    private const int Cap = 4;
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("share-decode-pool-");

    public void Dispose() => root.Delete(recursive: true);

    private FfmpegPcmDecoder StubDecoder()
    {
        void Script(string name, string body)
        {
            var path = Path.Combine(root.FullName, name);
            File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Script("ffmpeg", "exec cat /dev/zero");
        Script("ffprobe", "echo '{\"streams\":[{\"duration\":\"240.0\"}]}'");
        return new FfmpegPcmDecoder(
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                FfmpegPath = Path.Combine(root.FullName, "ffmpeg"),
                StreamDecodeConcurrency = Cap,
            }),
            NullLogger<FfmpegPcmDecoder>.Instance);
    }

    /// <summary>A response body that accepts the first write, then never drains: a slow reader.</summary>
    private sealed class StalledBody(TaskCompletionSource started) : Stream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Slow_share_streams_cannot_take_the_decode_slots_of_the_owners_wav_playback()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var audio = Path.Combine(root.FullName, "a.opus");
        await File.WriteAllBytesAsync(audio, [1, 2, 3]);
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        var song = new SongMetadata
        {
            Id = 1,
            OwnerUserId = TestUsers.OwnerId,
            SourcePath = audio,
            FileSizeBytes = 3,
            FileName = "a.opus",
            Extension = ".opus",
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = "Dummy",
        };
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(song);
            seed.SongShares.Add(new SongShare
            {
                Id = 1,
                OwnerUserId = TestUsers.OwnerId,
                SongId = 1,
                Scope = ShareScope.Song,
                Token = "tok",
                CreatedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var decoder = StubDecoder();
        using var abort = new CancellationTokenSource();
        var holders = new List<Task>();
        var started = new List<Task>();
        try
        {
            // An anonymous visitor opens far more wav streams than the server has slots, reading none.
            for (var i = 0; i < Cap * 2; i++)
            {
                var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var http = new DefaultHttpContext { RequestAborted = abort.Token };
                http.Response.Body = new StalledBody(firstWrite);
                await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(user: null));
                var result = await SharesEndpoints.StreamSharedSong("tok", 1, "wav", db, decoder, CancellationToken.None);
                holders.Add(Task.Run(async () =>
                {
                    try { await result.ExecuteAsync(http); }
                    catch (OperationCanceledException) { }
                }));
                started.Add(Task.WhenAny(firstWrite.Task, Task.Delay(TimeSpan.FromSeconds(10))));
            }
            await Task.WhenAll(started.Take(Cap));
            await Task.Delay(500);

            // The owner's wav playback (a small data range) must still get a decoder.
            var owner = await SongsEndpoints.StreamSongFileAsync(song, "wav", decoder, CancellationToken.None);
            var ownerHttp = new DefaultHttpContext();
            ownerHttp.Request.Headers.Range = "bytes=44-1067";
            ownerHttp.Response.Body = new MemoryStream();
            await owner.ExecuteAsync(ownerHttp);

            Assert.Equal(StatusCodes.Status206PartialContent, ownerHttp.Response.StatusCode);
        }
        finally
        {
            abort.Cancel();
            await Task.WhenAll(holders);
        }
    }
}
