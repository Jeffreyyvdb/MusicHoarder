using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// A SHA-256 over values fed to it one at a time: a fingerprint of everything a computation is
/// about to be given, cheap enough to take on every request, so a result can be reused for as long
/// as the fingerprint of a fresh read stays the same.
///
/// <para>
/// Every value goes in with its length or at its fixed size, so two different sequences of values
/// never produce the same bytes: null is not the empty string, and "ab" then "c" is not "a" then
/// "bc". Add counts before lists for the same reason.
/// </para>
///
/// <para>
/// The bytes are this process's own (native UTF-16, native layout). A stamp is for comparing with
/// another stamp taken by the same running API, never for storing.
/// </para>
/// </summary>
public sealed class ContentStamp : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    // Values are a few bytes each and there are tens of thousands of them: gather, then hash in blocks.
    private readonly byte[] _buffer = new byte[8192];
    private int _used;

    public void Add(bool value) => Reserve(1)[0] = value ? (byte)1 : (byte)0;

    public void Add(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(sizeof(int)), value);

    public void Add(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(sizeof(long)), value);

    public void Add(double value) => Add(BitConverter.DoubleToInt64Bits(value));

    public void Add(Guid value) => value.TryWriteBytes(Reserve(16));

    public void Add(string? value)
    {
        if (value is null)
        {
            Add(-1);
            return;
        }

        Add(value.Length);
        var bytes = MemoryMarshal.AsBytes(value.AsSpan());
        if (bytes.Length > _buffer.Length)
        {
            Flush();
            _hash.AppendData(bytes);
            return;
        }

        bytes.CopyTo(Reserve(bytes.Length));
    }

    /// <summary>The fingerprint of everything added so far. The stamp starts over afterwards.</summary>
    public byte[] Finish()
    {
        Flush();
        return _hash.GetHashAndReset();
    }

    public void Dispose() => _hash.Dispose();

    private Span<byte> Reserve(int size)
    {
        if (_buffer.Length - _used < size)
            Flush();
        var span = _buffer.AsSpan(_used, size);
        _used += size;
        return span;
    }

    private void Flush()
    {
        if (_used == 0)
            return;
        _hash.AppendData(_buffer, 0, _used);
        _used = 0;
    }
}
