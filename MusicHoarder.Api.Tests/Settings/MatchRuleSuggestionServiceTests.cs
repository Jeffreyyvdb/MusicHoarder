using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Tests.Settings;

public class MatchRuleSuggestionServiceTests
{
    [Fact]
    public async Task Llm_Suggestion_IsParsedCompiledAndScored()
    {
        var db = CreateDb();
        AddSong(db, "ADF Samski | Wintersessie 2020 | 101Barz");
        AddSong(db, "Yung Nnelg | Wintersessie 2020 | 101Barz");
        AddSong(db, "Some Other Song");
        await db.SaveChangesAsync();

        var client = new FakeChatClient(configured: true,
            content: """{"rules":[{"name":"101Barz sessions","pattern":"{artist} | {title} | 101Barz","sourceField":"title","albumOverride":"101Barz sessies","albumArtistOverride":"101Barz"}]}""");
        var service = CreateService(db, client);

        var result = await service.SuggestAsync();

        Assert.Equal("llm", result.Source);
        Assert.Equal(1, client.CallCount);
        var s = Assert.Single(result.Suggestions);
        Assert.Equal("{artist} | {title} | 101Barz", s.Pattern);
        Assert.Equal("101Barz sessies", s.AlbumOverride);
        Assert.Equal("101Barz", s.AlbumArtistOverride);
        Assert.Equal(2, s.MatchCount);     // the two 101Barz songs, not "Some Other Song"
        Assert.Equal(3, s.SampleSize);
    }

    [Fact]
    public async Task ZeroMatchSuggestion_IsDropped()
    {
        var db = CreateDb();
        AddSong(db, "ADF Samski | Wintersessie 2020 | 101Barz");
        await db.SaveChangesAsync();

        var client = new FakeChatClient(configured: true,
            content: """{"rules":[{"name":"nope","pattern":"{artist} | {title} | SomeOtherChannel","sourceField":"title"}]}""");
        var service = CreateService(db, client);

        var result = await service.SuggestAsync();

        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task NotConfigured_FallsBackToHeuristic_ProposesSharedAnchor()
    {
        var db = CreateDb();
        // Vary the session label so the channel "101Barz" is the unique shared anchor.
        string[] labels = ["Studiosessie", "Zomersessie", "Wintersessie", "Throwback Sessie", "Megasessie"];
        for (var i = 0; i < 5; i++)
            AddSong(db, $"Artist{i} - {labels[i]} {i} - 101Barz");
        await db.SaveChangesAsync();

        var client = new FakeChatClient(configured: false, content: "{}");
        var service = CreateService(db, client);

        var result = await service.SuggestAsync();

        Assert.Equal("heuristic", result.Source);
        Assert.Equal(0, client.CallCount);
        Assert.NotEmpty(result.Suggestions);
        // The shared "101Barz" anchor is proposed as the album artist and matched against the sample.
        Assert.Contains(result.Suggestions, s => s.AlbumArtistOverride == "101Barz" && s.MatchCount >= 5);
    }

    [Fact]
    public async Task NoUnmatchedSongs_ReturnsEmpty()
    {
        var db = CreateDb();
        var service = CreateService(db, new FakeChatClient(configured: true, content: "{}"));

        var result = await service.SuggestAsync();

        Assert.Empty(result.Suggestions);
        Assert.Equal(0, result.SampleSize);
    }

    // --- helpers ---

    private static MusicHoarderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static void AddSong(MusicHoarderDbContext db, string title)
    {
        db.Songs.Add(new SongMetadata
        {
            OwnerUserId = Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = $"/root/music/{title}.mp3",
            FileName = $"{title}.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = title,
            EnrichmentStatus = EnrichmentStatus.NeedsReview,
        });
    }

    private static MatchRuleSuggestionService CreateService(MusicHoarderDbContext db, FakeChatClient client) =>
        new(new SimpleScopeFactory(db), client,
            new TestOptionsMonitor(new QualityGradingOptions { Model = "test/model" }),
            NullLogger<MatchRuleSuggestionService>.Instance);

    private sealed class FakeChatClient(bool configured, string content) : IChatCompletionClient
    {
        public int CallCount { get; private set; }
        public bool IsConfigured => configured;

        public Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new ChatCompletionResult(content, 100, 50));
        }
    }

    private sealed class TestOptionsMonitor(QualityGradingOptions value) : IOptionsMonitor<QualityGradingOptions>
    {
        public QualityGradingOptions CurrentValue { get; } = value;
        public QualityGradingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<QualityGradingOptions, string?> listener) => null;
    }

    private sealed class SimpleScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(new Provider(db));
        private sealed class Scope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;
            public void Dispose() { }
        }
        private sealed class Provider(MusicHoarderDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(MusicHoarderDbContext) ? db : null;
        }
    }
}
