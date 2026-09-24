using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Audio;

/// <summary>
/// The WAV stream behind <c>?format=wav</c>: a PCM body whose length is known before decoding starts,
/// so it can answer the byte ranges Safari insists on. ffmpeg is faked except in the
/// <see cref="FfmpegFactAttribute"/> tests at the bottom, which pin the two things the design rests
/// on: the length read up front matches the decode, and a range decoded from part-way through is
/// bit-identical to the same bytes of a decode from the start.
/// </summary>
public sealed class WavStreamResultTests : IDisposable
{
    private const int Header = WavStreamResult.HeaderLength;
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("mh-wav-");

    public void Dispose()
    {
        try { root.Delete(recursive: true); }
        catch (IOException) { }
    }

    private string Source()
    {
        var path = Path.Combine(root.FullName, "song.opus");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private sealed class AbortRecorder : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    private sealed record Served(int Status, IHeaderDictionary Headers, byte[] Body, bool Aborted);

    private static async Task<Served> Serve(WavStreamResult result, string? range = null, string? ifRange = null)
    {
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;
        var lifetime = new AbortRecorder();
        http.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        if (range is not null)
            http.Request.Headers.Range = range;
        if (ifRange is not null)
            http.Request.Headers.IfRange = ifRange;

        await result.ExecuteAsync(http);
        return new Served(http.Response.StatusCode, http.Response.Headers, body.ToArray(), lifetime.Aborted);
    }

    private static byte[] Pattern(long fromDataOffset, int count) =>
        Enumerable.Range(0, count).Select(i => FakePcmDecoder.PatternAt(fromDataOffset + i)).ToArray();

    [Fact]
    public async Task Without_a_range_it_serves_the_header_and_every_frame()
    {
        var decoder = new FakePcmDecoder(frames: 1_000);
        var served = await Serve(new WavStreamResult(Source(), 1_000, decoder));

        Assert.Equal(200, served.Status);
        Assert.Equal("audio/wav", served.Headers.ContentType);
        Assert.Equal("bytes", served.Headers.AcceptRanges);
        Assert.Equal(Header + 4_000, served.Body.Length);
        Assert.Equal(WavStreamResult.Header(1_000), served.Body[..Header]);
        Assert.Equal(Pattern(0, 4_000), served.Body[Header..]);
    }

    [Fact]
    public async Task A_header_probe_is_answered_without_starting_the_decoder()
    {
        // Safari opens with `bytes=0-1` before anything else.
        var decoder = new FakePcmDecoder();
        var served = await Serve(new WavStreamResult(Source(), 1_000, decoder), "bytes=0-1");

        Assert.Equal(206, served.Status);
        Assert.Equal($"bytes 0-1/{Header + 4_000}", served.Headers.ContentRange);
        Assert.Equal("RI"u8.ToArray(), served.Body);
        Assert.Empty(decoder.Opened);
    }

    [Fact]
    public async Task A_range_across_the_header_joins_it_to_the_first_samples()
    {
        var decoder = new FakePcmDecoder();
        var served = await Serve(new WavStreamResult(Source(), 1_000, decoder), "bytes=40-51");

        Assert.Equal([.. WavStreamResult.Header(1_000)[40..44], .. Pattern(0, 8)], served.Body);
        Assert.Equal(0, decoder.Opened.Single().StartFrame);
    }

    [Fact]
    public async Task A_range_part_way_through_decodes_from_its_frame_and_starts_on_its_byte()
    {
        // Byte 1003 is data offset 959: frame 239, three bytes in.
        var decoder = new FakePcmDecoder();
        var served = await Serve(new WavStreamResult(Source(), 1_000, decoder), "bytes=1003-1010");

        Assert.Equal(206, served.Status);
        Assert.Equal(Pattern(959, 8), served.Body);
        Assert.Equal(239, decoder.Opened.Single().StartFrame);
    }

    [Fact]
    public async Task Open_ended_and_suffix_ranges_run_to_the_end()
    {
        var length = Header + 4_000;
        var openEnded = await Serve(new WavStreamResult(Source(), 1_000, new FakePcmDecoder()), "bytes=3000-");
        var suffix = await Serve(new WavStreamResult(Source(), 1_000, new FakePcmDecoder()), "bytes=-8");

        Assert.Equal($"bytes 3000-{length - 1}/{length}", openEnded.Headers.ContentRange);
        Assert.Equal(Pattern(3000 - Header, length - 3000), openEnded.Body);
        Assert.Equal(Pattern(4_000 - 8, 8), suffix.Body);
    }

    [Fact]
    public async Task A_range_past_the_end_is_416()
    {
        var served = await Serve(new WavStreamResult(Source(), 1_000, new FakePcmDecoder()), "bytes=999999-");

        Assert.Equal(416, served.Status);
        Assert.Equal($"bytes */{Header + 4_000}", served.Headers.ContentRange);
    }

    [Fact]
    public async Task A_resumed_download_of_another_version_of_the_file_gets_the_whole_body()
    {
        var served = await Serve(new WavStreamResult(Source(), 1_000, new FakePcmDecoder()), "bytes=100-", "\"stale\"");

        Assert.Equal(200, served.Status);
        Assert.Equal(Header + 4_000, served.Body.Length);
    }

    [Fact]
    public async Task A_decoder_a_little_short_is_padded_with_silence_and_a_long_one_is_cut()
    {
        // The container's length and the decode can disagree by a few frames; the body is always the
        // length the header promised.
        var shortDecode = await Serve(new WavStreamResult(Source(), 1_000, new FakePcmDecoder(frames: 1_000, producedFrames: 990)));
        var longDecode = await Serve(new WavStreamResult(Source(), 1_000, new FakePcmDecoder(frames: 1_000, producedFrames: 1_010)));

        Assert.Equal(Header + 4_000, shortDecode.Body.Length);
        Assert.All(shortDecode.Body[(Header + 3_960)..], b => Assert.Equal(0, b));
        Assert.Equal(Header + 4_000, longDecode.Body.Length);
        Assert.False(shortDecode.Aborted);
    }

    [Fact]
    public async Task A_decoder_that_fails_far_short_aborts_rather_than_playing_silence()
    {
        var frames = 5 * WavStreamResult.SampleRate;
        var served = await Serve(new WavStreamResult(Source(), frames, new FakePcmDecoder(frames: frames, producedFrames: 100)));

        Assert.True(served.Aborted);
        Assert.True(served.Body.Length < Header + frames * 4L);
    }

    [Fact]
    public async Task At_the_decode_limit_a_range_that_needs_decoding_is_503_but_the_header_probe_still_works()
    {
        var busy = new FakePcmDecoder(busy: true);

        var probe = await Serve(new WavStreamResult(Source(), 1_000, busy), "bytes=0-1");
        var data = await Serve(new WavStreamResult(Source(), 1_000, busy), "bytes=0-");

        Assert.Equal(206, probe.Status);
        Assert.Equal(503, data.Status);
        Assert.Empty(data.Body);
    }

    [Fact]
    public void The_header_describes_48_kHz_stereo_16_bit_pcm()
    {
        var header = WavStreamResult.Header(48_000);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal(36u + 192_000, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(header, 8, 8));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22)));
        Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)));
        Assert.Equal(192_000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)));
        Assert.Equal("data", Encoding.ASCII.GetString(header, 36, 4));
        Assert.Equal(192_000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)));
    }

    [Theory]
    [InlineData("bytes=0-1,5-9")] // several ranges: the whole body rather than multipart
    [InlineData("items=0-1")]
    [InlineData("garbage")]
    public void An_unusable_range_header_serves_the_whole_body(string header)
    {
        Assert.Equal((0L, 99L, false), WavStreamResult.ResolveRange(header, null, "\"x\"", 100));
    }

    [Fact]
    public void Only_songs_a_wav_header_can_describe_are_converted()
    {
        Assert.True(WavStreamResult.Fits(6L * 3600 * WavStreamResult.SampleRate));
        Assert.False(WavStreamResult.Fits(7L * 3600 * WavStreamResult.SampleRate));
    }

    [Fact]
    public void The_length_drops_the_encoders_pre_skip_and_falls_back_to_the_containers_duration()
    {
        // ffprobe on an Ogg Opus file: 240.0065 s including 312 samples of pre-skip, which the
        // decoder drops.
        const string opus = """{"streams":[{"sample_rate":"48000","initial_padding":312,"duration":"240.006500"}],"format":{"duration":"240.006500"}}""";
        const string streamWithoutDuration = """{"streams":[{"sample_rate":"44100","initial_padding":0}],"format":{"duration":"7.300000"}}""";

        Assert.Equal(11_520_000, FfmpegPcmDecoder.FramesFromProbe(opus));
        Assert.Equal(350_400, FfmpegPcmDecoder.FramesFromProbe(streamWithoutDuration));
        Assert.Null(FfmpegPcmDecoder.FramesFromProbe("""{"streams":[],"format":{}}"""));
        Assert.Null(FfmpegPcmDecoder.FramesFromProbe("not json"));
    }

    [Fact]
    public void A_range_part_way_through_starts_decoding_a_second_early()
    {
        var args = FfmpegPcmDecoder.Arguments("/music/a.opus", 254_321 - FfmpegPcmDecoder.PrerollFrames);

        Assert.Equal("4.298354", args[args.ToList().IndexOf("-ss") + 1]);
        Assert.DoesNotContain("-ss", FfmpegPcmDecoder.Arguments("/music/a.opus", 0));
        Assert.Equal(["-ac", "2", "-ar", "48000", "-f", "s16le", "-"], args.TakeLast(7));
    }

    // ── ffmpeg itself ─────────────────────────────────────────────────────────

    private static FfmpegPcmDecoder RealDecoder(int concurrency = 16) =>
        new(Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { StreamDecodeConcurrency = concurrency }),
            NullLogger<FfmpegPcmDecoder>.Instance);

    /// <summary>Ten seconds of Opus with content that changes every sample, made by ffmpeg.</summary>
    private string MusicLikeOpus()
    {
        var path = Path.Combine(root.FullName, "tone.opus");
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[]
                 {
                     "-nostdin", "-v", "error", "-y",
                     "-f", "lavfi", "-i", "sine=frequency=331:duration=10",
                     "-f", "lavfi", "-i", "anoisesrc=d=10:a=0.05:c=pink:seed=7",
                     "-filter_complex", "[0][1]amix=2,aformat=channel_layouts=stereo",
                     "-c:a", "libopus", "-b:a", "128k", path,
                 })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    private static async Task<byte[]> ReadAll(Stream? stream)
    {
        Assert.NotNull(stream);
        await using (stream)
        {
            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return buffer.ToArray();
        }
    }

    [FfmpegFact]
    public async Task The_length_read_up_front_is_the_length_ffmpeg_decodes()
    {
        var path = MusicLikeOpus();
        var decoder = RealDecoder();

        var frames = await decoder.CountFramesAsync(path, CancellationToken.None);
        var decoded = await ReadAll(decoder.Open(path, 0));

        Assert.Equal(decoded.Length / WavStreamResult.BlockAlign, frames);
    }

    [FfmpegFact]
    public async Task A_range_decoded_from_part_way_through_is_bit_identical_to_a_decode_from_the_start()
    {
        // What makes independent range requests join without a click.
        var path = MusicLikeOpus();
        var decoder = RealDecoder();
        var full = await ReadAll(decoder.Open(path, 0));

        foreach (var startFrame in new long[] { 30_000, 254_321, 400_000 })
        {
            var part = await ReadAll(decoder.Open(path, startFrame));
            var offset = (int)(startFrame * WavStreamResult.BlockAlign);
            Assert.Equal(full.Length - offset, part.Length);
            Assert.True(full.AsSpan(offset).SequenceEqual(part), $"differs from frame {startFrame}");
        }
    }

    [FfmpegFact]
    public async Task Decodes_beyond_the_limit_are_refused_until_one_finishes()
    {
        var path = MusicLikeOpus();
        var decoder = RealDecoder(concurrency: 1);

        var first = decoder.Open(path, 0);
        Assert.NotNull(first);
        Assert.Null(decoder.Open(path, 0));

        await first.DisposeAsync();
        await using var again = decoder.Open(path, 0);
        Assert.NotNull(again);
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
