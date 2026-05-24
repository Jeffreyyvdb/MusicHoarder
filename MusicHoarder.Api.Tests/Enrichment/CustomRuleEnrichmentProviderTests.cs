using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Tests.Enrichment;

public class CustomRuleEnrichmentProviderTests
{
    [Fact]
    public async Task TitleRule_Matches_ReturnsAuthoritativeMatched()
    {
        var provider = Create(("101Barz sessions", "{artist} | {title} | 101Barz", MatchRuleSourceField.Title));
        var song = Song(artist: "Yung Nnelg", title: "Yung Nnelg | Wintersessie 2020 | 101Barz", album: "101Barz sessies");

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
        Assert.True(matched.Result.Authoritative);
        Assert.Equal("Yung Nnelg", matched.Result.Artist);
        Assert.Equal("Wintersessie 2020", matched.Result.Title);
        Assert.Equal("101Barz sessies", matched.Result.Album);
        Assert.Equal("CustomRule", matched.Result.MatchedBy);
        Assert.Contains("rule:101Barz sessions", matched.Result.MatchWarnings);
    }

    [Fact]
    public async Task NoEmbeddedArtist_CapturedArtistIsUsed()
    {
        var provider = Create(("101Barz sessions", "{artist} | {title} | 101Barz", MatchRuleSourceField.Title));
        var song = Song(artist: null, title: "Some Rapper | Zomersessie 2021 | 101Barz");

        var matched = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));
        Assert.Equal("Some Rapper", matched.Result.Artist);
        Assert.Equal("Zomersessie 2021", matched.Result.Title);
    }

    [Fact]
    public async Task NonMatchingTitle_ReturnsNoMatch()
    {
        var provider = Create(("101Barz sessions", "{artist} | {title} | 101Barz", MatchRuleSourceField.Title));
        var song = Song(artist: "Adele", title: "Hello");

        Assert.IsType<ProviderNoMatch>(await provider.TryEnrichAsync(song));
    }

    [Fact]
    public async Task FileNameRule_MatchesAgainstFileName()
    {
        var provider = Create(("scene", "{artist} - {title}", MatchRuleSourceField.FileName));
        var song = Song(artist: null, title: null, fileName: "Boef - Habiby.mp3");

        var matched = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));
        Assert.Equal("Boef", matched.Result.Artist);
        Assert.Equal("Habiby", matched.Result.Title);
    }

    [Fact]
    public void CanHandle_NoRules_IsFalseOnceWarmed()
    {
        var provider = Create(); // no rules
        Assert.False(provider.CanHandle(Song(title: "x | y | z")));
    }

    [Fact]
    public void CanHandle_WithRules_IsTrue()
    {
        var provider = Create(("r", "{title} | 101Barz", MatchRuleSourceField.Title));
        Assert.True(provider.CanHandle(Song(title: "anything")));
    }

    // --- helpers ---

    private static CustomRuleEnrichmentProvider Create(params (string Name, string Pattern, MatchRuleSourceField Field)[] rules)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
        });
        return new CustomRuleEnrichmentProvider(new FakeMatchRuleService(rules), options, NullLogger<CustomRuleEnrichmentProvider>.Instance);
    }

    private static SongMetadata Song(string? artist = null, string? title = null, string? album = null, string fileName = "x.mp3") => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = "/" + fileName,
        FileName = fileName,
        Extension = Path.GetExtension(fileName),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        Title = title,
        Album = album,
    };

    private sealed class FakeMatchRuleService : IMatchRuleService
    {
        private readonly List<EnabledMatchRule> _rules;

        public FakeMatchRuleService((string Name, string Pattern, MatchRuleSourceField Field)[] rules)
        {
            _rules = [];
            foreach (var (name, pattern, field) in rules)
            {
                Assert.True(MatchRulePattern.TryCompile(pattern, out var compiled, out _), $"test pattern failed to compile: {pattern}");
                _rules.Add(new EnabledMatchRule(_rules.Count + 1, name, field, compiled!));
            }
        }

        public bool HasEnabledRules => _rules.Count > 0;
        public Task<IReadOnlyList<EnabledMatchRule>> GetEnabledAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EnabledMatchRule>>(_rules);
        public Task<IReadOnlyList<MetadataMatchRule>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MetadataMatchRule> CreateAsync(MatchRuleInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MetadataMatchRule?> UpdateAsync(int id, MatchRuleInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
