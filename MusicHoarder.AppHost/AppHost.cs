using System.Diagnostics;
using System.Text.RegularExpressions;

var builder = DistributedApplication.CreateBuilder(args);

// All deploy composes are generated from this one AppHost; DEPLOY_TARGET picks which shape `aspire
// publish` emits (swarm for musichoarder.app, compose for previews, selfhost for the published
// template). Defaults to swarm so a bare `aspire publish` keeps producing the prod compose.
var deployTarget = builder.Configuration["DEPLOY_TARGET"]?.ToLowerInvariant() switch
{
    "compose" => DeployTarget.Compose,
    "selfhost" => DeployTarget.SelfHost,
    _ => DeployTarget.Swarm,
};

builder.AddDockerComposeEnvironment("compose")
    // The Aspire dashboard ships only with the prod (swarm) stack; previews and the self-host template
    // are lean plain-compose stacks without it (and without the OTLP wiring Aspire injects alongside it).
    .WithProperties(env => env.DashboardEnabled = deployTarget == DeployTarget.Swarm)
    .ConfigureComposeFile(file => file.ConfigureMusicHoarderDeployment(deployTarget));

// GHCR registry so `aspire publish` emits ghcr.io image references and `aspire do push`
// builds + pushes there. Dokploy's Compose service pulls these prebuilt images.
#pragma warning disable ASPIRECOMPUTE003 // AddContainerRegistry/WithContainerRegistry are experimental.
var ghcr = builder.AddContainerRegistry("ghcr", "ghcr.io", "jeffreyyvdb/musichoarder");
#pragma warning restore ASPIRECOMPUTE003

var sourceDirectory = builder.AddParameter("source-directory")
    .WithDescription("Source music directory the scanner crawls (absolute path).");
var destinationDirectory = builder.AddParameter("destination-directory")
    .WithDescription("Destination library directory the LibraryBuilder writes into (absolute path).");
// Optional: a folder of real audio that seeds the demo account with playable songs (hosted demo only).
// Blank by default → the demo keeps its synthetic seed. Point at a small local folder to test locally.
var demoMediaDirectory = builder.AddParameter("demo-media-directory", builder.Configuration["Parameters:demo-media-directory"] ?? "")
    .WithDescription("Optional directory of real audio used to seed the demo account with playable songs. Blank keeps the synthetic demo seed.");
// These secrets are optional: the app degrades gracefully when blank. Default a missing value to
// empty so the AppHost boots without prompting (a configured user-secret / env value still wins).
var acoustIdApiKey = builder.AddParameter("acoustid-api-key", builder.Configuration["Parameters:acoustid-api-key"] ?? "", secret: true)
    .WithDescription("AcoustID API key (acoustid.org/api-key). Optional — disables the AcoustID provider when blank.");
var spotifyClientId = builder.AddParameter("spotify-client-id", builder.Configuration["Parameters:spotify-client-id"] ?? "", secret: true)
    .WithDescription("Spotify app Client ID. Optional — disables Spotify enrichment + OAuth when blank.");
var spotifyClientSecret = builder.AddParameter("spotify-client-secret", builder.Configuration["Parameters:spotify-client-secret"] ?? "", secret: true)
    .WithDescription("Spotify app Client Secret.");
var frontendPublicBaseUrl = builder.AddParameter("frontend-public-base-url")
    .WithDescription("Public HTTPS base URL of the frontend, used for Spotify OAuth redirect-back. Only consumed when publishing (e.g. the Dokploy domain); local dev uses the dynamic dev endpoint.");
// Spotify forbids per-env redirect URIs (no wildcards, no localhost), so every environment routes OAuth through one
// registered relay on the prod frontend. Local dev points at that prod relay verbatim; publish mode derives it from
// frontend-public-base-url. The signing key + return-origin allowlist guard the relay's browser bounce.
var spotifyOAuthRelayUrl = builder.AddParameter("spotify-oauth-relay-url", builder.Configuration["Parameters:spotify-oauth-relay-url"] ?? "")
    .WithDescription("Absolute URL of the single registered Spotify OAuth relay (e.g. https://<prod-frontend>/api/spotify/relay). Used verbatim as redirect_uri in local dev. Empty → falls back to request-derived redirect (offline dev).");
var spotifyOAuthStateKey = builder.AddParameter("spotify-oauth-state-key", builder.Configuration["Parameters:spotify-oauth-state-key"] ?? "", secret: true)
    .WithDescription("Shared HMAC key that signs the Spotify OAuth state. MUST be identical across local dev, the prod relay, and every PR preview. Empty → opaque unsigned state (offline dev only).");
var spotifyReturnOriginAllowlist = builder.AddParameter("spotify-return-origin-allowlist", builder.Configuration["Parameters:spotify-return-origin-allowlist"] ?? "https://localhost:* http://127.0.0.1:*")
    .WithDescription("Comma/space-separated origins the Spotify relay may bounce back to (prod origin, *.<preview-domain>, and loopback for local dev). Single '*' matches one host/port segment.");

// Umami analytics is read by the frontend at runtime via $env/dynamic/public, so these are plain
// (non-secret) runtime env vars on the frontend container. Default to empty so the AppHost boots and
// `aspire do push` resolves them without prompting; the tracker script only renders when both
// public-umami-src and public-umami-website-id are non-empty (filled in Dokploy).
var publicUmamiSrc = builder.AddParameter("public-umami-src", builder.Configuration["Parameters:public-umami-src"] ?? "")
    .WithDescription("Full Umami tracker URL ending in /script.js. Blank disables analytics.");
var publicUmamiWebsiteId = builder.AddParameter("public-umami-website-id", builder.Configuration["Parameters:public-umami-website-id"] ?? "")
    .WithDescription("Umami website id (GUID). Blank disables analytics.");
var publicUmamiRecorderSrc = builder.AddParameter("public-umami-recorder-src", builder.Configuration["Parameters:public-umami-recorder-src"] ?? "")
    .WithDescription("Optional Umami session-recorder URL ending in /recorder.js. Blank disables the recorder.");

// AI quality grading calls an OpenAI-compatible endpoint (OpenRouter by default). The key is the
// only required secret; with it blank the grader degrades to a no-op like the other providers.
var qualityGradingApiKey = builder.AddParameter("quality-grading-api-key", builder.Configuration["Parameters:quality-grading-api-key"] ?? "", secret: true)
    .WithDescription("API key for the OpenAI-compatible quality-grading endpoint (e.g. an OpenRouter key). Optional — disables AI grading when blank.");
var qualityGradingBaseUrl = builder.AddParameter("quality-grading-base-url", builder.Configuration["Parameters:quality-grading-base-url"] ?? "https://openrouter.ai/api/v1")
    .WithDescription("Base URL of the OpenAI-compatible chat-completions API used for grading.");
var qualityGradingModel = builder.AddParameter("quality-grading-model", builder.Configuration["Parameters:quality-grading-model"] ?? "deepseek/deepseek-v4-flash")
    .WithDescription("Cheap model id used for grading (provider namespace, e.g. deepseek/deepseek-v4-flash or google/gemini-2.0-flash-001).");

// Experimental AI lyrics transcription hits an OpenAI-compatible /audio/transcriptions endpoint
// (Groq Whisper recommended). With the key blank the whole feature is hidden. Provider-neutral param
// names so the deployed env var is LYRICS_TRANSCRIPTION_API_KEY (matches the self-host/preview compose).
var lyricsTranscriptionApiKey = builder.AddParameter("lyrics-transcription-api-key", builder.Configuration["Parameters:lyrics-transcription-api-key"] ?? "", secret: true)
    .WithDescription("API key for the OpenAI-compatible audio-transcriptions endpoint (Groq recommended). Blank → the AI lyrics feature is hidden.");
var lyricsTranscriptionBaseUrl = builder.AddParameter("lyrics-transcription-base-url", builder.Configuration["Parameters:lyrics-transcription-base-url"] ?? "https://api.groq.com/openai/v1")
    .WithDescription("Base URL of the transcription API (e.g. https://api.groq.com/openai/v1 for Groq, or https://api.openai.com/v1, or a self-hosted whisper).");
var lyricsTranscriptionModel = builder.AddParameter("lyrics-transcription-model", builder.Configuration["Parameters:lyrics-transcription-model"] ?? "whisper-large-v3")
    .WithDescription("Transcription model id. Must return verbose_json timestamps (e.g. whisper-large-v3 on Groq, or whisper-1 on OpenAI).");
// Fast, cheap LLM that segments AI-transcribed lyrics for songs with no LRCLIB lyrics. Called over the
// QualityGrading OpenRouter creds with reasoning off — keep it a low-latency non-reasoning model.
var lyricsTranscriptionLlmModel = builder.AddParameter("lyrics-transcription-llm-model", builder.Configuration["Parameters:lyrics-transcription-llm-model"] ?? "google/gemini-2.5-flash-lite")
    .WithDescription("LLM that segments AI-transcribed lyrics for no-lyrics songs (provider namespace, e.g. google/gemini-2.5-flash-lite).");

// Soulseek via a user-operated slskd instance (never provisioned here — the user runs slskd
// themselves; MusicHoarder only needs to reach its REST API and read its completed-downloads dir).
// All blank → the integration is off and the "slskd" download provider reports NotFound.
var slskdBaseUrl = builder.AddParameter("slskd-base-url", builder.Configuration["Parameters:slskd-base-url"] ?? "")
    .WithDescription("Base URL of a user-operated slskd instance (e.g. http://localhost:5030). Blank → Soulseek integration off.");
var slskdApiKey = builder.AddParameter("slskd-api-key", builder.Configuration["Parameters:slskd-api-key"] ?? "", secret: true)
    .WithDescription("slskd API key (web.authentication.api_keys), sent as X-API-Key.");
var slskdDownloadsDirectory = builder.AddParameter("slskd-downloads-directory", builder.Configuration["Parameters:slskd-downloads-directory"] ?? "")
    .WithDescription("slskd's completed-downloads directory as seen from the API process (in dev: the host path slskd writes to).");

// Optional streaming-FLAC acquisition sidecar (the "spotiflac" download provider). The sidecar is a
// separate, self-hosted, off-by-default service (never provisioned here); MusicHoarder only calls its
// HTTP contract. Blank → the integration is off and the "spotiflac" provider reports NotFound.
var streamingFlacSidecarUrl = builder.AddParameter("streaming-flac-sidecar-url", builder.Configuration["Parameters:streaming-flac-sidecar-url"] ?? "")
    .WithDescription("Base URL of a self-hosted streaming-FLAC acquisition sidecar (e.g. http://spotiflac:8000). Blank → integration off.");

var ownerEmail = builder.AddParameter("owner-email")
    .WithDescription("Email of the owner (admin) account. Used by magic-link sign-in.");
var demoUserEmail = builder.AddParameter("demo-user-email")
    .WithDescription("Email of the demo (read-only) account. Defaults to demo@musichoarder.local.");
var resendApiKey = builder.AddParameter("resend-api-key", builder.Configuration["Parameters:resend-api-key"] ?? "", secret: true)
    .WithDescription("Resend API key for magic-link emails. Optional — falls back to logging links to the console when blank.");
var resendFromAddress = builder.AddParameter("resend-from-address")
    .WithDescription("'From' address for magic-link emails (must be on a domain verified in Resend).");

// Locally, give each git branch/worktree its own data volume so their EF migration
// histories never collide (switching branches otherwise corrupts the shared __EFMigrationsHistory).
// Publish mode keeps the stable "musichoarder-volume" name baked into the compose output.
var dataVolumeName = "musichoarder-volume";
if (builder.ExecutionContext.IsRunMode)
{
    var dbKey = ResolveDbKey(builder.AppHostDirectory);
    if (!string.IsNullOrEmpty(dbKey))
    {
        dataVolumeName = $"musichoarder-volume-{dbKey}";
    }
}

// Pin the Postgres image to the major version prod's data volume was initialised with. The
// Aspire.Hosting.PostgreSQL default tag floats with the package and has since moved to 18.x (which
// also relocates the data dir to /var/lib/postgresql), so leaving it unpinned would let a compose
// regen silently propose a major-version upgrade + volume-path change to the live database. A
// deliberate PG upgrade is a separate, data-migration-gated change — not a side effect of `aspire publish`.
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("17.6")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume(dataVolumeName);
var postgresdb = postgres.AddDatabase("musichoarderdb");

var api = builder.AddProject<Projects.MusicHoarder_Api>("api")
    .WaitFor(postgresdb)
    .WithReference(postgresdb)
    .WithEnvironment("MusicEnricher__SourceDirectory", sourceDirectory)
    .WithEnvironment("MusicEnricher__DestinationDirectory", destinationDirectory)
    .WithEnvironment("MusicEnricher__DemoMediaDirectory", demoMediaDirectory)
    .WithEnvironment("MusicEnricher__AcoustIdApiKey", acoustIdApiKey)
    .WithEnvironment("Spotify__ClientId", spotifyClientId)
    .WithEnvironment("Spotify__ClientSecret", spotifyClientSecret)
    .WithEnvironment("Spotify__OAuthStateSigningKey", spotifyOAuthStateKey)
    .WithEnvironment("Auth__OwnerEmail", ownerEmail)
    .WithEnvironment("Auth__DemoUserEmail", demoUserEmail)
    .WithEnvironment("Auth__DataProtectionKeysPath", "/data/dpkeys")
    .WithEnvironment("Resend__ApiKey", resendApiKey)
    .WithEnvironment("Resend__FromAddress", resendFromAddress)
    .WithEnvironment("QualityGrading__ApiKey", qualityGradingApiKey)
    .WithEnvironment("QualityGrading__BaseUrl", qualityGradingBaseUrl)
    .WithEnvironment("QualityGrading__Model", qualityGradingModel)
    .WithEnvironment("LyricsTranscription__ApiKey", lyricsTranscriptionApiKey)
    .WithEnvironment("LyricsTranscription__BaseUrl", lyricsTranscriptionBaseUrl)
    .WithEnvironment("LyricsTranscription__Model", lyricsTranscriptionModel)
    .WithEnvironment("LyricsTranscription__LlmModel", lyricsTranscriptionLlmModel)
    .WithEnvironment("Slskd__BaseUrl", slskdBaseUrl)
    .WithEnvironment("Slskd__ApiKey", slskdApiKey)
    .WithEnvironment("Slskd__DownloadsDirectory", slskdDownloadsDirectory)
    .WithEnvironment("StreamingFlac__SidecarUrl", streamingFlacSidecarUrl)
    .WithExternalHttpEndpoints()
    .WithUrl("/scalar", "Scalar");
#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES003 // WithRemoteImageTag is experimental.
// Push a stable :latest tag so CI overwrites it each run and a Dokploy redeploy re-pulls it.
api.WithContainerRegistry(ghcr).WithRemoteImageTag("latest");
#pragma warning restore ASPIREPIPELINES003
#pragma warning restore ASPIRECOMPUTE003

#pragma warning disable ASPIREJAVASCRIPT001
#pragma warning disable ASPIRECERTIFICATES001
var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithBun()
    .WithHttpsEndpoint(env: "PORT")
    .WithHttpsDeveloperCertificate()
    .WithReference(api)
    // Internal Node→ASP.NET proxy hop stays HTTP to sidestep cross-runtime dev-cert trust.
    .WithEnvironment("MUSICHOARDER_API_URL", api.GetEndpoint("http"))
    // The Spotify OAuth relay route ("/api/spotify/relay") lives on the (prod) frontend and verifies the signed
    // state before bouncing the browser to the originating env's callback.
    .WithEnvironment("SPOTIFY_OAUTH_STATE_SIGNING_KEY", spotifyOAuthStateKey)
    .WithEnvironment("SPOTIFY_RETURN_ORIGIN_ALLOWLIST", spotifyReturnOriginAllowlist)
    .WithEnvironment("PUBLIC_UMAMI_SRC", publicUmamiSrc)
    .WithEnvironment("PUBLIC_UMAMI_WEBSITE_ID", publicUmamiWebsiteId)
    .WithEnvironment("PUBLIC_UMAMI_RECORDER_SRC", publicUmamiRecorderSrc)
    .WaitForStart(api)
    .WithExternalHttpEndpoints()
    .PublishAsNodeServer(entryPoint: "build/index.js", outputPath: "build");
#pragma warning restore ASPIRECERTIFICATES001
#pragma warning restore ASPIREJAVASCRIPT001

#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES003 // WithRemoteImageTag is experimental.
frontend.WithContainerRegistry(ghcr).WithRemoteImageTag("latest");
#pragma warning restore ASPIREPIPELINES003
#pragma warning restore ASPIRECOMPUTE003

api.WithEnvironment(context =>
{
    if (context.ExecutionContext.IsPublishMode)
    {
        context.EnvironmentVariables["Frontend__PublicBaseUrl"] = frontendPublicBaseUrl.Resource;
        // Prod is itself the relay host, so derive the registered redirect URI from the public frontend base.
        context.EnvironmentVariables["Spotify__OAuthRelayUrl"] =
            ReferenceExpression.Create($"{frontendPublicBaseUrl.Resource}/api/spotify/relay");
    }
    else
    {
        // PublicBaseUrl is this env's own (dynamic) frontend origin — the relay bounces the browser back here to
        // complete the exchange with the session cookie present, fixing the loopback "unauthenticated" failure.
        context.EnvironmentVariables["Frontend__PublicBaseUrl"] = frontend.GetEndpoint("https");
        // redirect_uri = the single relay URI registered in Spotify (a prod URL). Configure it via the
        // spotify-oauth-relay-url user-secret; when empty, OAuth falls back to request-derived (offline dev only).
        context.EnvironmentVariables["Spotify__OAuthRelayUrl"] = spotifyOAuthRelayUrl.Resource;
    }
});

builder.Build().Run();

// Resolves a per-branch key for the local Postgres data volume. Order: explicit env override,
// current git branch, short SHA when detached, else null (caller falls back to the shared volume).
static string? ResolveDbKey(string workingDir)
{
    var explicitKey = Environment.GetEnvironmentVariable("MUSICHOARDER_DB_KEY");
    if (!string.IsNullOrWhiteSpace(explicitKey))
        return Sanitize(explicitKey);

    var branch = RunGit("rev-parse --abbrev-ref HEAD", workingDir);
    if (string.IsNullOrEmpty(branch))
        return null;                       // git unavailable -> shared volume
    if (branch == "HEAD")                  // detached HEAD
        branch = RunGit("rev-parse --short HEAD", workingDir);

    return string.IsNullOrEmpty(branch) ? null : Sanitize(branch);

    static string Sanitize(string s)
    {
        var slug = Regex.Replace(s.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }

    static string? RunGit(string args, string dir)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}
