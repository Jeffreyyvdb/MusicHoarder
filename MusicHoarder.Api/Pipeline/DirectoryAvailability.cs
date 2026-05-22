using Microsoft.Extensions.Options;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Pipeline;

/// <summary>Cached reachability of the configured source/destination directories.</summary>
public record DirectoryAvailabilitySnapshot(
    bool SourceAvailable,
    bool DestinationAvailable,
    string SourceDirectory,
    string DestinationDirectory,
    DateTime CheckedAtUtc)
{
    public bool AllAvailable => SourceAvailable && DestinationAvailable;
}

public interface IDirectoryAvailability
{
    /// <summary>The most recent probe result. Never blocks; safe to read on a request thread.</summary>
    DirectoryAvailabilitySnapshot Current { get; }

    /// <summary>Force an immediate probe, update <see cref="Current"/>, and fire any reconnect side-effects.</summary>
    Task<DirectoryAvailabilitySnapshot> ProbeNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Periodically probes the source/destination directories so pipeline stages can gate
/// on reachability instead of throwing. When the source transitions offline→online it
/// auto-triggers a scan, so reconnecting to the home network resumes the pipeline on its own.
/// </summary>
public class DirectoryAvailabilityMonitor(
    JobManager jobManager,
    IOptions<MusicEnricherOptions> options,
    ILogger<DirectoryAvailabilityMonitor> logger)
    : BackgroundService, IDirectoryAvailability
{
    private volatile DirectoryAvailabilitySnapshot _current = new(
        SourceAvailable: false,
        DestinationAvailable: false,
        SourceDirectory: options.Value.SourceDirectory,
        DestinationDirectory: options.Value.DestinationDirectory,
        CheckedAtUtc: DateTime.MinValue);

    public DirectoryAvailabilitySnapshot Current => _current;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.DirectoryProbeIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeNowAsync(stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Directory availability probe failed unexpectedly");
                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async Task<DirectoryAvailabilitySnapshot> ProbeNowAsync(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var timeout = TimeSpan.FromSeconds(opts.DirectoryProbeTimeoutSeconds);

        var previous = _current;
        var sourceAvailable = await ProbePathAsync(opts.SourceDirectory, timeout, cancellationToken);
        var destinationAvailable = await ProbePathAsync(opts.DestinationDirectory, timeout, cancellationToken);

        var next = new DirectoryAvailabilitySnapshot(
            sourceAvailable,
            destinationAvailable,
            opts.SourceDirectory,
            opts.DestinationDirectory,
            DateTime.UtcNow);
        _current = next;

        var firstProbe = previous.CheckedAtUtc == DateTime.MinValue;

        if (previous.SourceAvailable != sourceAvailable && !firstProbe)
            logger.LogInformation(
                "Source directory {Directory} is now {State}",
                opts.SourceDirectory, sourceAvailable ? "reachable" : "unreachable");

        if (previous.DestinationAvailable != destinationAvailable && !firstProbe)
            logger.LogInformation(
                "Destination directory {Directory} is now {State}",
                opts.DestinationDirectory, destinationAvailable ? "reachable" : "unreachable");

        // Source came back (or was reachable on the very first probe): kick a discovery scan so the
        // library populates without the user having to click anything. Discovery is cheap and is the
        // prerequisite for any manual testing, so it runs even when AutoStartPipeline is off — only
        // the downstream processing (fingerprint → enrich → build) is gated by that flag.
        if (sourceAvailable && (!previous.SourceAvailable || firstProbe))
        {
            if (jobManager.TryStartJob(JobType.Scan, out _, out _))
                logger.LogInformation(
                    "Auto-triggered scan of {SourceDirectory} ({Reason})",
                    opts.SourceDirectory, firstProbe ? "startup" : "source reconnected");
        }

        return next;
    }

    private static async Task<bool> ProbePathAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            // Directory.Exists can block on an unreachable network mount, so cap it with a timeout.
            return await Task.Run(() => Directory.Exists(path), cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
