using Microsoft.Extensions.Options;

namespace MusicHoarder.Api.Options;

/// <summary>
/// Cross-field validation for <see cref="MusicEnricherOptions"/> that the per-property data annotations
/// can't express. Runs at startup via <c>ValidateOnStart()</c>, so a contradictory config fails fast
/// instead of silently doing nothing at runtime.
/// </summary>
public sealed class MusicEnricherOptionsValidator : IValidateOptions<MusicEnricherOptions>
{
    public ValidateOptionsResult Validate(string? name, MusicEnricherOptions o)
    {
        var errors = new List<string>();

        if (o.AutoDownloadWishlist && !o.EnableWishlistDownloads)
            errors.Add("MusicEnricher:AutoDownloadWishlist requires EnableWishlistDownloads = true (the auto-sweep can't run while the feature is off).");

        if (o.DownloadMaxSleepSeconds > 0 && o.DownloadMaxSleepSeconds < o.DownloadSleepSeconds)
            errors.Add("MusicEnricher:DownloadMaxSleepSeconds must be >= DownloadSleepSeconds (it's the upper bound of the randomized pre-download wait).");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Mode-aware validation for <see cref="SyncOptions"/>: the key-strength and per-role requirements
/// only apply when sync is actually on, so the default (Off, everything blank) always boots.
/// </summary>
public sealed class SyncOptionsValidator : IValidateOptions<SyncOptions>
{
    public ValidateOptionsResult Validate(string? name, SyncOptions o)
    {
        if (o.Mode == SyncMode.Off)
            return ValidateOptionsResult.Success;

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(o.ApiKey) || o.ApiKey.Length < 32)
            errors.Add("Sync:ApiKey must be at least 32 characters when sync is enabled — it is the only gate on an internet-facing endpoint. Generate one with `openssl rand -base64 48`.");

        if (o.Mode == SyncMode.Push && string.IsNullOrWhiteSpace(o.RemoteBaseUrl))
            errors.Add("Sync:RemoteBaseUrl is required in Push mode (the receiving instance's public HTTPS origin).");

        if (o.Mode == SyncMode.Receive && string.IsNullOrWhiteSpace(o.SyncedSourceDirectory))
            errors.Add("Sync:SyncedSourceDirectory is required in Receive mode (where received files are written).");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Validation for <see cref="NavidromeOptions"/>: the like sync is all-or-nothing on credentials —
/// a half-configured connection (URL but no password, etc.) is almost always a mistake, so fail fast
/// rather than silently never syncing. Fully blank (feature off) always boots.
/// </summary>
public sealed class NavidromeOptionsValidator : IValidateOptions<NavidromeOptions>
{
    public ValidateOptionsResult Validate(string? name, NavidromeOptions o)
    {
        var anySet = !string.IsNullOrWhiteSpace(o.BaseUrl)
            || !string.IsNullOrWhiteSpace(o.Username)
            || !string.IsNullOrWhiteSpace(o.Password);
        if (!anySet)
            return ValidateOptionsResult.Success;

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(o.BaseUrl))
            errors.Add("Navidrome:BaseUrl is required when a Navidrome username/password is set.");
        else if (!Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _))
            errors.Add("Navidrome:BaseUrl must be an absolute URL, e.g. http://navidrome:4533.");
        if (string.IsNullOrWhiteSpace(o.Username))
            errors.Add("Navidrome:Username is required when a Navidrome connection is configured.");
        if (string.IsNullOrWhiteSpace(o.Password))
            errors.Add("Navidrome:Password is required when a Navidrome connection is configured.");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Cross-field validation for <see cref="QualityGradingOptions"/>: the reasoning-token budget must leave
/// room for the JSON answer within the output-token budget, or responses get truncated mid-object.
/// </summary>
public sealed class QualityGradingOptionsValidator : IValidateOptions<QualityGradingOptions>
{
    public ValidateOptionsResult Validate(string? name, QualityGradingOptions o)
    {
        if (o.ReasoningMaxTokens > 0 && o.ReasoningMaxTokens >= o.MaxOutputTokens)
            return ValidateOptionsResult.Fail(
                "QualityGrading:ReasoningMaxTokens must be less than MaxOutputTokens so the JSON answer has room after the chain-of-thought.");

        return ValidateOptionsResult.Success;
    }
}
