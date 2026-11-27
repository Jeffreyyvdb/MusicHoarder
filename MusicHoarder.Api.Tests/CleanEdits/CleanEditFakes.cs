using Microsoft.Extensions.Hosting;
using MusicHoarder.Api.CleanEdits;

namespace MusicHoarder.Api.Tests.CleanEdits;

/// <summary>A decoder that hands back five seconds of silence for any file it can "read".</summary>
internal sealed class SilentDecoder : IMonoDecoder
{
    public Func<string, bool> Unreadable { get; set; } = _ => false;

    public Task<float[]?> DecodeAsync(string path, CancellationToken ct) =>
        Task.FromResult(Unreadable(path) ? null : new float[BandSpectrogram.SampleRate * 5]);
}

internal sealed class ScriptedReferences : IEditionReferenceClient
{
    private readonly TaskCompletionSource _asked = new();
    public int Lookups;
    public bool Incomplete;
    public TaskCompletionSource? Block;
    public System.Collections.Concurrent.ConcurrentQueue<ReferenceQuery> Asked { get; } = new();

    public Task WaitUntilAskedAsync() => _asked.Task;

    public async Task<ReferenceLookup> FindAsync(ReferenceQuery query, CancellationToken ct)
    {
        Interlocked.Increment(ref Lookups);
        Asked.Enqueue(query);
        _asked.TrySetResult();
        if (Block is not null)
            await Block.Task.WaitAsync(ct);
        return new ReferenceLookup([], Incomplete);
    }

    public Task<byte[]?> DownloadPreviewAsync(string url, CancellationToken ct) => Task.FromResult<byte[]?>(null);
}

internal sealed class Lifetime(CancellationToken stopping) : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => stopping;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}
