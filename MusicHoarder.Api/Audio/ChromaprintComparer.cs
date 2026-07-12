using System.Numerics;

namespace MusicHoarder.Api.Audio;

/// <summary>
/// Compares two Chromaprint acoustic fingerprints for <em>similarity</em>, not equality. fpcalc emits
/// a compressed base64 fingerprint (what <c>SongMetadata.Fingerprint</c> stores); re-encoding the same
/// recording to a different codec/bitrate yields a fingerprint that is very close but not bit-identical,
/// so the rest of the app's exact-string comparisons can't tell "same recording, better file" from
/// "different track". This decodes the compressed form to its raw 32-bit sub-fingerprint frames and
/// scores them by bit-error-rate, which the quality-upgrade merge uses to confirm a downloaded file is
/// acoustically the same recording as the one it would replace.
/// <para>
/// The decode is the exact inverse of Chromaprint's <c>FingerprintCompressor</c>: a 4-byte header
/// (algorithm + 24-bit big-endian frame count), then a 3-bit "normal" stream of bit-gap values
/// terminated by a 0 per frame, byte-aligned, then a 5-bit "exception" stream for gaps that hit the
/// 3-bit ceiling (7). Frames are stored XOR-delta'd against the previous frame. Verified against
/// <c>fpcalc -raw</c> output.
/// </para>
/// </summary>
public static class ChromaprintComparer
{
    private const int NormalBits = 3;
    private const int ExceptionBits = 5;
    private const int MaxNormalValue = 7;

    // A corrupt 24-bit header could claim up to ~16M frames; cap the allocation. Real fingerprints run
    // a few frames per second, so even a multi-hour file stays well under this.
    private const int MaxFrames = 500_000;

    /// <summary>
    /// Fingerprint similarity in <c>[0,1]</c> (1 = identical), or <c>null</c> when either fingerprint is
    /// missing or can't be decoded — callers treat null as "gate not applicable" and fall back to other
    /// signals rather than blocking on a tooling gap.
    /// </summary>
    public static double? Similarity(string? compressedA, string? compressedB)
    {
        if (!TryDecode(compressedA, out var a) || !TryDecode(compressedB, out var b))
            return null;
        return Similarity(a, b);
    }

    /// <summary>
    /// Best-alignment similarity between two raw sub-fingerprint arrays: the minimum bit-error-rate over
    /// a small sliding offset (encodes/edits can shift frames by one or two), returned as <c>1 - BER</c>.
    /// </summary>
    public static double Similarity(uint[] a, uint[] b, int maxOffset = 6)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;

        var bestBer = 1.0;
        for (var offset = -maxOffset; offset <= maxOffset; offset++)
        {
            var (ai, bi) = offset >= 0 ? (offset, 0) : (0, -offset);
            var n = Math.Min(a.Length - ai, b.Length - bi);
            if (n < 10)
                continue; // too little overlap to be meaningful

            long bitErrors = 0;
            for (var k = 0; k < n; k++)
                bitErrors += BitOperations.PopCount(a[ai + k] ^ b[bi + k]);

            var ber = bitErrors / (double)(n * 32);
            if (ber < bestBer)
                bestBer = ber;
        }
        return 1.0 - bestBer;
    }

    /// <summary>Decodes a compressed base64 Chromaprint (URL-safe or standard) into its raw frames.</summary>
    public static bool TryDecode(string? compressed, out uint[] frames)
    {
        frames = [];
        if (string.IsNullOrWhiteSpace(compressed))
            return false;

        if (!TryFromBase64(compressed, out var data) || data.Length < 4)
            return false;

        var size = (data[1] << 16) | (data[2] << 8) | data[3];
        if (size <= 0 || size > MaxFrames)
            return false;

        var reader = new BitReader(data, startByte: 4);

        // Normal stream: 3-bit gap values, a 0 terminates each frame. Read until `size` terminators.
        var bits = new List<int>();
        var terminators = 0;
        while (terminators < size)
        {
            if (reader.Eof)
                return false; // truncated: still owed frames with no bits left
            var value = reader.Read(NormalBits);
            bits.Add(value);
            if (value == 0)
                terminators++;
        }

        // The compressor flushes (byte-aligns) between the normal and exception streams.
        reader.AlignToByte();
        for (var i = 0; i < bits.Count; i++)
            if (bits[i] == MaxNormalValue)
                bits[i] += reader.Read(ExceptionBits);

        // Unpack gaps → set bits per frame, then undo the XOR-delta against the previous frame.
        var result = new uint[size];
        uint acc = 0;
        var lastBit = 0;
        var frame = 0;
        foreach (var bit in bits)
        {
            if (bit == 0)
            {
                result[frame++] = acc;
                acc = 0;
                lastBit = 0;
            }
            else
            {
                lastBit += bit;
                acc |= 1u << (lastBit - 1);
            }
        }
        for (var i = 1; i < size; i++)
            result[i] ^= result[i - 1];

        frames = result;
        return true;
    }

    private static bool TryFromBase64(string s, out byte[] data)
    {
        // fpcalc / AcoustID use URL-safe base64 without padding.
        var normalized = s.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
        }
        try
        {
            data = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            data = [];
            return false;
        }
    }

    /// <summary>LSB-first bit reader over a byte buffer; reads past the end return zero-padded bits.</summary>
    private sealed class BitReader(byte[] data, int startByte)
    {
        private int _pos = startByte * 8;
        private readonly int _total = data.Length * 8;

        public bool Eof => _pos >= _total;

        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                if (_pos >= _total)
                    return value;
                var bit = (data[_pos >> 3] >> (_pos & 7)) & 1;
                value |= bit << i;
                _pos++;
            }
            return value;
        }

        public void AlignToByte()
        {
            if ((_pos & 7) != 0)
                _pos = (_pos + 7) & ~7;
        }
    }
}
