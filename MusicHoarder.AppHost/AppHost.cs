using System.Diagnostics;
using System.Text.RegularExpressions;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose")
    .WithProperties(env => env.DashboardEnabled = true)
    .ConfigureComposeFile(file =>
    {
        var apiService = file.Services["api"];

        // Force a fresh pull of the :latest images on every deploy. Without this, Dokploy's
        // `docker compose up` reuses the cached :latest tag and never picks up new builds.
        apiService.PullPolicy = "always";
        file.Services["frontend"].PullPolicy = "always";

        // Persist ASP.NET DataProtection keys across redeploys (otherwise auth sessions reset on
        // every deploy). Named volume, so it survives container recreation.
        apiService.AddVolume(new Volume { Name = "musichoarder-dpkeys", Type = "volume", Source = "musichoarder-dpkeys", Target = "/data/dpkeys" });
        file.Volumes["musichoarder-dpkeys"] = new Volume { Name = "musichoarder-dpkeys", Driver = "local" };

        // Bind-mount the host music library into the API container at the same paths the app reads
        // (compose interpolates ${SOURCE_DIRECTORY}/${DESTINATION_DIRECTORY} from the deploy env;
        // Docker creates the host dirs if missing).
        apiService.AddVolume(new Volume { Name = "music-source", Type = "bind", Source = "${SOURCE_DIRECTORY}", Target = "${SOURCE_DIRECTORY}", ReadOnly = true });
        apiService.AddVolume(new Volume { Name = "music-destination", Type = "bind", Source = "${DESTINATION_DIRECTORY}", Target = "${DESTINATION_DIRECTORY}" });
    });

// GHCR registry so `aspire publish` emits ghcr.io image references and `aspire do push`
// builds + pushes there. Dokploy's Compose service pulls these prebuilt images.
#pragma warning disable ASPIRECOMPUTE003 // AddContainerRegistry/WithContainerRegistry are experimental.
var ghcr = builder.AddContainerRegistry("ghcr", "ghcr.io", "jeffreyyvdb/musichoarder");
#pragma warning restore ASPIRECOMPUTE003

var sourceDirectory = builder.AddParameter("source-directory")
    .WithDescription("Source music directory the scanner crawls (absolute path).");
var destinationDirectory = builder.AddParameter("destination-directory")
    .WithDescription("Destination library directory the LibraryBuilder writes into (absolute path).");
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

var postgres = builder.AddPostgres("postgres")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume(dataVolumeName);
var postgresdb = postgres.AddDatabase("musichoarderdb");

var api = builder.AddProject<Projects.MusicHoarder_Api>("api")
    .WaitFor(postgresdb)
    .WithReference(postgresdb)
    .WithEnvironment("MusicEnricher__SourceDirectory", sourceDirectory)
    .WithEnvironment("MusicEnricher__DestinationDirectory", destinationDirectory)
    .WithEnvironment("MusicEnricher__AcoustIdApiKey", acoustIdApiKey)
    .WithEnvironment("Spotify__ClientId", spotifyClientId)
    .WithEnvironment("Spotify__ClientSecret", spotifyClientSecret)
    .WithEnvironment("Spotify__OAuthStateSigningKey", spotifyOAuthStateKey)
    .WithEnvironment("Auth__OwnerEmail", ownerEmail)
    .WithEnvironment("Auth__DemoUserEmail", demoUserEmail)
    .WithEnvironment("Auth__DataProtectionKeysPath", "/data/dpkeys")
    .WithEnvironment("Resend__ApiKey", resendApiKey)
    .WithEnvironment("Resend__FromAddress", resendFromAddress)
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
