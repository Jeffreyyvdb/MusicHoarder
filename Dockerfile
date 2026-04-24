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

# Install fpcalc (Chromaprint) for acoustic fingerprinting; curl for healthcheck
RUN apt-get update && apt-get install -y --no-install-recommends \
    libchromaprint-tools \
    curl \
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
