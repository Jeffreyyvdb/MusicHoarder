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

# Pinned tool versions. The checksums are the ones each project publishes for the asset
# (yt-dlp: SHA2-256SUMS, deno: <asset>.sha256sum); the build fails if a download differs.
# .github/workflows/tool-pins.yml opens a PR when a newer release is out. Keep these four lines
# identical in Dockerfile and Dockerfile.api-base.
ARG YTDLP_VERSION=2026.10.26
ARG YTDLP_SHA256=767ba9dce7052aa196f8b12652f433d76de128ba168966fa84b6394d5e6c6795
ARG DENO_VERSION=v2.9.8
ARG DENO_SHA256=e890975bc2549807bbcc0b584ffafda72cc7c93349dd99c80483dd784f6c4609

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
    && curl -fsSL "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp_linux" -o /usr/local/bin/yt-dlp \
    && echo "${YTDLP_SHA256}  /usr/local/bin/yt-dlp" | sha256sum -c - \
    && chmod a+rx /usr/local/bin/yt-dlp \
    && curl -fsSL "https://github.com/denoland/deno/releases/download/${DENO_VERSION}/deno-x86_64-unknown-linux-gnu.zip" -o /tmp/deno.zip \
    && echo "${DENO_SHA256}  /tmp/deno.zip" | sha256sum -c - \
    && unzip -o /tmp/deno.zip -d /usr/local/bin \
    && chmod a+rx /usr/local/bin/deno \
    && rm -f /tmp/deno.zip \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .
# Runs the app as PUID/PGID when they are set, else as root. Same script as Dockerfile.api-base.
COPY --chmod=755 docker/api-entrypoint.sh /usr/local/bin/mh-entrypoint

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

# Dokploy reads this to gate zero-downtime traffic switching
# (https://docs.dokploy.com/docs/core/applications/zero-downtime).
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8080/alive || exit 1

ENTRYPOINT ["/usr/local/bin/mh-entrypoint", "dotnet", "MusicHoarder.Api.dll"]
