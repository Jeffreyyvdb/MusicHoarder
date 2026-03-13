using System.IO.Abstractions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;
using MusicHoarder.ServiceDefaults;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services
    .AddOptions<MusicEnricherOptions>()
    .BindConfiguration(MusicEnricherOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.AddNpgsqlDbContext<MusicHoarderDbContext>(connectionName: "musichoarderdb");

builder.Services.AddSingleton(Channel.CreateUnbounded<ScanRequest>());
builder.Services.AddSingleton<ScanProgressTracker>();
builder.Services.AddSingleton<IFpcalcService, FpcalcService>();

builder.Services.AddHostedService<ScannerBackgroundService>();

builder.Services.AddScoped<IFileSystem, FileSystem>();
builder.Services.AddScoped<IFileScanner, FileScanner>();
builder.Services.AddScoped<IIndexService, IndexService>();

builder.Services.AddOpenApi();

var app = builder.Build();

var musicEnricherOptions = app.Services.GetRequiredService<IOptions<MusicEnricherOptions>>().Value;
Directory.CreateDirectory(musicEnricherOptions.TempDirectory);

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();

    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseHttpsRedirection();

app.MapPost("/scan", async (Channel<ScanRequest> channel) =>
{
    var scanId = Guid.NewGuid();
    await channel.Writer.WriteAsync(new ScanRequest(scanId));
    return Results.Accepted($"/scan/{scanId}/progress", new { scanId });
});

app.MapGet("/scan/{scanId}/progress", (Guid scanId, ScanProgressTracker tracker) =>
{
    var state = tracker.GetCurrent();
    if (state is null || state.ScanId != scanId)
        return Results.NotFound(new { message = "No scan found with that id." });

    return Results.Ok(state);
});

app.Run();
