# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first so NuGet restore is cached independently of source changes
COPY MusicHoarder.Api/MusicHoarder.Api.csproj MusicHoarder.Api/
COPY MusicHoarder.ServiceDefaults/MusicHoarder.ServiceDefaults.csproj MusicHoarder.ServiceDefaults/

RUN dotnet restore MusicHoarder.Api/MusicHoarder.Api.csproj

# Copy source and publish
COPY MusicHoarder.Api/ MusicHoarder.Api/
COPY MusicHoarder.ServiceDefaults/ MusicHoarder.ServiceDefaults/

RUN dotnet publish MusicHoarder.Api/MusicHoarder.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Runtime deps:
#   - libchromaprint-tools → fpcalc for acoustic fingerprinting (AcoustID)
#   - ffmpeg               → audio extraction/remux for the wishlist yt-dlp downloader
#   - yt-dlp (self-contained PyInstaller build) → fetches wishlist tracks from YouTube
#   - deno                 → JS runtime yt-dlp REQUIRES to solve YouTube's player challenge.
#                            Recent yt-dlp deprecated extraction without one ("No supported
#                            JavaScript runtime could be found" → ERROR). yt-dlp auto-detects
#                            deno on PATH, so no --js-runtimes flag is needed. See
#                            https://github.com/yt-dlp/yt-dlp/wiki/EJS
#   - curl / unzip         → container healthcheck + fetching the yt-dlp & deno binaries
RUN apt-get update && apt-get install -y --no-install-recommends \
    libchromaprint-tools \
    ffmpeg \
    curl \
    unzip \
    && curl -fsSL https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux -o /usr/local/bin/yt-dlp \
    && chmod a+rx /usr/local/bin/yt-dlp \
    && curl -fsSL https://github.com/denoland/deno/releases/latest/download/deno-x86_64-unknown-linux-gnu.zip -o /tmp/deno.zip \
    && unzip -o /tmp/deno.zip -d /usr/local/bin \
    && chmod a+rx /usr/local/bin/deno \
    && rm -f /tmp/deno.zip \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

# Dokploy reads this to gate zero-downtime traffic switching
# (https://docs.dokploy.com/docs/core/applications/zero-downtime).
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8080/alive || exit 1

ENTRYPOINT ["dotnet", "MusicHoarder.Api.dll"]
