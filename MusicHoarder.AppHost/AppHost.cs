var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose")
    .WithProperties(env => env.DashboardEnabled = true);

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

var postgres = builder.AddPostgres("postgres")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("musichoarder-volume");
var postgresdb = postgres.AddDatabase("musichoarderdb");

var api = builder.AddProject<Projects.MusicHoarder_Api>("musichoarder-api")
    .WaitFor(postgresdb)
    .WithReference(postgresdb)
    .WithEnvironment("MusicEnricher__SourceDirectory", sourceDirectory)
    .WithEnvironment("MusicEnricher__DestinationDirectory", destinationDirectory)
    .WithEnvironment("MusicEnricher__AcoustIdApiKey", acoustIdApiKey)
    .WithEnvironment("Spotify__ClientId", spotifyClientId)
    .WithEnvironment("Spotify__ClientSecret", spotifyClientSecret)
    .WithExternalHttpEndpoints()
    .WithUrl("/scalar", "Scalar");
#pragma warning disable ASPIRECOMPUTE003
api.WithContainerRegistry(ghcr);
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
frontend.WithContainerRegistry(ghcr);
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
