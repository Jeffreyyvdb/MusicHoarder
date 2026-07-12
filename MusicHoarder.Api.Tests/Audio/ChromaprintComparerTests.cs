using MusicHoarder.Api.Audio;

namespace MusicHoarder.Api.Tests.Audio;

public class ChromaprintComparerTests
{
    // Real fpcalc 1.6.0 fixtures (see plan): a 13s source encoded two ways plus a different source.
    // A_128 and A_flac are the SAME recording (mp3 128 vs flac); B_flac is a different recording.
    private const string A_128 =
        "AQAAU4ulRFHUIH70Qs0zJcHXIXyG7UefZUvxjxiVTuHw8Af1K8aj4JZ0vAo-LkeY0lqQ7BlzfFmOJo3RjjxKZiEafcafQueEH4-oG88U4dlh4Uek6IGuG5aSM0V1F7nw4LsQn1AUJWoaVLrxMwgPJruY4RXGJRKc7PiRz1DFI86JE3sOPpaCOVF05NCP_ME6pQmaKbGigmGOHD_eR8EthNKio0m0NcKe43hcPLMqjEzQ6-Af_AiS4z-e4_iOP0d_HCCIMsgIYgBQQHgiDBMCGEaMQlQoIQhDDENiCEDVGWkcsIJZELAiZBgBhARSUFUZA1QZgBmQggiGGROGCQ4QUcACEQ";
    private const string A_flac =
        "AQAAU4ulRFHUIH70Qs0zJcHXIXwmbKfRZ9lS_CNGpVM4PPxB_YrxKLglHa-Cj8sRprQWJHvGHF-Wo0ljtCOPklmIRp_xp9A54ccj6sYzRXh2WPgRKXqg64al5ExR3UUuPPguxCcURYmaBpVu_AzCg8kuZniFcYkEJzt-5DNU8Yhz4sSeg4-lYE4UHTn0I3-wTmmCZkqsqGCYI8eP91FwC6G06GgSbY2w5zgeF8-sCiMT9Dr4Bz-C5PiP5zi-48_RHwcgiDJIEAOAAsITYZgQwDBiFKJCCUEYYhgSQwCqzkjjgBXMgoAVIcMIICSQgqrKGKDKAMyAFEQwzJgwTHCAiAIWiA";
    private const string B_flac =
        "AQAAU5ESSYmkRFISHId56DhyUMd3HEfOQ8R35DCOVDbOgsqHGxJO-PjxHY5I4mEWA4-P4yN8_EN6vKLRk0SWwydO6McP48cb6NrxgML_4BJxuPjxXYK-4QcPfTmMUyIs44dzFv8Fo99A9IL_ouPx4wdzg_pxHXJx4jmqQ3tWHHZqAGGdMlQwwywTDkhLgdXKcAeEIR4Z4QwQADjCgDMCGUIFAYZSgYSwgAtjJQQAAEaUUNQZAARF1BkAAAA";

    // First 10 frames of `fpcalc -raw A_128.mp3` (83 total) — ground truth for the decoder.
    private static readonly uint[] A_128_RawHead =
    [
        817293836, 297241165, 308578959, 304923279, 304611998,
        371786426, 934949546, 899560362, 1015312622, 2088794158,
    ];

    [Fact]
    public void TryDecode_MatchesFpcalcRawOutput()
    {
        Assert.True(ChromaprintComparer.TryDecode(A_128, out var frames));
        Assert.Equal(83, frames.Length);
        Assert.Equal(A_128_RawHead, frames[..10]);
    }

    [Fact]
    public void Similarity_SameRecordingAcrossCodecs_IsHigh()
    {
        var sim = ChromaprintComparer.Similarity(A_128, A_flac);
        Assert.NotNull(sim);
        Assert.True(sim >= 0.75, $"expected same-recording similarity >= 0.75 but was {sim}");
    }

    [Fact]
    public void Similarity_DifferentRecording_IsLow()
    {
        var sim = ChromaprintComparer.Similarity(A_128, B_flac);
        Assert.NotNull(sim);
        Assert.True(sim < 0.75, $"expected different-recording similarity < 0.75 but was {sim}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-valid-base64-@@@")]
    [InlineData("AQ")] // decodes but < 4 bytes header
    public void Similarity_UndecodableInput_ReturnsNull(string? bad)
    {
        Assert.Null(ChromaprintComparer.Similarity(A_128, bad));
        Assert.Null(ChromaprintComparer.Similarity(bad, A_128));
    }
}
