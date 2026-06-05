using System.Reflection;
using Microsoft.AspNetCore.Http;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Snapshots;
using MusicHoarder.Api.Version;

namespace MusicHoarder.Api.Tests.Version;

public class VersionEndpointsTests
{
    private sealed class FakeMonitor(ReleaseUpdateSnapshot snapshot) : IReleaseUpdateMonitor
    {
        public ReleaseUpdateSnapshot Current { get; } = snapshot;
    }

    [Fact]
    public void GetLatestVersion_PassesThroughSnapshotFields()
    {
        var publishedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new ReleaseUpdateSnapshot(
            LatestVersion: "9.9.9",
            ReleaseUrl: "https://github.com/Jeffreyyvdb/MusicHoarder/releases/tag/v9.9.9",
            PublishedAt: publishedAt,
            CheckedAtUtc: publishedAt);

        var result = VersionEndpoints.GetLatestVersion(new FakeMonitor(snapshot));
        var value = Value(result);

        Assert.Equal("9.9.9", GetProperty<string>(value, "latest"));
        Assert.Equal(snapshot.ReleaseUrl, GetProperty<string>(value, "releaseUrl"));
        Assert.Equal(publishedAt, GetProperty<DateTime?>(value, "publishedAt"));
        // updateAvailable must be the comparer's verdict on the *actual* running version (whatever the
        // test assembly resolves to) vs the snapshot's latest — confirms the endpoint wires them together.
        var expected = SemVerComparer.IsUpdateAvailable(EnrichmentSnapshotService.ResolveVersion(null), "9.9.9");
        Assert.Equal(expected, GetProperty<bool>(value, "updateAvailable"));
    }

    [Fact]
    public void GetLatestVersion_EmptySnapshot_ReportsNoUpdate()
    {
        var empty = new ReleaseUpdateSnapshot(null, null, null, DateTime.MinValue);

        var result = VersionEndpoints.GetLatestVersion(new FakeMonitor(empty));
        var value = Value(result);

        Assert.Null(GetProperty<string?>(value, "latest"));
        Assert.False(GetProperty<bool>(value, "updateAvailable"));
    }

    private static object Value(IResult result)
        => result.GetType().GetProperty("Value")!.GetValue(result)!;

    private static T GetProperty<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Property '{name}' not found");
        return (T)prop.GetValue(obj)!;
    }
}
