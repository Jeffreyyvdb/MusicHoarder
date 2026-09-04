using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Scanner;

/// <summary>
/// The verdict ladder in isolation: candidate pairs plus a scripted similarity gate in, verdicts out.
/// </summary>
public class DuplicatePairConfirmerTests
{
    private static readonly MusicEnricherOptions Opts = new()
    {
        DuplicateFingerprintMinSimilarity = 0.85,
        DuplicateFingerprintRejectSimilarity = 0.55,
    };

    [Fact]
    public void Confirm_ExactFingerprint_IsConfirmedAtFullSimilarity_WithoutDecoding()
    {
        var gate = new ScriptedGate(decodable: ["FP"], similarity: 0.10);
        var songs = Songs(Song(1, "FP"), Song(2, "FP"));

        var verdicts = Confirm(gate, songs, (new SongIdPair(1, 2), DuplicateMatchReason.ExactFingerprint));

        var verdict = Assert.Single(verdicts).Value;
        Assert.Equal(DuplicateConfidence.Confirmed, verdict.Confidence);
        Assert.Equal(1.0, verdict.Similarity);
        Assert.Equal(DuplicateMatchReason.ExactFingerprint, verdict.Reasons);
        Assert.Equal(0, gate.DecodeCalls);
    }

    [Fact]
    public void Confirm_SimilarityAtOrAboveMinimum_ConfirmsAndRecordsTheSimilarityReason()
    {
        var gate = new ScriptedGate(decodable: ["A", "B"], similarity: 0.85);

        var verdicts = Confirm(gate, Songs(Song(1, "A"), Song(2, "B")), (new SongIdPair(1, 2), DuplicateMatchReason.Metadata));

        var verdict = Assert.Single(verdicts).Value;
        Assert.Equal(DuplicateConfidence.Confirmed, verdict.Confidence);
        Assert.Equal(DuplicateMatchReason.Metadata | DuplicateMatchReason.FingerprintSimilarity, verdict.Reasons);
        Assert.Equal(0.85, verdict.Similarity);
    }

    [Fact]
    public void Confirm_SimilarityBelowReject_DropsThePairEntirely()
    {
        var gate = new ScriptedGate(decodable: ["A", "B"], similarity: 0.30);

        var verdicts = Confirm(gate, Songs(Song(1, "A"), Song(2, "B")), (new SongIdPair(1, 2), DuplicateMatchReason.Metadata));

        Assert.Empty(verdicts);
    }

    [Fact]
    public void Confirm_SimilarityBetweenThresholds_IsSuspectedWithTheScore()
    {
        var gate = new ScriptedGate(decodable: ["A", "B"], similarity: 0.70);

        var verdicts = Confirm(gate, Songs(Song(1, "A"), Song(2, "B")), (new SongIdPair(1, 2), DuplicateMatchReason.Isrc));

        var verdict = Assert.Single(verdicts).Value;
        Assert.Equal(DuplicateConfidence.Suspected, verdict.Confidence);
        Assert.Equal(DuplicateMatchReason.Isrc, verdict.Reasons);
        Assert.Equal(0.70, verdict.Similarity);
    }

    [Fact]
    public void Confirm_UndecodableFingerprint_IsSuspectedWithoutAScore()
    {
        // Only one side decodes, so there is no audio evidence either way.
        var gate = new ScriptedGate(decodable: ["A"], similarity: 0.99);

        var verdicts = Confirm(gate, Songs(Song(1, "A"), Song(2, null)), (new SongIdPair(1, 2), DuplicateMatchReason.Metadata));

        var verdict = Assert.Single(verdicts).Value;
        Assert.Equal(DuplicateConfidence.Suspected, verdict.Confidence);
        Assert.Equal(DuplicateMatchReason.Metadata, verdict.Reasons);
        Assert.Null(verdict.Similarity);
        Assert.Equal(0, gate.SimilarityCalls);
    }

    [Fact]
    public void Confirm_DecodesEachSongOnce_AcrossAllItsPairs()
    {
        // Three songs, three pairs: each fingerprint is decoded once, not once per pair.
        var gate = new ScriptedGate(decodable: ["A", "B", "C"], similarity: 0.90);
        var songs = Songs(Song(1, "A"), Song(2, "B"), Song(3, "C"));

        var verdicts = Confirm(
            gate, songs,
            (new SongIdPair(1, 2), DuplicateMatchReason.Metadata),
            (new SongIdPair(1, 3), DuplicateMatchReason.Metadata),
            (new SongIdPair(2, 3), DuplicateMatchReason.Metadata));

        Assert.Equal(3, verdicts.Count);
        Assert.Equal(3, gate.DecodeCalls);
        Assert.Equal(3, gate.SimilarityCalls);
    }

    private static Dictionary<SongIdPair, DuplicatePairVerdict> Confirm(
        IFingerprintSimilarityGate gate,
        Dictionary<int, SongMetadata> songs,
        params (SongIdPair Pair, DuplicateMatchReason Reasons)[] candidates)
        => new DuplicatePairConfirmer(gate).Confirm(
            candidates.ToDictionary(c => c.Pair, c => c.Reasons), songs, Opts);

    private static Dictionary<int, SongMetadata> Songs(params SongMetadata[] songs)
        => songs.ToDictionary(s => s.Id);

    private static SongMetadata Song(int id, string? fingerprint) => new()
    {
        Id = id,
        OwnerUserId = Guid.Empty,
        SourcePath = $"/music/{id}.mp3",
        FileName = $"{id}.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1_000_000,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Fingerprint = fingerprint,
    };

    /// <summary>Decodes only the listed fingerprint strings, returns one fixed similarity, and
    /// counts how often each was asked.</summary>
    private sealed class ScriptedGate(IEnumerable<string> decodable, double similarity) : IFingerprintSimilarityGate
    {
        private readonly HashSet<string> _decodable = [.. decodable];

        public int DecodeCalls { get; private set; }
        public int SimilarityCalls { get; private set; }

        public bool TryDecode(string? compressed, out uint[] frames)
        {
            DecodeCalls++;
            if (compressed is not null && _decodable.Contains(compressed))
            {
                frames = [1u];
                return true;
            }
            frames = [];
            return false;
        }

        public double Similarity(uint[] a, uint[] b)
        {
            SimilarityCalls++;
            return similarity;
        }
    }
}
