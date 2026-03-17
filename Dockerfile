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

# Install fpcalc (Chromaprint) for acoustic fingerprinting
RUN apt-get update && apt-get install -y --no-install-recommends \
    libchromaprint-tools \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "MusicHoarder.Api.dll"]
