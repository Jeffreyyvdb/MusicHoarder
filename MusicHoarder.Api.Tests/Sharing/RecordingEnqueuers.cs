using MusicHoarder.Api.Navidrome;
using MusicHoarder.Api.Sync;

namespace MusicHoarder.Api.Tests.Sharing;

/// <summary>
/// Counting stand-ins for the two like-propagation enqueuers.
///
/// <para>
/// They exist to assert a NEGATIVE: a member liking a track that was shared with them must not
/// enqueue anything. Those two calls mirror the library owner's own taste out to their Navidrome
/// and to any sync peer, so letting a guest's like reach them would star tracks in someone else's
/// server that they never liked — silently, and with no way to tell afterwards which likes were
/// theirs.
/// </para>
/// </summary>
internal sealed class RecordingNavidromeEnqueuer : INavidromeLikeEnqueuer
{
    public List<(int SongId, Guid OwnerUserId)> Calls { get; } = [];

    public void TryEnqueue(int songId, Guid ownerUserId) => Calls.Add((songId, ownerUserId));
}

/// <inheritdoc cref="RecordingNavidromeEnqueuer"/>
internal sealed class RecordingTrackSyncEnqueuer : ITrackSyncEnqueuer
{
    public List<(int SongId, Guid OwnerUserId)> Calls { get; } = [];

    public void TryEnqueue(int songId, Guid ownerUserId) => Calls.Add((songId, ownerUserId));
}

internal static class NoopNavidrome
{
    internal static INavidromeLikeEnqueuer Instance { get; } = new RecordingNavidromeEnqueuer();
}

internal static class NoopTrackSync
{
    internal static ITrackSyncEnqueuer Instance { get; } = new RecordingTrackSyncEnqueuer();
}
