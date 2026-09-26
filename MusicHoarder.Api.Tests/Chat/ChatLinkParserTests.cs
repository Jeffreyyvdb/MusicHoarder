using MusicHoarder.Api.Chat;

namespace MusicHoarder.Api.Tests.Chat;

public class ChatLinkParserTests
{
    [Theory]
    // What the apps actually put in a share sheet.
    [InlineData("Check out this song on Spotify: https://open.spotify.com/track/0DiWol3AO6WpXZgp0goxAV?si=1a2b",
        "https://open.spotify.com/track/0DiWol3AO6WpXZgp0goxAV?si=1a2b")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "https://youtu.be/dQw4w9WgXcQ?si=abc")]
    [InlineData("listen (https://example.com/a).", "https://example.com/a")]
    [InlineData("see https://en.wikipedia.org/wiki/Daft_Punk_(band), ok", "https://en.wikipedia.org/wiki/Daft_Punk_(band)")]
    [InlineData("no link here", null)]
    [InlineData("ftp://example.com/file", null)]
    [InlineData(null, null)]
    public void Finds_the_first_web_link_in_shared_text(string? text, string? expected) =>
        Assert.Equal(expected, ChatLinkParser.FindFirstUrl(text));

    [Theory]
    [InlineData("https://open.spotify.com/track/0DiWol3AO6WpXZgp0goxAV?si=1a2b", "https://open.spotify.com/track/0DiWol3AO6WpXZgp0goxAV", "spotify", "track", "0DiWol3AO6WpXZgp0goxAV")]
    [InlineData("https://open.spotify.com/intl-nl/album/2noRn2Aes5aoNVsU6iWThc", "https://open.spotify.com/album/2noRn2Aes5aoNVsU6iWThc", "spotify", "album", "2noRn2Aes5aoNVsU6iWThc")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=x", "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", "spotify", "playlist", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/artist/4tZwfgrHOc3mvqYlEYSvVi", "https://open.spotify.com/artist/4tZwfgrHOc3mvqYlEYSvVi", "spotify", "artist", "4tZwfgrHOc3mvqYlEYSvVi")]
    [InlineData("https://spotify.link/AbCdEf", "https://spotify.link/AbCdEf", "spotify", null, null)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtube", "video", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDxyz&t=42", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtube", "video", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtube", "video", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&feature=share", "https://music.youtube.com/watch?v=dQw4w9WgXcQ", "youtube", "video", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/playlist?list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI", "https://www.youtube.com/playlist?list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI", "youtube", "playlist", "PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI")]
    [InlineData("https://example.com/some/page?x=1", "https://example.com/some/page?x=1", null, null, null)]
    public void Classifies_spotify_and_youtube_links_and_drops_tracking(
        string url, string canonical, string? provider, string? kind, string? id)
    {
        var parsed = ChatLinkParser.Classify(url);

        Assert.Equal(canonical, parsed.Url);
        Assert.Equal(provider, parsed.Provider);
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(id, parsed.Id);
    }

    [Fact]
    public void Recognises_this_instances_own_share_links_only_on_its_own_host()
    {
        var own = ChatLinkParser.Classify("https://music.example/share/abcDEF123_-xyz", "music.example");
        Assert.Equal(("musichoarder", "share", "abcDEF123_-xyz"), (own.Provider, own.Kind, own.Id));

        var elsewhere = ChatLinkParser.Classify("https://evil.example/share/abcDEF123_-xyz", "music.example");
        Assert.Null(elsewhere.Provider);
    }
}
