using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace MusicHoarder.Api.Audio;

/// <summary>What a stream request asked for: the file as it is, or decoded to WAV.</summary>
public enum StreamFormat
{
    Original,
    Wav,
}

public static class StreamFormats
{
    /// <summary>
    /// Parses the stream endpoints' <c>?format=</c> value. Absent means the original; anything other
    /// than a known format is rejected rather than silently served as the original, so a client asking
    /// for something this server cannot produce finds out.
    /// </summary>
    public static bool TryParse(string? value, out StreamFormat format)
    {
        format = StreamFormat.Original;
        if (string.IsNullOrEmpty(value))
            return true;
        if (string.Equals(value, "wav", StringComparison.OrdinalIgnoreCase))
        {
            format = StreamFormat.Wav;
            return true;
        }
        return false;
    }
}

/// <summary>
/// A song decoded to WAV (PCM, 48 kHz, stereo, 16-bit) while it streams, for a client that cannot play
/// the file as it is (Ogg Opus in Safari). Only a client that needs it asks (<c>?format=wav</c>);
/// everyone else gets the original bytes.
///
/// <para>
/// PCM is what makes streaming during the conversion possible. Safari will only play media from a
/// server that answers byte-range requests, and a compressed conversion has no length until it is
/// finished. A PCM stream's length is known before the first byte (header plus frames × 4), and any
/// byte maps straight to a sample, so every range, including a seek far ahead, is answered by decoding
/// from that sample (<see cref="FfmpegPcmDecoder"/>). Playback starts in about a tenth of a second,
/// nothing is cached, and the audio is exactly what the original decodes to: no second lossy step. The
/// cost is bandwidth, about 1.5 Mbps.
/// </para>
/// </summary>
internal sealed class WavStreamResult(string sourcePath, long frames, IPcmDecoder decoder) : IResult
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BlockAlign = Channels * 2;
    public const int HeaderLength = 44;

    /// <summary>
    /// How far the decoder may fall short of the declared length before it counts as a failure rather
    /// than the container's length being a little generous: one second, padded with silence.
    /// </summary>
    internal const long MaxPaddingBytes = SampleRate * BlockAlign;

    /// <summary>Longest song a WAV header can describe (its sizes are 32-bit): a little over six hours.</summary>
    public static bool Fits(long frames) => HeaderLength - 8 + frames * BlockAlign <= uint.MaxValue;

    public long Length => HeaderLength + frames * BlockAlign;

    public async Task ExecuteAsync(HttpContext http)
    {
        var response = http.Response;
        var ct = http.RequestAborted;
        var etag = ETagFor(sourcePath);
        var headers = http.Request.Headers;
        var range = ResolveRange(headers.Range, headers.IfRange, etag, Length);

        response.ContentType = "audio/wav";
        response.Headers.AcceptRanges = "bytes";
        response.Headers.ETag = etag;
        if (range is null)
        {
            response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            response.Headers.ContentRange = $"bytes */{Length}";
            return;
        }

        var (start, end, partial) = range.Value;
        var position = start;

        // Start the decoder before committing to a status, so ffmpeg missing is a clean 500 and a
        // server at its decode limit a clean 503.
        Stream? pcm = null;
        long skip = 0;
        if (end >= HeaderLength)
        {
            var dataOffset = Math.Max(start, HeaderLength) - HeaderLength;
            try
            {
                pcm = decoder.Open(sourcePath, dataOffset / BlockAlign);
            }
            catch (InvalidOperationException)
            {
                response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }
            if (pcm is null)
            {
                response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                response.Headers.RetryAfter = "2";
                return;
            }
            skip = dataOffset % BlockAlign;
        }

        await using (pcm)
        {
            response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
            if (partial)
                response.Headers.ContentRange = $"bytes {start}-{end}/{Length}";
            response.ContentLength = end - start + 1;
            await response.StartAsync(ct);

            try
            {
                if (position < HeaderLength)
                {
                    var header = Header(frames);
                    var headerEnd = (int)Math.Min(end, HeaderLength - 1);
                    await response.Body.WriteAsync(header.AsMemory((int)position, headerEnd - (int)position + 1), ct);
                    position = headerEnd + 1;
                }
                if (pcm is not null)
                    await CopyDataAsync(pcm, skip, response.Body, end - position + 1, http, ct);
            }
            catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or IOException)
            {
                // The listener skipped or seeked; disposing the decoder stops ffmpeg.
            }
        }
    }

    /// <summary>
    /// Copies exactly <paramref name="count"/> bytes of PCM. A decoder that ends a little early (the
    /// container's length was generous) is padded with silence; one that ends far short failed, and
    /// the connection is aborted so the player reports it instead of playing silence.
    /// </summary>
    private static async Task CopyDataAsync(Stream pcm, long skip, Stream body, long count, HttpContext http, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (skip > 0)
        {
            var read = await pcm.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, skip)), ct);
            if (read == 0)
                break;
            skip -= read;
        }

        var remaining = count;
        while (remaining > 0)
        {
            var read = await pcm.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
                break;
            await body.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }

        if (remaining > MaxPaddingBytes)
        {
            http.Abort();
            return;
        }
        Array.Clear(buffer);
        while (remaining > 0)
        {
            var n = (int)Math.Min(buffer.Length, remaining);
            await body.WriteAsync(buffer.AsMemory(0, n), ct);
            remaining -= n;
        }
    }

    /// <summary>
    /// The byte range to serve: the whole body when there is no usable Range header (absent, not
    /// bytes, several ranges, or an If-Range for another version of the file), null when the range
    /// lies past the end.
    /// </summary>
    internal static (long Start, long End, bool Partial)? ResolveRange(string? rangeHeader, string? ifRange, string etag, long length)
    {
        var whole = (0L, length - 1, false);
        if (string.IsNullOrEmpty(rangeHeader))
            return whole;
        if (!string.IsNullOrEmpty(ifRange) && ifRange != etag)
            return whole;
        if (!RangeHeaderValue.TryParse(rangeHeader, out var parsed)
            || !string.Equals(parsed.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase)
            || parsed.Ranges.Count != 1)
            return whole;

        var item = parsed.Ranges.First();
        if (item.From is null)
        {
            // A suffix: the last N bytes.
            if (item.To is null or 0)
                return null;
            return (Math.Max(0, length - item.To.Value), length - 1, true);
        }
        if (item.From.Value >= length)
            return null;
        return (item.From.Value, Math.Min(item.To ?? length - 1, length - 1), true);
    }

    /// <summary>A canonical 44-byte WAV (RIFF, PCM) header for <paramref name="frames"/> frames.</summary>
    internal static byte[] Header(long frames)
    {
        var dataLength = (uint)(frames * BlockAlign);
        var header = new byte[HeaderLength];
        var span = header.AsSpan();
        Encoding.ASCII.GetBytes("RIFF", span[0..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], 36 + dataLength);
        Encoding.ASCII.GetBytes("WAVE", span[8..12]);
        Encoding.ASCII.GetBytes("fmt ", span[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..22], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..24], Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..32], SampleRate * BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..34], BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..36], 16);
        Encoding.ASCII.GetBytes("data", span[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..44], dataLength);
        return header;
    }

    /// <summary>
    /// Identifies this version of the file, so a player resuming a download after the file was
    /// re-tagged (If-Range) starts over rather than splicing two versions together.
    /// </summary>
    internal static string ETagFor(string path)
    {
        var info = new FileInfo(path);
        var identity = string.Join('|', path, info.Length, info.LastWriteTimeUtc.Ticks, "wav-v1");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $"\"{hash[..16]}\"";
    }
}
