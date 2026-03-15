var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("musichoarder-volume");
var postgresdb = postgres.AddDatabase("musichoarderdb");

var api = builder.AddProject<Projects.MusicHoarder_Api>("musichoarder-api")
    .WaitFor(postgresdb)
    .WithReference(postgresdb)
    .WithExternalHttpEndpoints()
    .WithUrl("/scalar", "Scalar");

builder.AddJavaScriptApp("frontend", "../frontend")
    // .WithPnpm()
    .WithNpm()
    .WithHttpEndpoint(env: "PORT")
    .WithReference(api)
    .WithEnvironment("MUSICHOARDER_API_URL", api.GetEndpoint("http"))
    .WaitFor(api)
    .WithExternalHttpEndpoints()
    .PublishAsDockerFile();

builder.Build().Run();