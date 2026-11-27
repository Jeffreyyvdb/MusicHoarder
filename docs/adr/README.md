# Architecture Decision Records

Short-lived records of significant architectural decisions in MusicHoarder. Each ADR states the
decision as it stands in the code today, the alternatives considered, and the trade-offs the
codebase has accepted. Every claim is backed by the cited source files.

For how to *run* the system, see the [self-hosting guide](../SELF_HOSTING.md) and
[AGENTS.md](../../AGENTS.md); for the pipeline's day-to-day behavior, see [ADR-003](ADR-003.md)
and [ADR-004](ADR-004.md).

| ADR | Title | Status |
| --- | ----- | ------ |
| [ADR-001](ADR-001.md) | Use .NET Aspire as the single source for both local orchestration and deployment artifacts | Accepted |
| [ADR-002](ADR-002.md) | PostgreSQL + EF Core (Npgsql) as the only store, with the EF InMemory provider for unit tests | Accepted |
| [ADR-003](ADR-003.md) | The ingestion pipeline is a set of independent BackgroundServices sweeping a `SongMetadata` state machine | Accepted |
| [ADR-004](ADR-004.md) | Activity and job reporting are derived at read time, not written to event-log tables | Accepted |
| [ADR-005](ADR-005.md) | One weekly semantic-release cut for the whole repo, with deployment decoupled as `:edge` builds | Accepted |
| [ADR-006](ADR-006.md) | One SvelteKit web client served by adapter-node in Docker, plus a thin Capacitor shell — no second client codebase | Accepted |

## Conventions

- **Status** — `Accepted` means the decision is already implemented in the code this repository
  serves. Superseded or proposed ADRs would carry their own status; there are none yet.
- **Dates** — recorded as the date the ADR was written down, not the date the decision was first
  made (some of these predate this documentation by a long margin; ADR-006 in particular
  formalizes a migration away from the retired `android/` native client).
- New ADRs get the next number in sequence and a row in the table above. Once recorded, an ADR is
  not edited to match new reality — a new ADR supersedes it and the old one gains a `Superseded
  by ADR-NNN` status.
