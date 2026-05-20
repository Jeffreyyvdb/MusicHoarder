using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MusicHoarder.Api.Pipeline;

/// <summary>
/// Reports the cached reachability of the source/destination directories. Degraded
/// (not Unhealthy) when a directory is offline — an unreachable home-network share
/// shouldn't fail the container's liveness probe. Not tagged "live", so it never
/// leaks into the Dokploy/Docker /alive gate.
/// </summary>
public class LibraryDirectoriesHealthCheck(IDirectoryAvailability availability) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var s = availability.Current;
        var data = new Dictionary<string, object>
        {
            ["sourceAvailable"] = s.SourceAvailable,
            ["destinationAvailable"] = s.DestinationAvailable,
            ["checkedAtUtc"] = s.CheckedAtUtc,
        };

        if (s.AllAvailable)
            return Task.FromResult(HealthCheckResult.Healthy("Library directories reachable.", data));

        var offline = new List<string>(2);
        if (!s.SourceAvailable) offline.Add("source");
        if (!s.DestinationAvailable) offline.Add("destination");

        return Task.FromResult(HealthCheckResult.Degraded(
            $"Library {string.Join(" and ", offline)} directory unreachable.", data: data));
    }
}
