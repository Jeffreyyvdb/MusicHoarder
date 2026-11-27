using System.Text.Json;
using System.Text.Json.Serialization;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.CleanEdits;

/// <summary>
/// The song as the check needs it. <see cref="RequestedDurationSeconds"/> is the length of the track that
/// was asked for, when it was a download: a YouTube copy can be a cut of another length, and the
/// catalogs list the release.
/// </summary>
internal sealed record CleanEditSubject(string Artist, string Title, string? Isrc, double? RequestedDurationSeconds, string Path);

/// <summary>One preview the verdict rests on, and where it sits in the file.</summary>
internal sealed record ReferenceUsed(Edition Edition, string Id, double OffsetSeconds, double Correlation);

/// <summary>What is stored with a check so a person can see why the file got its verdict.</summary>
internal sealed record CleanEditEvidence(
    IReadOnlyList<ReferenceUsed> References,
    string? ComparedPair,
    IReadOnlyList<double> MutedAtSeconds)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static CleanEditEvidence? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<CleanEditEvidence>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Why a check failed: the file could not be read, or the catalogs did not fully answer.</summary>
internal enum CheckFailure
{
    None,
    File,
    Catalog,
}

internal sealed record CleanEditOutcome(
    CleanEditVerdict Verdict,
    double? Margin,
    double? EditedSeconds,
    CleanEditEvidence? Evidence,
    CheckFailure Failure = CheckFailure.None)
{
    public static CleanEditOutcome UnreadableFile { get; } = new(CleanEditVerdict.Failed, null, null, null, CheckFailure.File);
    public static CleanEditOutcome CatalogFailed { get; } = new(CleanEditVerdict.Failed, null, null, null, CheckFailure.Catalog);
}

/// <summary>
/// Decides whether one built file is the explicit or the clean edition of its song, by comparing it
/// with the free previews of both editions. It reads the file and the catalogs and writes nothing.
/// </summary>
internal sealed class CleanEditChecker(
    IMonoDecoder decoder,
    IEditionReferenceClient references,
    ILogger<CleanEditChecker> logger)
{
    public async Task<CleanEditOutcome> CheckAsync(CleanEditSubject subject, CancellationToken ct)
    {
        if (await DecodeAsync(subject.Path, ct) is not { } decoded)
            return CleanEditOutcome.UnreadableFile;
        var (test, fileSeconds) = decoded;

        double[] durations = subject.RequestedDurationSeconds is { } requested ? [requested, fileSeconds] : [fileSeconds];
        var lookup = await references.FindAsync(new ReferenceQuery(subject.Artist, subject.Title, subject.Isrc, durations), ct);

        var located = new List<LocatedReference>();
        var unreadable = 0;
        foreach (var reference in lookup.References)
        {
            var preview = await LoadPreviewAsync(reference, ct);
            if (preview is null)
            {
                unreadable++;
                continue;
            }
            var alignment = EditionAligner.Locate(test, preview);
            if (alignment is { } found && found.Correlation >= EditionAligner.MinimumCorrelation)
                located.Add(new LocatedReference(reference.Edition, reference.Id, preview, found.OffsetFrames, found.Correlation));
        }

        var analysis = CleanEditDecider.Analyze(test, located, [.. lookup.References.Select(r => r.Edition).Distinct()]);
        var missingData = lookup.Incomplete || unreadable > 0;
        if (missingData && analysis.Verdict is CleanEditVerdict.Inconclusive or CleanEditVerdict.NoCleanEdition or CleanEditVerdict.NoReference)
            return CleanEditOutcome.CatalogFailed;

        var evidence = new CleanEditEvidence(
            [.. located.Select(r => new ReferenceUsed(
                r.Edition, r.Id, Math.Round(r.OffsetFrames * BandSpectrogram.FrameSeconds, 1), Math.Round(r.Correlation, 2)))],
            analysis.Pairwise is { } pair ? $"{pair.ExplicitId}~{pair.CleanId}" : null,
            analysis.MutedWords?.SpanStartSeconds ?? []);
        return new CleanEditOutcome(analysis.Verdict, analysis.Pairwise?.Margin, analysis.Pairwise?.EditedSeconds, evidence);
    }

    /// <summary>
    /// The file's spectrogram and length. The samples stay in this method, so they are not held across
    /// the catalog calls that follow.
    /// </summary>
    private async Task<(BandSpectrogram Bands, double Seconds)?> DecodeAsync(string path, CancellationToken ct)
    {
        var pcm = await decoder.DecodeAsync(path, ct);
        if (pcm is null)
            return null;
        var bands = BandSpectrogram.FromPcm(pcm);
        return bands.Frames > 0 ? (bands, (double)pcm.Length / BandSpectrogram.SampleRate) : null;
    }

    private async Task<BandSpectrogram?> LoadPreviewAsync(EditionReference reference, CancellationToken ct)
    {
        var bytes = await references.DownloadPreviewAsync(reference.PreviewUrl, ct);
        if (bytes is null)
            return null;

        // ffmpeg needs a seekable file: an m4a preview can keep its index at the end.
        var file = Path.Combine(Path.GetTempPath(), $"mh-preview-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(file, bytes, ct);
            var pcm = await decoder.DecodeAsync(file, ct);
            if (pcm is null)
                return null;
            var bands = BandSpectrogram.FromPcm(pcm);
            return bands.Frames > 0 ? bands : null;
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not stage preview {Reference}", reference.Id);
            return null;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
