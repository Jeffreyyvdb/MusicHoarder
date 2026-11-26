using Microsoft.Extensions.Logging;
using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Download;

internal sealed class FakeDownloadOnlyProvider(string name) : IDownloadProvider
{
    public string Name => name;

    public Task<DownloadResult> DownloadAsync(DownloadRequest req, CancellationToken ct) =>
        Task.FromResult(DownloadResult.Missing("unused"));
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Messages.Add((logLevel, formatter(state, exception)));
}
