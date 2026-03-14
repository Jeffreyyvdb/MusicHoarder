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
    .WithNpm()
    .WithHttpEndpoint(port: 5173, env: "PORT")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();