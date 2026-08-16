using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Download;

/// <summary>
/// Estimates the audio↔video sync offset for a <see cref="SongMusicVideo"/> whose video did not
/// supply the song's audio (manual fetch, slskd/spotiflac audio, or a quality upgrade that replaced
/// the file). Song side: the stored <see cref="SongMetadata.Fingerprint"/> (fpcalc's default ~120 s
/// window). Video side: a fresh fpcalc run over the mp4 with a wider window so the song's opening
/// still overlaps after a cinematic intro. The offset lands on the row as
/// <see cref="MusicVideoSyncSource.AutoAligned"/> when the bit-error minimum is trustworthy, else
/// <see cref="MusicVideoSyncSource.Unaligned"/> (offset 0; the UI's manual nudge is the fallback —
/// a radically different edit has no single true offset anyway).
/// </summary>
public class MusicVideoAlignmentService(
    IFpcalcService fpcalcService,
    IOptions<MusicEnricherOptions> options,
    ILogger<MusicVideoAlignmentService> logger)
{
    /// <summary>
    /// Mutates <paramref name="video"/> in place (no SaveChanges — the caller owns the unit of work).
    /// </summary>
    public async Task AlignAsync(SongMetadata song, SongMusicVideo video, CancellationToken ct)
    {
        var opts = options.Value;

        if (video.FilePath is null || !File.Exists(video.FilePath))
        {
            MarkUnaligned(video, "video file missing");
            return;
        }
        if (!ChromaprintComparer.TryDecode(song.Fingerprint, out var songFrames))
        {
            MarkUnaligned(video, "song has no decodable fingerprint");
            return;
        }

        // Wider window than the song's ~120 s so an intro up to MaxOffset still leaves the song's
        // opening inside the video fingerprint.
        var videoWindow = 120 + opts.MusicVideoAlignMaxOffsetSeconds;
        var outcome = await fpcalcService.GetFingerprintAsync(video.FilePath, videoWindow, ct);
        if (outcome.Result is null || !ChromaprintComparer.TryDecode(outcome.Result.Fingerprint, out var videoFrames))
        {
            MarkUnaligned(video, $"video fingerprint failed: {outcome.FailureReason ?? "undecodable"}");
            return;
        }

        // Measured frame rate beats the nominal constant: fpcalc's -length cap means duration and
        // frame count describe the same analyzed span.
        var msPerFrame = outcome.Result.DurationSeconds > 0 && videoFrames.Length > 0
            ? outcome.Result.DurationSeconds * 1000.0 / videoFrames.Length
            : 1000.0 / 8.08;
        var maxOffsetFrames = (int)Math.Ceiling(opts.MusicVideoAlignMaxOffsetSeconds * 1000.0 / msPerFrame);

        var result = VideoAudioAligner.EstimateOffset(songFrames, videoFrames, maxOffsetFrames);
        if (result is null || result.BitErrorRate > opts.MusicVideoAlignMaxBitErrorRate)
        {
            MarkUnaligned(video, result is null
                ? "insufficient fingerprint overlap"
                : $"low confidence (BER {result.BitErrorRate:F3})");
            return;
        }

        video.SyncOffsetMs = (int)Math.Round(result.OffsetFrames * msPerFrame);
        video.SyncSource = MusicVideoSyncSource.AutoAligned;
        video.SyncConfidence = 1 - result.BitErrorRate;
        logger.LogInformation(
            "Aligned music video for song {SongId}: offset {OffsetMs} ms (BER {Ber:F3})",
            song.Id, video.SyncOffsetMs, result.BitErrorRate);
    }

    private void MarkUnaligned(SongMusicVideo video, string reason)
    {
        video.SyncOffsetMs = 0;
        video.SyncSource = MusicVideoSyncSource.Unaligned;
        video.SyncConfidence = null;
        logger.LogInformation("Music video for song {SongId} left unaligned: {Reason}",
            video.SongId, LogSanitizer.ForLog(reason));
    }
}
