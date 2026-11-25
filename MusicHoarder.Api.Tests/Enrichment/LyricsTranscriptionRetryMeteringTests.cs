using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Audio;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// A retry of a transcription upload sends the audio again, and the provider's quota counts it again, so the
/// audio meter must count it too. Runs the real transcoder and retry loop against a provider that fails once.
/// </summary>
public sealed class LyricsTranscriptionRetryMeteringTests : IDisposable
{
    private readonly DirectoryInfo _tmpDir = Directory.CreateTempSubdirectory("mh-stt-retry-");

    public void Dispose() => _tmpDir.Delete(recursive: true);

    [FfmpegFact]
    public async Task a_retried_upload_is_metered_once_per_upload()
    {
        var audio = Path.Combine(_tmpDir.FullName, "clip.wav");
        MakeSilence(audio, seconds: 2);

        var provider = new FailsOnceProvider();
        var options = new StaticOptionsMonitor<LyricsTranscriptionOptions>(new LyricsTranscriptionOptions
        {
            BaseUrl = "http://stt.test/v1",
            ApiKey = "test-key",
            RequestsPerMinute = 100,
        });
        var transcriber = new LyricsTranscriptionService(
            new HttpClient(provider),
            null!,
            new LlmLyricsAligner(
                new HttpClient(provider),
                new StaticOptionsMonitor<QualityGradingOptions>(new QualityGradingOptions()),
                options,
                NullLogger<LlmLyricsAligner>.Instance),
            new LyricsTranscriptionRateLimiter(options, NullLogger<LyricsTranscriptionRateLimiter>.Instance),
            options,
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { SourceDirectory = "/src", DestinationDirectory = "/dst" }),
            NullLogger<LyricsTranscriptionService>.Instance);

        await using var db = new MusicHoarderDbContext(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var budget = TestBudget.Over(db, new LyricsTimingOptions());

        await budget.SpendAsync(
            TranscriptionPurpose.TimingProbe,
            2,
            retry => transcriber.TranscribeClipAsync(audio, 0, 2, null, CancellationToken.None, retry),
            CancellationToken.None);

        Assert.Equal(2, provider.Uploads);
        Assert.Equal(2, await db.TranscriptionAudioSpends.CountAsync());
    }

    private static void MakeSilence(string path, int seconds)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", $"anullsrc=r=16000:cl=mono", "-t", seconds.ToString(), path })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class FailsOnceProvider : HttpMessageHandler
    {
        public int Uploads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uploads++;
            return Task.FromResult(Uploads == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"text\":\"\",\"words\":[],\"segments\":[]}", Encoding.UTF8, "application/json"),
                });
        }
    }
}
