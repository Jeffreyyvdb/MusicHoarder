#!/usr/bin/env bash
#
# Restore a MusicHoarder catalog dump (produced by scripts/backup.sh) into the Postgres of the
# stack in the current directory. Use this to move your data onto a new host without re-running
# enrichment or AI analysis.
#
# Recommended order on the TARGET host (so the API's automatic EF migrations see the restored
# __EFMigrationsHistory and apply nothing new):
#
#   docker compose up -d postgres        # bring up only the database, wait until it's healthy
#   ./scripts/restore.sh musichoarder-backup-YYYYMMDD-HHMMSS.sql
#   docker compose up -d                 # start the API + frontend
#
# Both the shipped self-host stack and the Dokploy/Aspire stack use the `postgres` role by
# default, so ownership transfers cleanly. If your target volume was initialized under a different
# role, override it:  PG_USER=musichoarder ./scripts/restore.sh backup.sql
#
set -euo pipefail

PG_SERVICE="${PG_SERVICE:-postgres}"
PG_USER="${PG_USER:-postgres}"
PG_DB="${PG_DB:-musichoarderdb}"

DUMP_FILE="${1:-}"
if [[ -z "$DUMP_FILE" ]]; then
  echo "Usage: $0 <backup.sql>" >&2
  exit 1
fi
if [[ ! -r "$DUMP_FILE" ]]; then
  echo "Error: cannot read dump file '${DUMP_FILE}'" >&2
  exit 1
fi

echo "Restoring '${DUMP_FILE}' into database '${PG_DB}' (user '${PG_USER}', service '${PG_SERVICE}')"

# ON_ERROR_STOP=1 aborts on the first SQL error instead of plowing through a half-broken restore.
docker compose exec -T "$PG_SERVICE" \
  psql -U "$PG_USER" -d "$PG_DB" -v ON_ERROR_STOP=1 \
  <"$DUMP_FILE"

echo "Done. Start the rest of the stack with: docker compose up -d"
