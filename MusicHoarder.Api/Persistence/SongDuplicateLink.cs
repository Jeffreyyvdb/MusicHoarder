namespace MusicHoarder.Api.Persistence;

/// <summary>How a pair of songs came to be considered duplicate candidates. Flags accumulate:
/// a pair can block on metadata AND share an AcoustID AND be confirmed by fingerprint similarity.</summary>
[Flags]
public enum DuplicateMatchReason
{
    None = 0,

    /// <summary>Byte-identical compressed Chromaprint strings — the strongest possible signal.</summary>
    ExactFingerprint = 1,

    /// <summary>Decoded Chromaprint frames matched above the confirm threshold (same recording,
    /// different encode).</summary>
    FingerprintSimilarity = 2,

    /// <summary>Both songs resolved to the same AcoustID track id during enrichment.</summary>
    AcoustIdTrack = 4,

    /// <summary>Both songs carry the same ISRC. Candidate-only: dirty tags share ISRCs, so this
    /// never confirms a pair on its own.</summary>
    Isrc = 8,

    /// <summary>Normalized primary-artist + title matched with durations within tolerance.</summary>
    Metadata = 16,
}

public enum DuplicateConfidence
{
    /// <summary>Metadata agrees but the audio couldn't be (or wasn't conclusively) confirmed.
    /// Surfaced in the UI only — never sets <see cref="SongMetadata.IsDuplicate"/>, so the
    /// build/heal/grading pipelines are unaffected.</summary>
    Suspected = 0,

    /// <summary>Acoustically confirmed (exact or similar fingerprint). Projected onto
    /// <see cref="SongMetadata.IsDuplicate"/> via cluster election.</summary>
    Confirmed = 1,
}

public enum DuplicateLinkStatus
{
    Active = 0,

    /// <summary>The user marked this pair "not duplicates". Dismissed links persist forever and are
    /// excluded from clustering, so detection re-runs can never resurrect a rejected pairing.</summary>
    Dismissed = 1,
}

/// <summary>
/// A detected pairwise duplicate relationship between two songs of one owner. Pairs are stored in
/// canonical order (<see cref="SongIdLow"/> &lt; <see cref="SongIdHigh"/>) so re-runs upsert rather
/// than duplicate. Groups are derived at read time by union-find over Active links — there is no
/// group entity to keep consistent.
/// </summary>
public class SongDuplicateLink
{
    public int Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public int SongIdLow { get; set; }
    public int SongIdHigh { get; set; }
    public DuplicateMatchReason Reasons { get; set; }

    /// <summary>Chromaprint similarity in [0,1] when both fingerprints decoded; null otherwise.</summary>
    public double? Similarity { get; set; }

    public DuplicateConfidence Confidence { get; set; }
    public DuplicateLinkStatus Status { get; set; }
    public DateTime DetectedAtUtc { get; set; }
    public DateTime? DismissedAtUtc { get; set; }
}
