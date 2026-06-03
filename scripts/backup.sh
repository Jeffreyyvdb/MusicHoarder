#!/usr/bin/env bash
#
# Back up the MusicHoarder catalog (the musichoarderdb Postgres database) to a timestamped .sql
# file in the current directory. Everything worth keeping — enrichment provider matches, AI
# quality grades, lyrics, manual approvals, Spotify tokens + match cache, canonical albums — lives
# in this one database. The source library is read-only and the destination is regenerable, so
# neither needs backing up.
#
# Run it from the directory holding your docker-compose.yml (or its override), with the stack up:
#
#   ./scripts/backup.sh
#
# Override any of these via the environment if your stack differs (e.g. an older self-host install
# whose volume was initialized as the `musichoarder` role):
#
#   PG_USER=musichoarder ./scripts/backup.sh
#
set -euo pipefail

PG_SERVICE="${PG_SERVICE:-postgres}"
PG_USER="${PG_USER:-postgres}"
PG_DB="${PG_DB:-musichoarderdb}"

OUT="musichoarder-backup-$(date +%Y%m%d-%H%M%S).sql"

echo "Dumping database '${PG_DB}' (user '${PG_USER}', service '${PG_SERVICE}') -> ${OUT}"

# --clean --if-exists makes the dump idempotent: restoring it drops existing objects first, so it
# applies cleanly even into a database the API already created empty tables in via EF migrations.
docker compose exec -T "$PG_SERVICE" \
  pg_dump -U "$PG_USER" -d "$PG_DB" --clean --if-exists \
  >"$OUT"

echo "Done: ${OUT} ($(du -h "$OUT" | cut -f1))"
