namespace MusicHoarder.Api.Persistence;

/// <summary>What a transcription request was for. The numbers are stored, so they must never move.</summary>
public enum TranscriptionPurpose
{
    /// <summary>The background sweep listening to a short window of a song to check its LRC timing.</summary>
    TimingProbe = 0,

    /// <summary>The background sweep re-syncing a whole song a probe heard drifting.</summary>
    AutoResync = 1,

    /// <summary>Someone pressed the AI lyrics button.</summary>
    ManualTranscribe = 2,

    /// <summary>Someone asked for one song's timing to be verified.</summary>
    ManualTimingProbe = 3,
}

/// <summary>
/// One request's worth of audio sent to the transcription provider. The provider meters its quota in
/// audio-seconds over a rolling hour and day, and these rows are our copy of that meter. They live in the
/// database because the API restarts on every deploy, and a meter that forgot its spend on restart handed the
/// background sweeps a fresh day's allowance each time, until they had spent the whole quota and a manual
/// re-sync had nothing left.
/// </summary>
public class TranscriptionAudioSpend
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public double AudioSeconds { get; set; }
    public TranscriptionPurpose Purpose { get; set; }
}
