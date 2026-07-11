using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Soulseek;

/// <summary>
/// Pure candidate election over Soulseek search responses: filter out unusable files, then rank the
/// survivors best-first. No IO — fully unit-testable. An empty result means "nothing acceptable on
/// the network right now"; the download provider reports NotFound so the wishlist chain can fall
/// through to the next provider.
/// </summary>
public static class SlskdCandidateSelector
{
    /// <summary>
    /// <paramref name="durationMs"/> is the wanted track's duration (0/unknown skips the check).
    /// <paramref name="title"/> filters by token presence in the remote path so an album-wide search
    /// still selects the right track.
    /// </summary>
    public static IReadOnlyList<SlskdCandidate> Select(
        IReadOnlyList<SlskdSearchResponse> responses,
        string title,
        int durationMs,
        SlskdOptions options)
    {
        var allowed = new HashSet<string>(
            options.AllowedExtensions.Select(e => e.ToLowerInvariant()), StringComparer.Ordinal);
        var titleTokens = TitleNormalizer.NormalizeForSearch(title)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wantedSeconds = durationMs > 0 ? durationMs / 1000 : (int?)null;

        var candidates = new List<SlskdCandidate>();
        foreach (var response in responses)
        {
            if (response.Files is null)
                continue;
            foreach (var file in response.Files)
            {
                if (file.IsLocked || file.Size <= 0)
                    continue;

                var ext = file.NormalizedExtension;
                if (!allowed.Contains(ext))
                    continue;

                var tier = AudioQuality.TierFor(ext);
                if (tier == AudioCodecTier.Lossy && (file.BitRate ?? 0) < options.MinBitrateLossy)
                    continue;

                // Duration sanity: only enforced when both sides advertise one — plenty of peers
                // don't send length metadata, and dropping those entirely would starve results.
                if (wantedSeconds is { } wanted && file.Length is { } length
                    && Math.Abs(length - wanted) > options.DurationToleranceSeconds)
                    continue;

                if (!ContainsAllTokens(file.Filename, titleTokens))
                    continue;

                candidates.Add(new SlskdCandidate(
                    response.Username, file,
                    response.HasFreeUploadSlot, response.QueueLength, response.UploadSpeed));
            }
        }

        return candidates
            .OrderByDescending(c => RankScore(c, options))
            .ThenByDescending(c => c.HasFreeUploadSlot)
            .ThenBy(c => c.QueueLength)
            .ThenByDescending(c => c.UploadSpeed)
            .ThenByDescending(c => c.File.Size)
            .ToList();
    }

    /// <summary>
    /// Quality-first rank. With <see cref="SlskdOptions.PreferLossless"/> off, tiers collapse so
    /// only advertised bitrate differentiates (a user who wants the smaller lossy file can opt out
    /// of FLAC-hunting).
    /// </summary>
    private static long RankScore(SlskdCandidate c, SlskdOptions options)
    {
        var ext = c.File.NormalizedExtension;
        return options.PreferLossless
            ? AudioQuality.Score(ext, c.File.BitRate)
            : Math.Clamp(c.File.BitRate ?? 0, 0, 99_999);
    }

    /// <summary>Every normalized title token must appear in the normalized remote path.</summary>
    internal static bool ContainsAllTokens(string remotePath, string[] tokens)
    {
        if (tokens.Length == 0)
            return true;
        var haystack = TitleNormalizer.NormalizeForSearch(remotePath.Replace('\\', ' ').Replace('/', ' '));
        foreach (var token in tokens)
        {
            if (!haystack.Contains(token, StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
