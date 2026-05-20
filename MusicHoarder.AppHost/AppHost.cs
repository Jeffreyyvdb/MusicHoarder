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
var acoustIdApiKey = builder.AddParameter("acoustid-api-key", secret: true)
    .WithDescription("AcoustID API key (acoustid.org/api-key). Optional — disables the AcoustID provider when blank.");
var spotifyClientId = builder.AddParameter("spotify-client-id", secret: true)
    .WithDescription("Spotify app Client ID. Optional — disables Spotify enrichment + OAuth when blank.");
var spotifyClientSecret = builder.AddParameter("spotify-client-secret", secret: true)
    .WithDescription("Spotify app Client Secret.");
var frontendPublicBaseUrl = builder.AddParameter("frontend-public-base-url")
    .WithDescription("Public HTTPS base URL of the frontend, used for Spotify OAuth redirect-back. Only consumed when publishing (e.g. the Dokploy domain); local dev uses the dynamic dev endpoint.");

var ownerEmail = builder.AddParameter("owner-email")
    .WithDescription("Email of the owner (admin) account. Used by magic-link sign-in.");
var demoUserEmail = builder.AddParameter("demo-user-email")
    .WithDescription("Email of the demo (read-only) account. Defaults to demo@musichoarder.local.");
var resendApiKey = builder.AddParameter("resend-api-key", secret: true)
    .WithDescription("Resend API key for magic-link emails. Optional — falls back to logging links to the console when blank.");
var resendFromAddress = builder.AddParameter("resend-from-address")
    .WithDescription("'From' address for magic-link emails (must be on a domain verified in Resend).");

var postgres = builder.AddPostgres("postgres")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("musichoarder-volume");
var postgresdb = postgres.AddDatabase("musichoarderdb");

var api = builder.AddProject<Projects.MusicHoarder_Api>("api")
    .WaitFor(postgresdb)
    .WithReference(postgresdb)
    .WithEnvironment("MusicEnricher__SourceDirectory", sourceDirectory)
    .WithEnvironment("MusicEnricher__DestinationDirectory", destinationDirectory)
    .WithEnvironment("MusicEnricher__AcoustIdApiKey", acoustIdApiKey)
    .WithEnvironment("Spotify__ClientId", spotifyClientId)
    .WithEnvironment("Spotify__ClientSecret", spotifyClientSecret)
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
    // In publish (Docker Compose) the frontend's internal hostname isn't the public origin
    // Spotify must redirect back to, so emit a fillable parameter; dev keeps the live endpoint.
    if (context.ExecutionContext.IsPublishMode)
    {
        context.EnvironmentVariables["Frontend__PublicBaseUrl"] = frontendPublicBaseUrl.Resource;
    }
    else
    {
        context.EnvironmentVariables["Frontend__PublicBaseUrl"] = frontend.GetEndpoint("https");
    }
});

builder.Build().Run();
