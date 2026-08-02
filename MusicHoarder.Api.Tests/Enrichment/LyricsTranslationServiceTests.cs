using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Enrichment;

public class LyricsTranslationServiceTests
{
    [Fact]
    public async Task TranslatesSyncedLyrics_PreservingTimestamps()
    {
        var handler = new ScriptedChatHandler(_ => Chat(
            """{"language":"ar","lines":[{"i":0,"r":"7abibi","t":"My darling"},{"i":1,"r":"ya nour el ein","t":"Oh light of my eye"}]}"""));
        var service = CreateService(handler);

        var result = await service.TranslateAsync(
            "[00:12.34]حبيبي\n[00:20.00]يا نور العين", null, "Amr Diab", "Nour El Ein", CancellationToken.None);

        Assert.Equal("ar", result.LanguageCode);
        Assert.Equal("[00:12.34]7abibi\n[00:20.00]ya nour el ein", result.RomanizedSynced);
        Assert.Equal("7abibi\nya nour el ein", result.RomanizedPlain);
        Assert.Equal("[00:12.34]My darling\n[00:20.00]Oh light of my eye", result.TranslatedSynced);
        Assert.Equal("My darling\nOh light of my eye", result.TranslatedPlain);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task PlainOnlyLyrics_ProduceNoSyncedDocuments()
    {
        var handler = new ScriptedChatHandler(_ => Chat(
            """{"language":"es","lines":[{"i":0,"r":"KYEH-roh","t":"I want"}]}"""));
        var service = CreateService(handler);

        var result = await service.TranslateAsync(null, "Quiero", "Artist", "Title", CancellationToken.None);

        Assert.Null(result.RomanizedSynced);
        Assert.Null(result.TranslatedSynced);
        Assert.Equal("KYEH-roh", result.RomanizedPlain);
        Assert.Equal("I want", result.TranslatedPlain);
    }

    [Fact]
    public async Task ChunksLongSongs_AndReassemblesInOrder()
    {
        // 61 lines with ChunkSize 60 → two calls: 60 lines then 1 line, indices restarting per chunk.
        var handler = new ScriptedChatHandler(request =>
        {
            var lineCount = request.Split('\n').Count(l => l.Length > 0 && char.IsDigit(l[0]) && l.Contains('\t'));
            var lines = string.Join(',', Enumerable.Range(0, lineCount)
                .Select(i => $$"""{"i":{{i}},"r":"r{{i}}","t":"t{{i}}"}"""));
            return Chat($$"""{"language":"fr","lines":[{{lines}}]}""");
        });
        var service = CreateService(handler);

        var source = string.Join('\n', Enumerable.Range(0, 61).Select(i => $"[{i:00}:00.00]line {i}"));
        var result = await service.TranslateAsync(source, null, null, null, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        var romanized = result.RomanizedPlain!.Split('\n');
        Assert.Equal(61, romanized.Length);
        // The second chunk's indices restart at 0; its single line must land at overall position 60.
        Assert.Equal("r0", romanized[0]);
        Assert.Equal("r0", romanized[60]);
        Assert.Equal("r59", romanized[59]);
        Assert.StartsWith("[60:00.00]", result.RomanizedSynced!.Split('\n')[60]);
    }

    [Fact]
    public async Task LineCountMismatch_Throws()
    {
        var handler = new ScriptedChatHandler(_ => Chat(
            """{"language":"ar","lines":[{"i":0,"r":"only","t":"one"}]}"""));
        var service = CreateService(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateAsync("[00:01.00]a\n[00:02.00]b", null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task EnglishSong_ShortCircuits_WithoutFurtherChunks()
    {
        var handler = new ScriptedChatHandler(_ => Chat("""{"language":"en","lines":[]}"""));
        var service = CreateService(handler);

        var source = string.Join('\n', Enumerable.Range(0, 61).Select(i => $"line {i}"));
        var result = await service.TranslateAsync(null, source, null, null, CancellationToken.None);

        Assert.Equal("en", result.LanguageCode);
        Assert.Null(result.RomanizedPlain);
        Assert.Null(result.TranslatedPlain);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task EnglishPassthroughLines_AreDropped()
    {
        // Some models return per-line passthrough for an English song instead of the empty-lines
        // signal; verbatim copies must not be stored (they'd surface a pointless toggle).
        var handler = new ScriptedChatHandler(_ => Chat(
            """{"language":"en","lines":[{"i":0,"r":"Hello world","t":"Hello world"}]}"""));
        var service = CreateService(handler);

        var result = await service.TranslateAsync(null, "Hello world", null, null, CancellationToken.None);

        Assert.Equal("en", result.LanguageCode);
        Assert.Null(result.RomanizedPlain);
        Assert.Null(result.TranslatedPlain);
    }

    [Fact]
    public async Task HttpFailure_Throws()
    {
        var handler = new ScriptedChatHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited"),
        });
        var service = CreateService(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateAsync("[00:01.00]a", null, null, null, CancellationToken.None));
    }

    [Fact]
    public void IsConfigured_FalseWithoutOpenRouterKey()
    {
        var service = CreateService(new ScriptedChatHandler(_ => Chat("{}")), apiKey: "");

        Assert.False(service.IsConfigured);
    }

    [Fact]
    public void IsConfigured_FalseWhenDisabledOrModelMissing()
    {
        Assert.False(CreateService(new ScriptedChatHandler(_ => Chat("{}")), enabled: false).IsConfigured);
        Assert.False(CreateService(new ScriptedChatHandler(_ => Chat("{}")), model: "").IsConfigured);
        Assert.True(CreateService(new ScriptedChatHandler(_ => Chat("{}"))).IsConfigured);
    }

    // --- helpers ---

    private static LyricsTranslationService CreateService(
        ScriptedChatHandler handler, string apiKey = "test-key", bool enabled = true, string model = "test/model")
    {
        var grading = new QualityGradingOptions { ApiKey = apiKey, BaseUrl = "https://openrouter.test/api/v1" };
        var translation = new LyricsTranslationOptions { Enabled = enabled, Model = model, ChunkSize = 60 };
        return new LyricsTranslationService(
            new HttpClient(handler),
            new TestOptionsMonitor<QualityGradingOptions>(grading),
            new TestOptionsMonitor<LyricsTranslationOptions>(translation),
            NullLogger<LyricsTranslationService>.Instance);
    }

    /// <summary>Wraps a chat-completion JSON payload in the OpenAI response envelope.</summary>
    private static HttpResponseMessage Chat(string content)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } },
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>Responds to each chat call by inspecting the request's user message.</summary>
    private sealed class ScriptedChatHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var user = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() ?? "";
            return respond(user);
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
