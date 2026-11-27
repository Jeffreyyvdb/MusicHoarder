# MusicHoarder documentation

Start here if you are new to the codebase.

## Understand the system

- [Architecture overview](architecture.md) — the entry point: system context, components, the
  processing pipeline, and cross-cutting decisions.
- [Backend](backend.md) — the ASP.NET Core API: endpoint organization, auth, background workers,
  rate limiting, observability, snapshots, notifications and sharing.
- [Data model](data-model.md) — EF Core entities and relationships, PostgreSQL provisioning and
  migrations, and the on-disk storage layout.
- [Integrations](integrations.md) — every external provider (AcoustID, MusicBrainz, Spotify,
  Apple Music, Deezer, Soulseek, Navidrome, lyrics and LLM services) and how the
  scan → match → decide → build pipeline composes them.
- [Frontend & mobile](frontend.md) — the SvelteKit web app and the Capacitor iOS/Android shell.

## Run and operate it

- [Runbook](runbook.md) — how to run, upgrade, back up and troubleshoot a deployment.
- [Deployment](deployment.md) — Docker images, CI/CD workflows, semantic-release versioning and
  zero-downtime deploys.
- [Self-hosting guide](SELF_HOSTING.md) — the operator-facing reference: env variables, first
  login, reverse proxy, optional integrations, backups, troubleshooting.

## Decide

- [Architecture decision records](adr/README.md) — why the system is built the way it is:
  Aspire orchestration, the persistence and testing strategy, the pipeline design, release
  engineering, and more.

## Where to look next

| If you want to… | Read |
|-----------------|------|
| Get the app running locally | [README quickstart](../README.md#quickstart-local-development) |
| Understand a specific endpoint | [backend.md](backend.md), then `MusicHoarder.Api/Endpoints/` |
| Add or change an entity | [data-model.md](data-model.md), then `MusicHoarder.Api/Data/` |
| Work on a provider integration | [integrations.md](integrations.md) |
| Change the UI | [frontend.md](frontend.md), then `frontend/src/` |
| Ship a release | [deployment.md](deployment.md) — and keep commit messages Conventional |
| Contribute | [CONTRIBUTING.md](../CONTRIBUTING.md) |
