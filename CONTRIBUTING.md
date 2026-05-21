# Contributing to MusicHoarder

Thanks for your interest in contributing! This guide covers how to get the project
running locally and the conventions we follow.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) — Aspire provisions PostgreSQL as a container, so it must be running
- [Bun](https://bun.sh/) — frontend toolchain
- Node.js 22 — only needed for the semantic-release step
- `fpcalc` (from `libchromaprint-tools`) — required for audio fingerprinting

## Getting started

```bash
# Run the full stack (Aspire dashboard at https://localhost:17072).
# Provisions PostgreSQL in Docker, starts the API + frontend, auto-applies EF migrations.
dotnet run --project MusicHoarder.AppHost
```

On first run, required values (source/destination directories and optional API
credentials) are modeled as AppHost parameters and prompted in the Aspire dashboard.
To pre-seed them for repeatable boots, set them as AppHost user-secrets:

```bash
dotnet user-secrets set "Parameters:source-directory" "/tmp/musichoarder-source" --project MusicHoarder.AppHost
dotnet user-secrets set "Parameters:destination-directory" "/tmp/musichoarder-dest" --project MusicHoarder.AppHost
```

See `README.md` and `CLAUDE.md` for the full architecture overview and configuration details.

## Running checks locally

Please make sure these pass before opening a PR — CI runs the same checks.

```bash
# API tests (xUnit, in-memory EF provider — no Postgres/Docker required)
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj

# Frontend
cd frontend
bun run check   # svelte-check + TypeScript
bun run lint    # ESLint (flat config)
bun run build   # SvelteKit + adapter-node build
```

## Commit messages

Commit messages **must** follow [Conventional Commits](https://www.conventionalcommits.org/).
They are load-bearing: the whole repo (API and frontend together) is versioned by
semantic-release, and your commit subject decides the release bump.

| Prefix                                              | Release bump      |
| --------------------------------------------------- | ----------------- |
| `fix:` / `fix(scope):`                              | patch (0.0.**X**) |
| `feat:` / `feat(scope):`                            | minor (0.**Y**.0) |
| `feat!:` / any commit with a `BREAKING CHANGE:` foot | major (**X**.0.0) |
| `chore:`, `docs:`, `refactor:`, `test:`, `style:`   | no release        |

## Pull requests

1. Fork the repo and create a branch with a short, descriptive, kebab-case name
   (e.g. `feat/spotify-isrc-matching`, `fix/oauth-redirect`).
2. Make your change, keeping it focused and well-scoped.
3. Ensure the checks above pass.
4. Open a PR with a concise description: summarize the change, note any schema or
   config changes, and link the relevant issue.

## Code conventions

- Follow the patterns already in `MusicHoarder.Api`, `MusicHoarder.AppHost`, and
  `MusicHoarder.ServiceDefaults` — prefer extending them over inventing new ones.
- Schema changes go through an EF Core migration; never ship manual SQL.
- Configuration belongs in the `MusicEnricher` options section, not hardcoded.
- **Never commit secrets, credentials, personal data, or local environment paths.**
  See `SECURITY.md`.

`AGENTS.md` has the long-form design notes and conventions, including a frontend
flex/scrolling gotcha that comes up often.

## License

By contributing, you agree that your contributions will be licensed under the
[MIT License](LICENSE).
