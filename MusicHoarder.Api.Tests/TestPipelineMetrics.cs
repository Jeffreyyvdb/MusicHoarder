using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Observability;

namespace MusicHoarder.Api.Tests;

/// <summary>
/// Builds a real <see cref="PipelineMetrics"/> for tests that construct pipeline services directly.
/// The instruments are no-ops without an exporter, so this just satisfies the constructor dependency.
/// </summary>
internal static class TestPipelineMetrics
{
    public static PipelineMetrics Create()
    {
        var meterFactory = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        return new PipelineMetrics(meterFactory);
    }
}
