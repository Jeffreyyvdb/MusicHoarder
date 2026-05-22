using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth.Middleware;
using MusicHoarder.Api.Composition;
using MusicHoarder.Api.OpenApi;
using MusicHoarder.Api.Persistence;
using MusicHoarder.ServiceDefaults;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddMusicHoarderServices();

// Register the DbContext ourselves (non-pooled) so we can use a second constructor that takes
// ICurrentUserAccessor for per-user EF global query filters. Then call EnrichNpgsqlDbContext to
// apply Aspire's connection-string + OpenTelemetry wiring on top.
builder.Services.AddDbContext<MusicHoarderDbContext>((sp, options) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    options.UseNpgsql(configuration.GetConnectionString("musichoarderdb"));
});
builder.EnrichNpgsqlDbContext<MusicHoarderDbContext>();

builder.Services.AddOpenApi(options => options.AddDocumentTransformer<CookieSecuritySchemeTransformer>());

var app = builder.Build();

app.MapDefaultEndpoints();

await app.ApplyPendingMigrationsAsync();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options
        .AddPreferredSecuritySchemes(CookieSecuritySchemeTransformer.SchemeId));
}

// No app-level HTTPS redirection: in deployment TLS terminates at the reverse proxy
// (Traefik/Dokploy) and the API only listens on HTTP, so UseHttpsRedirection can't
// resolve an HTTPS port and just logs "Failed to determine the https port for redirect".

app.UseMiddleware<AuthenticationMiddleware>();
app.UseMiddleware<RequireAuthMiddleware>();

app.MapMusicHoarderEndpoints();

app.Run();
