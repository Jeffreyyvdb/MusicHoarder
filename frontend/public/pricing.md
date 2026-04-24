# Pricing — MusicHoarder

MusicHoarder is free and open source. There is no paid tier, no hosted version, and no "contact sales" wall.

## Self-hosted

- Price: $0
- License: MIT
- Source: https://github.com/Jeffreyyvdb/MusicHoarder
- Deployment: Docker (ships with .NET Aspire orchestration + PostgreSQL)
- Support: community (GitHub issues)
- Included: the full pipeline — scanning, fingerprinting, multi-provider enrichment (AcoustID, MusicBrainz, Spotify), duplicate detection, manual review, file organization, synced lyrics.

## Hosted

Not offered. MusicHoarder is intentionally self-hosted — it operates on your audio files, which stay on your infrastructure.

## Third-party API costs

MusicHoarder itself is free, but some providers it integrates with have their own terms:

- **AcoustID**: free for personal use, requires a free API key.
- **MusicBrainz**: free, rate-limited.
- **Spotify Web API**: free, requires your own Spotify Developer app (Client ID + Secret).

_Last updated: 2026-04-24_
