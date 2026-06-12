#!/usr/bin/env bash
#
# Restore a MusicHoarder catalog dump (produced by scripts/backup.sh) into the Postgres of the
# stack in the current directory. Use this to move your data onto a new host without re-running
# enrichment or AI analysis.
#
# Recommended order on the TARGET host:
#
#   docker compose up -d postgres        # bring up ONLY the database, wait until it's healthy
#   ./scripts/restore.sh musichoarder-backup-YYYYMMDD-HHMMSS.sql
#   ./scripts/remap-paths.sh ...         # IF the new host mounts your library at different paths
#   docker compose up -d                 # start the API + frontend
#
# ┌─ WHY THE remap STEP MATTERS ───────────────────────────────────────────────────────────────┐
# │ The dump stores ABSOLUTE file paths (Songs.SourcePath / DestinationPath). The API matches    │
# │ files to rows by exact SourcePath, and a scan auto-fires on first boot. If the stored paths   │
# │ don't match what THIS stack scans (MusicEnricher__SourceDirectory inside the container), the  │
# │ scan treats every file as new and SOFT-DELETES your whole imported catalog, then re-enriches  │
# │ from scratch. Remap the paths (scripts/remap-paths.sh) BEFORE starting the API.               │
# └────────────────────────────────────────────────────────────────────────────────────────────┘
#
# Both the shipped self-host stack and the Dokploy/Aspire stack use the `postgres` role by default,
# so ownership transfers cleanly. If your target volume was initialized under a different role,
# override it:  PG_USER=musichoarder ./scripts/restore.sh backup.sql
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

# `sh -c` so psql reads the container's own $POSTGRES_PASSWORD (works on both `trust` and
# `scram-sha-256` local auth). ON_ERROR_STOP=1 aborts on the first SQL error.
docker compose exec -T "$PG_SERVICE" \
  sh -c 'PGPASSWORD="${POSTGRES_PASSWORD:-}" psql -U '"'$PG_USER'"' -d '"'$PG_DB'"' -v ON_ERROR_STOP=1' \
  <"$DUMP_FILE"

# Show the absolute paths the dump brought in (ignoring the demo account's synthetic demo:// rows),
# so you can tell at a glance whether they need remapping for this host.
echo
echo "=== Paths stored in the restored catalog (real library, demo rows excluded) ==="
docker compose exec -T "$PG_SERVICE" sh -c \
  'PGPASSWORD="${POSTGRES_PASSWORD:-}" psql -U '"'$PG_USER'"' -d '"'$PG_DB'"' -At -c '"'"'
    SELECT '\''SourcePath:      '\'' || "SourcePath" FROM "Songs"
      WHERE "SourcePath" NOT LIKE '\''demo://%'\'' LIMIT 1;
    SELECT '\''DestinationPath: '\'' || "DestinationPath" FROM "Songs"
      WHERE "DestinationPath" IS NOT NULL AND "DestinationPath" NOT LIKE '\''/demo/%'\'' LIMIT 1;'"'"'' \
  2>/dev/null || echo "(could not read sample paths — check the 'Songs' table manually)"

cat <<'NEXT'

=== NEXT STEPS ===
1. Compare the SourcePath/DestinationPath prefixes above with what THIS stack scans:
       docker compose config | grep -iE 'SourceDirectory|DestinationDirectory'
   They must match byte-for-byte. If they DON'T, remap before starting the API, e.g.:
       ./scripts/remap-paths.sh --source /old/src/prefix /music/source \
                                --dest   /old/dst/prefix /music/destination
2. (Optional) Match this host's enrichment env to the source (ACOUSTID_API_KEY, SPOTIFY_*,
   QUALITY_GRADING_*) so it doesn't needlessly re-run enrichment/grading for songs already done.
3. Start the rest of the stack:
       docker compose up -d
   Then watch the first scan — you want "0 new ... Marked 0 files as deleted":
       docker compose logs -f <api-service>
NEXT
