namespace MusicHoarder.Api.Library;

/// <summary>
/// Remembers how many suggestions a name detector found for an account, next to a
/// <see cref="Persistence.ContentStamp"/> of everything that detection was given. The Inbox's name
/// counts are asked for on every page load, the names they are computed from change a few times a
/// week, and a detection compares every artist and album name in the library with every other.
///
/// <para>
/// Nothing ever invalidates an entry, and nothing has to: the caller stamps a fresh read of the
/// inputs on every request, and a remembered count is only returned for the stamp it was computed
/// from. A write to anything the detector reads — by a request, a background service, a revert —
/// changes the stamp; a write to anything else (a play count, a build status) does not. That holds
/// only while the stamp covers <b>every</b> input, so a detector that uses this keeps its
/// detection in a static method and stamps exactly that method's arguments.
/// </para>
///
/// <para>
/// It holds a 32-byte stamp and a number per entry: no report, no song rows.
/// </para>
/// </summary>
public sealed class DetectionCountMemo
{
    /// <summary>
    /// One entry per detector per account that opens the Inbox, so a handful. The cap only makes
    /// "a handful" a guarantee: past it everything is forgotten, and counted again when next asked.
    /// </summary>
    internal const int MaxEntries = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Detector, Guid OwnerUserId), (byte[] Stamp, int Count)> _entries = [];

    /// <summary>
    /// The count remembered for <paramref name="stamp"/>, or <paramref name="count"/>'s answer,
    /// which is then remembered in place of whatever this detector last found for this owner.
    /// </summary>
    public int GetOrCount(string detector, Guid ownerUserId, byte[] stamp, Func<int> count)
    {
        var key = (detector, ownerUserId);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var known) && known.Stamp.AsSpan().SequenceEqual(stamp))
                return known.Count;
        }

        // Not under the lock: a detection takes a while, and two requests that both miss only do
        // the same work twice. Whichever stores last, the entry is a stamp and the count that
        // belongs to it, so an older request finishing late cannot plant a stale answer — the next
        // caller's stamp will simply not match it.
        var counted = count();
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
                _entries.Clear();
            _entries[key] = (stamp, counted);
        }

        return counted;
    }
}
