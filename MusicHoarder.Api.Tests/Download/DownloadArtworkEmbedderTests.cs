using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// Round-trips real audio files through <see cref="DownloadArtworkEmbedder"/> (TagLib hits the real
/// filesystem, so these use temp copies of the committed silent fixtures). This is what gives a
/// yt-dlp download — which embeds no artwork of its own — a cover the scanner and the destination
/// cover writer can carry forward.
/// </summary>
public class DownloadArtworkEmbedderTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private const string ImageUrl = "https://i.ytimg.example/vi/abc/maxresdefault.jpg";
    private readonly List<string> tempFiles = [];

    // opus is what yt-dlp produces by default, so its Xiph picture block is the case that matters
    // most here; mp3/flac cover the providers that hand back other containers.
    [Theory]
    [InlineData("silence.opus")]
    [InlineData("silence.mp3")]
    [InlineData("silence.flac")]
    public async Task Embed_WritesFetchedImageAsFrontCover(string fixture)
    {
        var path = CopyFixture(fixture);
        var handler = new StubHandler { [ImageUrl] = (HttpStatusCode.OK, Jpeg(8192)) };

        var embedded = await CreateEmbedder(handler).EmbedAsync(path, ImageUrl);

        Assert.True(embedded);
        using var file = TagLib.File.Create(path);
        var picture = Assert.Single(file.Tag.Pictures);
        Assert.Equal(TagLib.PictureType.FrontCover, picture.Type);
        Assert.Equal("image/jpeg", picture.MimeType);
        Assert.Equal(8192, picture.Data.Count);
    }

    [Fact]
    public async Task Embed_NoUrl_IsNoOp()
    {
        var path = CopyFixture("silence.mp3");
        var handler = new StubHandler();

        Assert.False(await CreateEmbedder(handler).EmbedAsync(path, null));
        Assert.False(await CreateEmbedder(handler).EmbedAsync(path, "  "));
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task Embed_FileAlreadyHasArt_KeepsItAndSkipsTheFetch()
    {
        // A provider that ships tagged files (slskd) already carries the real release art — it must
        // never be replaced by a video thumbnail.
        var path = CopyFixture("silence.flac");
        var existing = Jpeg(6000);
        using (var seed = TagLib.File.Create(path))
        {
            seed.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(existing))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/jpeg",
            }];
            seed.Save();
        }

        var handler = new StubHandler { [ImageUrl] = (HttpStatusCode.OK, Jpeg(8192)) };

        Assert.False(await CreateEmbedder(handler).EmbedAsync(path, ImageUrl));
        Assert.Empty(handler.RequestedUrls);
        using var file = TagLib.File.Create(path);
        Assert.Equal(existing.Length, Assert.Single(file.Tag.Pictures).Data.Count);
    }

    [Fact]
    public async Task Embed_RejectsTinyAndNonImageResponses()
    {
        var path = CopyFixture("silence.mp3");
        var handler = new StubHandler
        {
            // ytimg soft-404s a missing thumbnail variant with a tiny placeholder image.
            ["https://img.example/tiny.jpg"] = (HttpStatusCode.OK, Jpeg(100)),
            ["https://img.example/html.jpg"] = (HttpStatusCode.OK, new byte[8192]),
        };
        var embedder = CreateEmbedder(handler);

        Assert.False(await embedder.EmbedAsync(path, "https://img.example/tiny.jpg"));
        Assert.False(await embedder.EmbedAsync(path, "https://img.example/html.jpg"));
        Assert.False(await embedder.EmbedAsync(path, "https://img.example/missing.jpg"));

        using var file = TagLib.File.Create(path);
        Assert.Empty(file.Tag.Pictures);
    }

    [Fact]
    public async Task Embed_MissingFile_ReturnsFalseWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mh-missing-{Guid.NewGuid():N}.opus");
        var handler = new StubHandler { [ImageUrl] = (HttpStatusCode.OK, Jpeg(8192)) };

        Assert.False(await CreateEmbedder(handler).EmbedAsync(path, ImageUrl));
    }

    private static DownloadArtworkEmbedder CreateEmbedder(StubHandler handler) =>
        new(new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/src",
                DestinationDirectory = "/dest",
            }),
            NullLogger<DownloadArtworkEmbedder>.Instance);

    /// <summary>JPEG magic bytes padded to <paramref name="length"/> — TagLib never decodes the image.</summary>
    private static byte[] Jpeg(int length)
    {
        var bytes = new byte[length];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        return bytes;
    }

    private string CopyFixture(string fixtureName)
    {
        var source = Path.Combine(FixtureDir, fixtureName);
        var dest = Path.Combine(
            Path.GetTempPath(),
            $"mh-downloadart-{Guid.NewGuid():N}{Path.GetExtension(fixtureName)}");
        File.Copy(source, dest, overwrite: true);
        tempFiles.Add(dest);
        return dest;
    }

    public void Dispose()
    {
        foreach (var f in tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body)> _responses = [];

        public List<string> RequestedUrls { get; } = [];

        public (HttpStatusCode, byte[]) this[string url]
        {
            set => _responses[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);
            return Task.FromResult(_responses.TryGetValue(url, out var entry)
                ? new HttpResponseMessage(entry.Status) { Content = new ByteArrayContent(entry.Body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
