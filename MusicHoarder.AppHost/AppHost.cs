var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("musichoarder-volume");
var postgresdb = postgres.AddDatabase("musichoarderdb");

builder.AddProject<Projects.MusicHoarder_Api>("musichoarder-api")
    .WaitFor(postgresdb)
    .WithReference(postgresdb);

builder.Build().Run();