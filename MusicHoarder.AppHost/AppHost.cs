var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("musichoarder-volume");
var postgresdb = postgres.AddDatabase("musichoarderdb");

var api = builder.AddProject<Projects.MusicHoarder_Api>("musichoarder-api")
    .WaitFor(postgresdb)
    .WithReference(postgresdb)
    .WithExternalHttpEndpoints()
    .WithUrl("/scalar", "Scalar");

var frontend = builder.AddJavaScriptApp("frontend", "../frontend")
    .WithPnpm()
    .WithHttpEndpoint(env: "PORT")
    .WithReference(api)
    .WithEnvironment("MUSICHOARDER_API_URL", api.GetEndpoint("http"))
    .WaitForStart(api)
    .WithExternalHttpEndpoints()
    .PublishAsDockerFile();

api.WithEnvironment("Frontend__PublicBaseUrl", frontend.GetEndpoint("http"));

builder.Build().Run();