using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Scanner;

/// <summary>
/// What the audio evidence says about one candidate pair: the accumulated
/// <see cref="Reasons"/> (the blocking reasons, plus <see cref="DuplicateMatchReason.FingerprintSimilarity"/>
/// when decoded audio confirmed it), the resulting <see cref="Confidence"/>, and the decoded
/// <see cref="Similarity"/> (1.0 for an exact fingerprint, null when nothing could be decoded).
/// </summary>
public sealed record DuplicatePairVerdict(
    DuplicateMatchReason Reasons,
    DuplicateConfidence Confidence,
    double? Similarity);

/// <summary>
/// Stage two of duplicate detection: turns candidate pairs into verdicts from audio evidence. An
/// exact fingerprint is Confirmed outright. Otherwise both fingerprints are decoded (once per song
/// per run — the pairwise-compare cost control) and compared: at or above
/// <see cref="MusicEnricherOptions.DuplicateFingerprintMinSimilarity"/> the pair is Confirmed,
/// below <see cref="MusicEnricherOptions.DuplicateFingerprintRejectSimilarity"/> it is dropped
/// (decodable fingerprints that strongly disagree are affirmative evidence of different
/// recordings), and in between — or with no decodable audio at all — it is Suspected, which
/// surfaces in the UI but never sets <see cref="SongMetadata.IsDuplicate"/>.
/// </summary>
public sealed class DuplicatePairConfirmer(IFingerprintSimilarityGate fingerprintGate)
{
    public Dictionary<SongIdPair, DuplicatePairVerdict> Confirm(
        IReadOnlyDictionary<SongIdPair, DuplicateMatchReason> candidates,
        IReadOnlyDictionary<int, SongMetadata> songsById,
        MusicEnricherOptions opts)
    {
        // Decode each fingerprint at most once per run — the pairwise-compare cost control.
        var decodeCache = new Dictionary<int, uint[]?>();
        uint[]? Frames(SongMetadata song)
        {
            if (!decodeCache.TryGetValue(song.Id, out var frames))
            {
                frames = fingerprintGate.TryDecode(song.Fingerprint, out var decoded) ? decoded : null;
                decodeCache[song.Id] = frames;
            }
            return frames;
        }

        var verdicts = new Dictionary<SongIdPair, DuplicatePairVerdict>();

        foreach (var (pair, reasons) in candidates)
        {
            if (reasons.HasFlag(DuplicateMatchReason.ExactFingerprint))
            {
                verdicts[pair] = new DuplicatePairVerdict(reasons, DuplicateConfidence.Confirmed, 1.0);
                continue;
            }

            var framesA = Frames(songsById[pair.Low]);
            var framesB = Frames(songsById[pair.High]);

            if (framesA is not null && framesB is not null)
            {
                var similarity = fingerprintGate.Similarity(framesA, framesB);
                if (similarity >= opts.DuplicateFingerprintMinSimilarity)
                {
                    verdicts[pair] = new DuplicatePairVerdict(
                        reasons | DuplicateMatchReason.FingerprintSimilarity, DuplicateConfidence.Confirmed, similarity);
                }
                else if (similarity < opts.DuplicateFingerprintRejectSimilarity)
                {
                    // Decodable fingerprints that strongly disagree are affirmative evidence of
                    // different recordings — don't surface the pair at all.
                }
                else
                {
                    verdicts[pair] = new DuplicatePairVerdict(reasons, DuplicateConfidence.Suspected, similarity);
                }
            }
            else
            {
                // No audio evidence available: metadata/identifier agreement alone is never enough
                // to auto-flag, but it's worth a human look.
                verdicts[pair] = new DuplicatePairVerdict(reasons, DuplicateConfidence.Suspected, null);
            }
        }

        return verdicts;
    }
}
