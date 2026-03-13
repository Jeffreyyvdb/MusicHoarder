using System.IO.Abstractions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api;
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

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.AddNpgsqlDbContext<MusicHoarderDbContext>(connectionName: "musichoarderdb");

builder.Services.AddSingleton(Channel.CreateUnbounded<ScanRequest>());
builder.Services.AddHostedService<ScannerBackgroundService>();
builder.Services.AddScoped<IFileSystem, FileSystem>();
builder.Services.AddScoped<IFileScanner, FileScanner>();
builder.Services.AddScoped<IIndexService, IndexService>();

builder.Services.AddOpenApi();

var app = builder.Build();

var musicEnricherOptions = app.Services.GetRequiredService<IOptions<MusicEnricherOptions>>().Value;
Directory.CreateDirectory(musicEnricherOptions.TempDirectory);

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();

    var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseHttpsRedirection();

app.MapPost("/scan", async (Channel<ScanRequest> channel) =>
{
    var scanId = Guid.NewGuid();
    await channel.Writer.WriteAsync(new ScanRequest(scanId));
    return Results.Accepted($"/scan/{scanId}", new { scanId });
});

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
    {
        var forecast = Enumerable.Range(1, 5).Select(index =>
                new WeatherForecast
                (
                    DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                    Random.Shared.Next(-20, 55),
                    summaries[Random.Shared.Next(summaries.Length)]
                ))
            .ToArray();
        return forecast;
    })
    .WithName("GetWeatherForecast");

app.Run();

namespace MusicHoarder.Api
{
    record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
    {
        public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
    }
}