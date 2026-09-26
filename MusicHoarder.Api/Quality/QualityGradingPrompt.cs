using System.Diagnostics.CodeAnalysis;
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

    /// <summary>
    /// Serializes the dossier exactly as it is sent to the model. Shared with
    /// <see cref="QualityDossierFactory"/> so its size guard measures the real payload.
    /// </summary>
    public static string SerializeDossier(SongGradingDossier dossier) =>
        JsonSerializer.Serialize(dossier, DossierJson);

    public static IReadOnlyList<ChatMessage> BuildMessages(SongGradingDossier dossier)
    {
        var json = SerializeDossier(dossier);
        return
        [
            new ChatMessage("system", System),
            new ChatMessage("user", $"Grade this song's enrichment result.\n\nDOSSIER:\n{json}"),
        ];
    }

    /// <summary>
    /// Parses the model reply into a <see cref="GradingResult"/>, tolerating code fences and stray prose.
    /// Throws <see cref="JsonException"/> when the reply holds no grade (see <see cref="TryParse"/>), so
    /// the caller records a failure and retries it instead of persisting an empty reply as a verdict.
    /// </summary>
    public static GradingResult Parse(string content) =>
        FindGrade(content, depth: 0, out var error) ?? throw error!;

    /// <summary>
    /// <see cref="Parse"/> without the exception: <c>false</c> when the reply holds no grade. A reply
    /// holds a grade when some JSON object in it has an integer <c>score</c> or a recognised
    /// <c>verdict</c> word — an object that merely parses (<c>{ }</c>, or a reasoning model's leaked
    /// <c>{"analysis": "..."}</c> channel) is not one.
    /// </summary>
    public static bool TryParse(string? content, [NotNullWhen(true)] out GradingResult? result)
    {
        result = string.IsNullOrWhiteSpace(content) ? null : FindGrade(content, depth: 0, out _);
        return result is not null;
    }

    // A reply is capped by MaxOutputTokens (~16 KB), so this only bounds a pathological one.
    private const int MaxCandidates = 256;

    // How many times a harmony-style "final" channel is unwrapped (its string may wrap another).
    private const int MaxFinalDepth = 2;

    /// <summary>
    /// Tries each <c>{</c> in the reply as a candidate object (see <see cref="ExtractCandidate"/>) and
    /// returns the first grade found. A candidate that parses but holds no grade does not end the
    /// search: a reasoning model (gpt-oss) leaks its channels as
    /// <c>{"analysis": "...", "final": "{\"score\": 95, ...}"}</c>, or replies with the analysis alone,
    /// and the real grade — when there is one — sits under <c>final</c> or in a later object. The search
    /// resumes <i>after</i> such an object, never inside it: its children (an issue, a nested draft) are
    /// not the model's answer, and reading one as the grade would persist a verdict nobody gave. When
    /// nothing holds a grade, <paramref name="error"/> says why, for the caller's failure record.
    /// </summary>
    private static GradingResult? FindGrade(string content, int depth, out JsonException? error)
    {
        JsonException? lastParseError = null;
        var sawObject = false;
        var searchFrom = 0;

        for (var tried = 0; tried < MaxCandidates; tried++)
        {
            var start = content.IndexOf('{', searchFrom);
            if (start < 0) break;
            searchFrom = start + 1;

            JsonDocument doc;
            int end;
            try
            {
                doc = JsonDocument.Parse(ExtractCandidate(content, start, out end));
            }
            catch (JsonException ex)
            {
                // This region wasn't valid JSON (e.g. prose braces) — try the next '{'.
                lastParseError = ex;
                continue;
            }

            using (doc)
            {
                sawObject = true;
                if (ReadGrade(doc.RootElement, depth) is { } grade)
                {
                    error = null;
                    return grade;
                }
            }

            searchFrom = end; // a whole object without a grade: skip its children
        }

        error = sawObject
            ? new JsonException("Model reply held no grade (no score or recognised verdict)")
            : lastParseError ?? new JsonException("No JSON object found in model reply.");
        return null;
    }

    /// <summary>
    /// Reads the grade from one parsed object, or <c>null</c> when it holds none (no integer
    /// <c>score</c> and no recognised <c>verdict</c>). Such an object falls back to its <c>final</c>
    /// channel: an object is read directly, a string is parsed leniently in turn.
    /// </summary>
    private static GradingResult? ReadGrade(JsonElement root, int depth)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        int? score = root.TryGetProperty("score", out var s)
            && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sc)
            ? Math.Clamp(sc, 0, 100)
            : null;
        var verdictWord = ParseVerdictWord(StringProperty(root, "verdict"));

        if (score is null && verdictWord is null)
        {
            if (depth >= MaxFinalDepth || !root.TryGetProperty("final", out var final))
                return null;
            return final.ValueKind switch
            {
                JsonValueKind.Object => ReadGrade(final, depth + 1),
                JsonValueKind.String => FindGrade(final.GetString()!, depth + 1, out _),
                _ => null,
            };
        }

        // Fall back to bucketing by score when the model omits/garbles the label.
        var verdict = verdictWord ?? VerdictForScore(score!.Value);

        var summary = StringProperty(root, "summary");
        if (summary is { Length: > 1024 }) summary = summary[..1024];

        var issues = new List<GradingIssue>();
        if (root.TryGetProperty("issues", out var iss) && iss.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in iss.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var code = StringProperty(item, "code");
                if (string.IsNullOrWhiteSpace(code)) continue;
                var severity = StringProperty(item, "severity") ?? "medium";
                var detail = StringProperty(item, "detail");
                issues.Add(new GradingIssue(code, severity, detail));
            }
        }

        return new GradingResult(score ?? 0, verdict, summary, issues);
    }

    /// <summary>The property's value when it is a JSON string; <c>null</c> when absent or of another kind.</summary>
    private static string? StringProperty(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static SongQualityVerdict? ParseVerdictWord(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "excellent" => SongQualityVerdict.Excellent,
        "good" => SongQualityVerdict.Good,
        "questionable" => SongQualityVerdict.Questionable,
        "wrong" => SongQualityVerdict.Wrong,
        "ungradeable" => SongQualityVerdict.Ungradeable,
        _ => null,
    };

    private static SongQualityVerdict VerdictForScore(int score) => score switch
    {
        >= 90 => SongQualityVerdict.Excellent,
        >= 70 => SongQualityVerdict.Good,
        >= 40 => SongQualityVerdict.Questionable,
        >= 1 => SongQualityVerdict.Wrong,
        _ => SongQualityVerdict.Ungradeable,
    };

    /// <summary>
    /// Returns the JSON-object text starting at <paramref name="start"/>, from a reply that may be
    /// wrapped in code fences or prose and — crucially — may be <b>truncated</b> (the model ran out of
    /// output tokens, or a reasoning model's chain-of-thought was used as a fallback). The scan tracks
    /// brace/bracket depth and respects string literals (so a <c>}</c> inside a value can't fool it):
    /// <list type="bullet">
    /// <item>a <i>balanced</i> region is returned whole — so trailing prose and stray
    /// <c>{ braces }</c> in reasoning text are skipped in favour of the real object;</item>
    /// <item>a truncated tail is salvaged by rewinding to the last point where a value or container
    /// had completed and appending the missing closers, yielding a valid object holding whatever
    /// fields finished (score/verdict/summary + any complete issues). The tolerant field reader in
    /// <see cref="ReadGrade"/> turns that into a usable grade instead of a hard failure;</item>
    /// <item>a character that cannot appear in JSON outside a string (a single quote, <c>&lt;</c>, a
    /// backslash…) ends the scan: the region up to it is returned, which can never parse, so
    /// <see cref="FindGrade"/> moves on to the next <c>{</c>.</item>
    /// </list>
    /// <paramref name="end"/> is the index just past the scanned region (the end of the reply for a
    /// truncated one), where <see cref="FindGrade"/> resumes after a whole object that held no grade.
    /// </summary>
    private static string ExtractCandidate(string content, int start, out int end)
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
                    {
                        end = i + 1;
                        return content[start..end]; // balanced top-level object
                    }
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
                        // A stray character (a single quote, '<', a backslash…) can't be JSON here.
                        // Hand back the region up to it — its parse is certain to fail, so the caller
                        // moves on — rather than consume a zero-length token below and spin on it.
                        if (!IsLiteralChar(ch))
                        {
                            end = i + 1;
                            return content[start..end];
                        }

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
        end = content.Length;
        if (safeEnd > start)
            return content[start..safeEnd] + safeClosers;

        return content[start..]; // nothing completed — let the caller's parse surface the error
    }

    private static bool IsLiteralChar(char c) =>
        char.IsLetterOrDigit(c) || c is '+' or '-' or '.';
}
