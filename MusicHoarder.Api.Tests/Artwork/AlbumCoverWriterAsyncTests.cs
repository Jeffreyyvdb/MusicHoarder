using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Artwork;

public class AlbumCoverWriterAsyncTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly ExternalCoverArtQuery Query = new("mbid-1", "rg-1", "Artist", "Album");

    [Fact]
    public async Task ExistingDestinationCoverSkipsEverything()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/track.mp3"] = new("audio"),
            ["/dest/Album/track.mp3"] = new("audio"),
            ["/dest/Album/cover.jpg"] = new("existing"),
        });
        var fetcher = new StubExternalCoverArtFetcher();
        var writer = CreateWriter(fs, fetcher);

        var result = await writer.WriteIfMissingAsync("/dest/Album", "/source/track.mp3", Query);

        Assert.False(result.Written);
        Assert.Empty(fetcher.Calls);
        Assert.Equal("existing", fs.File.ReadAllText("/dest/Album/cover.jpg"));
    }

    [Fact]
    public async Task SourceArtWinsOverExternalFetch()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/track.mp3"] = new("audio"),
            ["/source/folder.png"] = new(Png),
            ["/dest/Album/track.mp3"] = new("audio"),
        });
        var fetcher = new StubExternalCoverArtFetcher();
        var writer = CreateWriter(fs, fetcher);

        var result = await writer.WriteIfMissingAsync("/dest/Album", "/source/track.mp3", Query);

        Assert.True(result.Written);
        Assert.Equal("source", result.Source);
        Assert.Empty(fetcher.Calls);
        Assert.True(fs.File.Exists("/dest/Album/cover.png"));
    }

    [Fact]
    public async Task FetchedCoverIsWrittenWithContentTypeExtension()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/track.mp3"] = new("audio"),
            ["/dest/Album/track.mp3"] = new("audio"),
        });
        var fetcher = new StubExternalCoverArtFetcher
        {
            Result = new ExternalCoverArtFetchResult(new FetchedCoverArt(Png, "image/png", "coverartarchive"), false),
        };
        var writer = CreateWriter(fs, fetcher);

        var result = await writer.WriteIfMissingAsync("/dest/Album", "/source/track.mp3", Query);

        Assert.True(result.Written);
        Assert.Equal("coverartarchive", result.Source);
        Assert.Equal(Png, fs.File.ReadAllBytes("/dest/Album/cover.png"));
        Assert.Equal([Query], fetcher.Calls);
    }

    [Fact]
    public async Task CorruptEmbeddedArtFallsThroughToExternalFetch()
    {
        // Garbage bytes the resolver masks as image/jpeg (its mime fallback) but no decoder accepts —
        // mirrors the SkiaSharp "Value cannot be null (Parameter 'codec')" tracks seen in preview.
        var corrupt = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/track.mp3"] = new("audio"),
            ["/dest/Album/track.mp3"] = new("audio"),
        });
        var fetcher = new StubExternalCoverArtFetcher
        {
            Result = new ExternalCoverArtFetchResult(new FetchedCoverArt(Png, "image/png", "deezer"), false),
        };
        var writer = CreateWriter(fs, fetcher, embedded: new EmbeddedPicture(corrupt, "image/jpeg"));

        var result = await writer.WriteIfMissingAsync("/dest/Album", "/source/track.mp3", Query);

        Assert.True(result.Written);
        Assert.Equal("deezer", result.Source);
        Assert.Equal(Png, fs.File.ReadAllBytes("/dest/Album/cover.png"));
    }

    [Fact]
    public async Task CorruptFolderImageFallsThroughToExternalFetch()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/track.mp3"] = new("audio"),
            ["/source/cover.jpg"] = new(new byte[] { 0x00, 0x01, 0x02, 0x03 }),
            ["/dest/Album/track.mp3"] = new("audio"),
        });
        var fetcher = new StubExternalCoverArtFetcher
        {
            Result = new ExternalCoverArtFetchResult(new FetchedCoverArt(Png, "image/png", "itunes"), false),
        };
        var writer = CreateWriter(fs, fetcher);

        var result = await writer.WriteIfMissingAsync("/dest/Album", "/source/track.mp3", Query);

        Assert.True(result.Written);
        Assert.Equal("itunes", result.Source);
    }

    [Fact]
    public async Task MasterFlagOffSkipsExternalFetch()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/track.mp3"] = new("audio"),
            ["/dest/Album/track.mp3"] = new("audio"),
        });
        var fetcher = new StubExternalCoverArtFetcher
        {
            Result = new ExternalCoverArtFetchResult(new FetchedCoverArt(Png, "image/png", "deezer"), false),
        };
        var writer = CreateWriter(fs, fetcher, o => o.EnableExternalCoverArtFetch = false);

        var result = await writer.WriteIfMissingAsync("/dest/Album", "/source/track.mp3", Query);

        Assert.False(result.Written);
        Assert.Empty(fetcher.Calls);
    }

    [Fact]
    public async Task NullQuerySkipsExternalFetch()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/track.mp3"] = new("audio"),
            ["/dest/Album/track.mp3"] = new("audio"),
        });
        var fetcher = new StubExternalCoverArtFetcher();
        var writer = CreateWriter(fs, fetcher);

        var result = await writer.WriteIfMissingAsync("/dest/Album", "/source/track.mp3", externalQuery: null);

        Assert.False(result.Written);
        Assert.Empty(fetcher.Calls);
    }

    [Fact]
    public async Task TransientFetchFailureIsSurfaced()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/track.mp3"] = new("audio"),
            ["/dest/Album/track.mp3"] = new("audio"),
        });
        var fetcher = new StubExternalCoverArtFetcher
        {
            Result = new ExternalCoverArtFetchResult(null, HadTransientFailure: true),
        };
        var writer = CreateWriter(fs, fetcher);

        var result = await writer.WriteIfMissingAsync("/dest/Album", "/source/track.mp3", Query);

        Assert.False(result.Written);
        Assert.True(result.TransientFailure);
    }

    [Fact]
    public void SyncWriteIfMissingNeverFetchesExternally()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/track.mp3"] = new("audio"),
            ["/dest/Album/track.mp3"] = new("audio"),
        });
        var fetcher = new StubExternalCoverArtFetcher
        {
            Result = new ExternalCoverArtFetchResult(new FetchedCoverArt(Png, "image/png", "deezer"), false),
        };
        var writer = CreateWriter(fs, fetcher);

        Assert.False(writer.WriteIfMissing("/dest/Album", "/source/track.mp3"));
        Assert.Empty(fetcher.Calls);
    }

    private static AlbumCoverWriter CreateWriter(
        MockFileSystem fs,
        StubExternalCoverArtFetcher fetcher,
        Action<MusicEnricherOptions>? configure = null,
        EmbeddedPicture? embedded = null)
    {
        var options = new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
        };
        configure?.Invoke(options);

        return new AlbumCoverWriter(
            fs,
            new CoverArtResolver(fs, new FixedEmbeddedPictureReader(embedded)),
            fetcher,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<AlbumCoverWriter>.Instance);
    }

    private sealed class FixedEmbeddedPictureReader(EmbeddedPicture? picture = null) : IEmbeddedPictureReader
    {
        public EmbeddedPicture? ReadFront(string filePath) => picture;
        public bool HasPicture(string filePath) => picture is not null;
    }
}
