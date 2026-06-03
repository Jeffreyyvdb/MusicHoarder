using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

public record GradingIssue(string Code, string Severity, string? Detail);

public record GradingResult(
    int Score,
    SongQualityVerdict Verdict,
    string? Summary,
    IReadOnlyList<GradingIssue> Issues);

/// <summary>
/// Builds the grading messages and parses the model's JSON reply. Versioned: bump
/// <see cref="Version"/> whenever the wording changes so stored grades stay comparable.
/// </summary>
public static class QualityGradingPrompt
{
    public const int Version = 2;

    private static readonly JsonSerializerOptions DossierJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string System =
        """
        You are a meticulous music-library quality auditor. The pipeline you are auditing scans
        audio files, fingerprints them, queries several metadata providers (AcoustID, MusicBrainz,
        Deezer, Spotify, Apple Music, community trackers), then picks a "winning" set of tags and a
        destination path. Your job is to judge whether the result it chose for ONE song is correct
        and high quality — NOT to re-identify the song yourself.

        You will be given a JSON dossier: the file path + embedded tags as found on disk, every
        provider attempt and the candidate it returned, the field-level change log, the final chosen
        metadata, and the destination path the file would be written to.

        GROUND TRUTH — read carefully before judging:
        - `currentMetadata` and `destinationPathPreview` are what the pipeline ACTUALLY chose and what
          will be written to disk. Judge THESE.
        - The `changeLog` records both applied and merely-proposed changes. An entry with
          `proposed: true` / `applied: false` was NOT applied — the pipeline deliberately declined to
          overwrite the existing tag. Do NOT treat a proposed-but-unapplied change as if it had been
          made, and do NOT raise `embedded_tags_overwritten` or `path_metadata_mismatch` from a
          proposed-only change. Declining to overwrite a good embedded tag from a weak source is the
          CORRECT behaviour, not a failure.

        UNRELEASED / COMMUNITY-TRACKER tracks: when `enrichment.isUnreleased` is true or the match
        came from a community tracker (a leak/unreleased catalog), the mainstream providers (Spotify,
        MusicBrainz, Deezer, Apple Music) legitimately CANNOT corroborate — the song isn't in their
        catalogs by definition. Do NOT grade such a result "wrong" merely for being single-sourced or
        lacking mainstream matches. Judge it on internal consistency and plausibility instead — and
        note that a file named with a working/alternate title (leaks routinely circulate under
        working titles) matching a tracker's canonical title is normal and expected, not invented.

        Grade how trustworthy the FINAL chosen metadata + destination are, given the evidence:

        - Excellent (90-100): correct and corroborated by ≥2 independent providers (or strong IDs
          like a matching ISRC/MBID), complete (artist, title, album, year), and the destination
          path matches the chosen metadata.
        - Good (70-89): correct and consistent, but thinly sourced or missing minor fields.
        - Questionable (40-69): plausible but unverified, internally inconsistent, or a single
          low-confidence source; a human should check.
        - Wrong (1-39): the chosen metadata or destination contradicts the source file's own tags
          or filename, OR no provider matched yet the song was still given a confident-looking
          identity (artist/title/album that look invented or borrowed from an unrelated track).
        - Ungradeable (0): genuinely no information to judge.

        Pay special attention to the failure this audit exists to catch: a file where every provider
        returned "no match" but the final metadata and destination path nonetheless name a specific,
        unrelated song/artist/album. That is "wrong" with an "unsupported_identity" issue.

        Reply with ONLY a JSON object, no prose, no code fences:
        {
          "score": <integer 0-100>,
          "verdict": "excellent" | "good" | "questionable" | "wrong" | "ungradeable",
          "summary": "<one sentence, plain English>",
          "issues": [ { "code": "<snake_case>", "severity": "low"|"medium"|"high", "detail": "<short>" } ]
        }

        Useful issue codes (use these where they apply, add others as needed):
        unsupported_identity, artist_changed, title_changed, album_changed, no_provider_match,
        low_confidence, single_source, duration_mismatch, path_metadata_mismatch,
        missing_year, missing_album, embedded_tags_overwritten, looks_correct.
        """;

    public static IReadOnlyList<ChatMessage> BuildMessages(SongGradingDossier dossier)
    {
        var json = JsonSerializer.Serialize(dossier, DossierJson);
        return
        [
            new ChatMessage("system", System),
            new ChatMessage("user", $"Grade this song's enrichment result.\n\nDOSSIER:\n{json}"),
        ];
    }

    /// <summary>Parses the model reply into a <see cref="GradingResult"/>, tolerating code fences and stray prose.</summary>
    public static GradingResult Parse(string content)
    {
        using var doc = ParseLenientObject(content);
        var root = doc.RootElement;

        var score = root.TryGetProperty("score", out var s) && s.TryGetInt32(out var sc)
            ? Math.Clamp(sc, 0, 100)
            : 0;

        var verdictRaw = root.TryGetProperty("verdict", out var v) ? v.GetString() : null;
        var verdict = ParseVerdict(verdictRaw, score);

        string? summary = root.TryGetProperty("summary", out var sum) ? sum.GetString() : null;
        if (summary is { Length: > 1024 }) summary = summary[..1024];

        var issues = new List<GradingIssue>();
        if (root.TryGetProperty("issues", out var iss) && iss.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in iss.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var code = item.TryGetProperty("code", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(code)) continue;
                var severity = item.TryGetProperty("severity", out var sev) ? sev.GetString() ?? "medium" : "medium";
                var detail = item.TryGetProperty("detail", out var d) ? d.GetString() : null;
                issues.Add(new GradingIssue(code!, severity, detail));
            }
        }

        return new GradingResult(score, verdict, summary, issues);
    }

    private static SongQualityVerdict ParseVerdict(string? raw, int score) => raw?.Trim().ToLowerInvariant() switch
    {
        "excellent" => SongQualityVerdict.Excellent,
        "good" => SongQualityVerdict.Good,
        "questionable" => SongQualityVerdict.Questionable,
        "wrong" => SongQualityVerdict.Wrong,
        "ungradeable" => SongQualityVerdict.Ungradeable,
        // Fall back to bucketing by score when the model omits/garbles the label.
        _ => score switch
        {
            >= 90 => SongQualityVerdict.Excellent,
            >= 70 => SongQualityVerdict.Good,
            >= 40 => SongQualityVerdict.Questionable,
            >= 1 => SongQualityVerdict.Wrong,
            _ => SongQualityVerdict.Ungradeable,
        },
    };

    /// <summary>
    /// Finds and parses the model's JSON object from a reply that may be wrapped in code fences or
    /// prose, and — crucially — may be <b>truncated</b> (the model ran out of output tokens, or a
    /// reasoning model's chain-of-thought was used as a fallback). Each <c>{</c> in the text is
    /// treated as a candidate object boundary, scanned with brace/bracket depth tracking that
    /// respects string literals (so a <c>}</c> inside a value can't fool it):
    /// <list type="bullet">
    /// <item>a <i>balanced</i> region that parses as valid JSON is returned — so trailing prose and
    /// stray <c>{ braces }</c> in reasoning text are skipped in favour of the real object;</item>
    /// <item>a truncated tail is salvaged by rewinding to the last point where a value or container
    /// had completed and appending the missing closers, yielding a valid object holding whatever
    /// fields finished (score/verdict/summary + any complete issues). The tolerant field reader in
    /// <see cref="Parse"/> turns that into a usable grade instead of a hard failure.</item>
    /// </list>
    /// If no candidate parses, the last <see cref="JsonException"/> is rethrown so the caller records
    /// a clean failure.
    /// </summary>
    private static JsonDocument ParseLenientObject(string content)
    {
        JsonException? lastError = null;
        var searchFrom = 0;

        while (true)
        {
            var start = content.IndexOf('{', searchFrom);
            if (start < 0) break;

            var candidate = ExtractCandidate(content, start);
            try
            {
                return JsonDocument.Parse(candidate);
            }
            catch (JsonException ex)
            {
                // This region wasn't valid JSON (e.g. prose braces) — try the next '{'.
                lastError = ex;
                searchFrom = start + 1;
            }
        }

        throw lastError ?? new JsonException("No JSON object found in model reply.");
    }

    /// <summary>
    /// Returns the JSON-object text starting at <paramref name="start"/>: the balanced region if the
    /// braces close, otherwise the truncation-salvaged prefix (closers appended). See
    /// <see cref="ParseLenientObject"/> for how candidates are validated.
    /// </summary>
    private static string ExtractCandidate(string content, int start)
    {
        var stack = new Stack<char>();          // open containers: '{' or '['
        var inString = false;
        var escaped = false;
        var expectingValue = true;              // the object itself is the awaited value

        // Last index (exclusive) at which the prefix could be validly closed, plus the closers
        // needed there. Only set after a *completed value* / container open / container close —
        // never after a key, ':' or ',' — so the salvaged prefix never ends on a dangling token.
        var safeEnd = -1;
        var safeClosers = string.Empty;

        void MarkSafe(int idxExclusive)
        {
            safeEnd = idxExclusive;
            var sb = new StringBuilder(stack.Count);
            foreach (var open in stack) // Stack enumerates innermost-first → correct close order
                sb.Append(open == '{' ? '}' : ']');
            safeClosers = sb.ToString();
        }

        for (var i = start; i < content.Length; i++)
        {
            var ch = content[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"')
                {
                    inString = false;
                    var inObject = stack.Count > 0 && stack.Peek() == '{';
                    if (!inObject || expectingValue) MarkSafe(i + 1); // a value string just completed
                    expectingValue = false;
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    stack.Push(ch);
                    MarkSafe(i + 1);                 // an empty container is a valid stop
                    expectingValue = ch == '[';      // arrays expect a value next; objects a key
                    break;
                case '}':
                case ']':
                    if (stack.Count > 0) stack.Pop();
                    MarkSafe(i + 1);                 // the container is itself a completed value
                    expectingValue = false;
                    if (stack.Count == 0)
                        return content[start..(i + 1)]; // balanced top-level object
                    break;
                case ':':
                    expectingValue = true;
                    break;
                case ',':
                    expectingValue = stack.Count > 0 && stack.Peek() == '['; // array elem vs object key
                    break;
                default:
                    if (!char.IsWhiteSpace(ch))
                    {
                        // number / true / false / null — consume the whole token.
                        var j = i;
                        while (j < content.Length && IsLiteralChar(content[j])) j++;
                        if (j < content.Length) MarkSafe(j); // token complete (a delimiter follows)
                        expectingValue = false;
                        i = j - 1;                            // for-loop ++ resumes at the delimiter
                    }
                    break;
            }
        }

        // End of input with the object still open → truncated. Salvage to the last safe point.
        if (safeEnd > start)
            return content[start..safeEnd] + safeClosers;

        return content[start..]; // nothing completed — let the caller's parse surface the error
    }

    private static bool IsLiteralChar(char c) =>
        char.IsLetterOrDigit(c) || c is '+' or '-' or '.';
}
