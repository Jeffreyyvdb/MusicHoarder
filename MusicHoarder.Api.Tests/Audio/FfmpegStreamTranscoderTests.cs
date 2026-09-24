using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Audio;

/// <summary>
/// The AAC rendition cache behind <c>?format=aac</c>. ffmpeg is replaced by a delegate everywhere
/// except <see cref="Converts_a_real_opus_file_to_aac"/>, so these pin the cache and the sharing of
/// conversions, not the encoder.
/// </summary>
public sealed class FfmpegStreamTranscoderTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("mh-transcode-");

    public void Dispose()
    {
        try { root.Delete(recursive: true); }
        catch (IOException) { }
    }

    private string CacheDir => Path.Combine(root.FullName, "cache");

    private string Source(string name = "song.opus", int bytes = 16)
    {
        var path = Path.Combine(root.FullName, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private FfmpegStreamTranscoder Transcoder(
        Func<string, string, CancellationToken, Task> encode,
        StreamTranscodeOptions? options = null) =>
        new(CacheDir, options ?? new StreamTranscodeOptions(), encode, NullLogger.Instance, CancellationToken.None);

    /// <summary>An encoder that writes <paramref name="bytes"/> bytes and counts its calls.</summary>
    private static Func<string, string, CancellationToken, Task> Writing(Action onCall, int bytes = 8) =>
        async (_, output, _) =>
        {
            onCall();
            await File.WriteAllBytesAsync(output, new byte[bytes]);
        };

    [Fact]
    public async Task Converts_once_and_serves_the_cached_rendition_after_that()
    {
        var calls = 0;
        var transcoder = Transcoder(Writing(() => calls++));
        var source = Source();

        var first = await transcoder.GetAacRenditionAsync(source, CancellationToken.None);
        var second = await transcoder.GetAacRenditionAsync(source, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.True(File.Exists(first));
        Assert.EndsWith(".m4a", first);
        Assert.StartsWith(CacheDir, first);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_changed_source_file_converts_again()
    {
        // The library builder re-tags destination files in place; a rendition of the old bytes
        // must not outlive them.
        var calls = 0;
        var transcoder = Transcoder(Writing(() => calls++));
        var source = Source(bytes: 16);

        var before = await transcoder.GetAacRenditionAsync(source, CancellationToken.None);
        File.WriteAllBytes(source, new byte[32]);
        var after = await transcoder.GetAacRenditionAsync(source, CancellationToken.None);

        Assert.NotEqual(before, after);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Requests_arriving_together_share_one_conversion()
    {
        // The player's pre-flight, the media element's request and its range probes all land at once.
        var calls = 0;
        var release = new TaskCompletionSource();
        var transcoder = Transcoder(async (_, output, _) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            await File.WriteAllBytesAsync(output, [1]);
        });
        var source = Source();

        var a = transcoder.GetAacRenditionAsync(source, CancellationToken.None);
        var b = transcoder.GetAacRenditionAsync(source, CancellationToken.None);
        release.SetResult();

        Assert.Equal(await a, await b);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task One_request_leaving_does_not_cancel_the_conversion_another_still_waits_for()
    {
        var release = new TaskCompletionSource();
        var encoderCancelled = false;
        var transcoder = Transcoder(async (_, output, ct) =>
        {
            await release.Task;
            encoderCancelled = ct.IsCancellationRequested;
            await File.WriteAllBytesAsync(output, [1]);
        });
        var source = Source();
        using var leaving = new CancellationTokenSource();

        var gone = transcoder.GetAacRenditionAsync(source, leaving.Token);
        var staying = transcoder.GetAacRenditionAsync(source, CancellationToken.None);
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gone);
        release.SetResult();

        Assert.True(File.Exists(await staying));
        Assert.False(encoderCancelled);
    }

    [Fact]
    public async Task The_last_request_leaving_cancels_the_conversion_and_a_new_one_starts_fresh()
    {
        // Skipping through a queue must not leave a backlog of conversions ahead of the song wanted.
        var calls = 0;
        var running = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        var transcoder = Transcoder(async (_, output, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await using (ct.Register(() => cancelled.TrySetResult()))
                {
                    running.SetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
            }
            await File.WriteAllBytesAsync(output, [1]);
        });
        var source = Source();
        using var skipped = new CancellationTokenSource();

        var first = transcoder.GetAacRenditionAsync(source, skipped.Token);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await skipped.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var again = await transcoder.GetAacRenditionAsync(source, CancellationToken.None);
        Assert.True(File.Exists(again));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_failed_conversion_leaves_nothing_behind_and_the_next_request_tries_again()
    {
        var calls = 0;
        var transcoder = Transcoder(async (_, output, _) =>
        {
            if (++calls == 1)
            {
                await File.WriteAllBytesAsync(output, [1]); // half-written, then ffmpeg dies
                throw new InvalidOperationException("ffmpeg exited with code 1");
            }
            await File.WriteAllBytesAsync(output, [1, 2]);
        });
        var source = Source();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transcoder.GetAacRenditionAsync(source, CancellationToken.None));
        Assert.Empty(Directory.Exists(CacheDir)
            ? Directory.EnumerateFiles(CacheDir, "*", SearchOption.AllDirectories)
            : []);

        var rendition = await transcoder.GetAacRenditionAsync(source, CancellationToken.None);
        Assert.Equal(2, new FileInfo(rendition).Length);
    }

    [Fact]
    public async Task A_conversion_past_the_deadline_fails_as_a_timeout()
    {
        var transcoder = Transcoder(
            (_, _, ct) => Task.Delay(Timeout.Infinite, ct),
            new StreamTranscodeOptions { TimeoutSeconds = 0 });

        await Assert.ThrowsAsync<TimeoutException>(
            () => transcoder.GetAacRenditionAsync(Source(), CancellationToken.None));
    }

    [Fact]
    public async Task The_cache_is_trimmed_least_recently_used_first_and_keeps_what_it_just_made()
    {
        const int megabyte = 1024 * 1024;
        var transcoder = Transcoder(
            Writing(() => { }, bytes: 600 * 1024),
            new StreamTranscodeOptions { CacheMaxMegabytes = 1 });

        var older = await transcoder.GetAacRenditionAsync(Source("older.opus"), CancellationToken.None);
        File.SetLastAccessTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        var crashLeftover = Path.Combine(Path.GetDirectoryName(older)!, "leftover.m4a.abc.tmp");
        File.WriteAllBytes(crashLeftover, [1]);
        File.SetLastWriteTimeUtc(crashLeftover, DateTime.UtcNow.AddHours(-2));

        var newest = await transcoder.GetAacRenditionAsync(Source("newest.opus"), CancellationToken.None);

        Assert.False(File.Exists(older));
        Assert.False(File.Exists(crashLeftover));
        Assert.True(File.Exists(newest));
        Assert.True(new FileInfo(newest).Length < megabyte);
    }

    [Fact]
    public void Ffmpeg_is_asked_for_the_first_audio_stream_as_aac_with_its_index_up_front()
    {
        var args = FfmpegStreamTranscoder.AacArguments("/in/song.opus", "/out/x.tmp", 256);

        Assert.Equal("/in/song.opus", args[args.ToList().IndexOf("-i") + 1]);
        Assert.Equal("/out/x.tmp", args[^1]);
        Assert.Equal("0:a:0", args[args.ToList().IndexOf("-map") + 1]);
        Assert.Equal("aac", args[args.ToList().IndexOf("-c:a") + 1]);
        Assert.Equal("256k", args[args.ToList().IndexOf("-b:a") + 1]);
        Assert.Equal("+faststart", args[args.ToList().IndexOf("-movflags") + 1]);
        Assert.Equal("mp4", args[args.ToList().IndexOf("-f") + 1]);
    }

    [FfmpegFact]
    public async Task Converts_a_real_opus_file_to_aac()
    {
        var source = Path.Combine(root.FullName, "silence.opus");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.opus"), source);
        var transcoder = Transcoder(FfmpegStreamTranscoder.FfmpegEncoder(null, 256));

        var rendition = await transcoder.GetAacRenditionAsync(source, CancellationToken.None);

        // An MP4 file opens with an `ftyp` box.
        var head = new byte[8];
        await using (var file = File.OpenRead(rendition))
            await file.ReadExactlyAsync(head);
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(head, 4, 4));
    }
}

/// <summary>A test that runs ffmpeg itself, reported as skipped where ffmpeg is not installed.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, "ffmpeg")) || File.Exists(Path.Combine(dir, "ffmpeg.exe")));
        if (!onPath)
            Skip = "ffmpeg is not on PATH";
    }
}
