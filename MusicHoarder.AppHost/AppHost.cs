var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose")
    .WithProperties(env => env.DashboardEnabled = true);

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

api.WithEnvironment("Frontend__PublicBaseUrl", frontend.GetEndpoint("https"));

builder.Build().Run();
