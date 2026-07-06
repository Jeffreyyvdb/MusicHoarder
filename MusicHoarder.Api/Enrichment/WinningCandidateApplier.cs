using System.Text.Json;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Promotes a song's winning provider candidate — recorded only on its matched
/// <see cref="SongProviderAttempt"/>'s <c>MatchedDataJson</c> — onto the song row itself.
///
/// The orchestrator deliberately stops writing a <see cref="EnrichmentStatus.NeedsReview"/>
/// candidate's metadata onto the song (it lives on the attempt instead), so the manual /
/// bulk-approval path must re-apply that candidate before flipping the row to
/// <see cref="EnrichmentStatus.Matched"/>. This is enrichment-domain logic, kept out of the HTTP
/// endpoint layer so it can be unit-tested without standing up an endpoint.
/// </summary>
public static class WinningCandidateApplier
{
    /// <summary>
    /// Applies the candidate recorded on the song's matched provider attempt and returns
    /// <c>true</c>; returns <c>false</c> — leaving the song untouched — when there is no usable
    /// candidate: no <see cref="SongMetadata.MatchedBy"/>, an unrecognized provider name, no
    /// matched attempt for that provider, or missing / corrupt candidate JSON.
    /// </summary>
    public static bool TryApply(SongMetadata song)
    {
        if (string.IsNullOrWhiteSpace(song.MatchedBy)) return false;

        var providerEnum = EnrichmentOrchestrator.MapProviderName(song.MatchedBy);
        if (providerEnum is null) return false;

        var attempt = song.ProviderAttempts.FirstOrDefault(a =>
            a.Provider == providerEnum.Value && a.Status == ProviderAttemptStatus.Matched);
        if (attempt is null || string.IsNullOrWhiteSpace(attempt.MatchedDataJson)) return false;

        EnrichmentProviderResult? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<EnrichmentProviderResult>(attempt.MatchedDataJson);
        }
        catch (JsonException)
        {
            return false;
        }
        if (candidate is null) return false;

        song.ApplyEnrichmentMatch(candidate.ToMatchData(EnrichmentStatus.Matched));
        return true;
    }
}
