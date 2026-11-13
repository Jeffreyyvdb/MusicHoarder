using System.Text.Json;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// Maps a grading failure to the machine-readable code a grading run records and the UI acts on
/// (e.g. the "out of credits" banner). One mapping for the song grader, the album grader and the
/// grader trial, so the trial's arms report failures in the same words.
/// </summary>
public static class GradingErrorCode
{
    public static string Of(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.PaymentRequired } => "out_of_credits",
        HttpRequestException => "http_error",
        JsonException => "bad_response",
        InvalidOperationException => "empty_response",
        _ => "error",
    };
}
