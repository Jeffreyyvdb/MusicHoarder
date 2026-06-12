#!/usr/bin/env bash
#
# Remap the absolute file paths stored in the MusicHoarder catalog after restoring a dump onto a
# host that mounts the library at different paths than the source environment did.
#
# WHY: Songs.SourcePath / DestinationPath are absolute. The API matches files to rows by exact
# SourcePath and auto-scans on first boot, so if the stored paths don't equal what THIS stack scans
# (MusicEnricher__SourceDirectory inside the container), the scan soft-deletes the whole imported
# catalog and re-enriches from scratch. Run this BEFORE `docker compose up -d` (with only Postgres
# up). See docs/SELF_HOSTING.md.
#
# Usage (either or both mappings; FROM/TO are PATH PREFIXES, not full paths):
#
#   ./scripts/remap-paths.sh --source /root/music            /music/source \
#                            --dest   /root/musichoarder-dest /music/destination
#
# --source rewrites Songs.SourcePath; --dest rewrites Songs.DestinationPath +
# PreviousDestinationPath. Replacement is prefix-anchored, so a source prefix that is also a
# substring of the destination prefix (e.g. /root/music vs /root/musichoarder-dest) is safe. The
# demo account's synthetic rows (demo:// , /demo/...) never match a real prefix, so they're left
# alone.
#
# Find the current prefixes first with:  ./scripts/restore.sh prints them, or
#   docker compose exec -T postgres psql -U postgres -d musichoarderdb -At -c \
#     'SELECT DISTINCT "SourcePath" FROM "Songs" WHERE "SourcePath" NOT LIKE '\''demo://%'\'' LIMIT 3;'
#
set -euo pipefail

PG_SERVICE="${PG_SERVICE:-postgres}"
PG_USER="${PG_USER:-postgres}"
PG_DB="${PG_DB:-musichoarderdb}"

# Print the leading comment block (the header above) as help text, then exit.
usage() { awk 'NR==1{next} /^#/{sub(/^# ?/,""); print; next} {exit}' "$0"; exit "${1:-1}"; }

SRC_FROM="" SRC_TO="" DST_FROM="" DST_TO=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --source)
      [[ $# -ge 3 ]] || { echo "Error: --source needs FROM and TO" >&2; exit 1; }
      SRC_FROM="$2"; SRC_TO="$3"; shift 3 ;;
    --dest)
      [[ $# -ge 3 ]] || { echo "Error: --dest needs FROM and TO" >&2; exit 1; }
      DST_FROM="$2"; DST_TO="$3"; shift 3 ;;
    -h|--help) usage 0 ;;
    *) echo "Error: unknown argument '$1'" >&2; usage ;;
  esac
done

if [[ -z "$SRC_FROM" && -z "$DST_FROM" ]]; then
  echo "Error: provide --source and/or --dest" >&2; usage
fi

# Path prefixes shouldn't contain single quotes; reject them so we can't break (or inject) SQL.
for v in "$SRC_FROM" "$SRC_TO" "$DST_FROM" "$DST_TO"; do
  case "$v" in *\'*) echo "Error: paths must not contain a single quote: $v" >&2; exit 1 ;; esac
done

# Prefix-anchored rewrite: keep everything after the FROM prefix, swap in the TO prefix. The column
# argument is a fixed double-quoted SQL identifier we control; only FROM/TO are variable, and
# they're already validated to contain no single quote, so direct interpolation is safe here.
emit_update() {  # column-identifier, from, to
  printf "UPDATE \"Songs\" SET %s = '%s' || substring(%s from char_length('%s') + 1) WHERE %s LIKE '%s%%';\n" \
    "$1" "$3" "$1" "$2" "$1" "$2"
}

SQL=""
[[ -n "$SRC_FROM" ]] && SQL+=$(emit_update '"SourcePath"' "$SRC_FROM" "$SRC_TO")$'\n'
if [[ -n "$DST_FROM" ]]; then
  SQL+=$(emit_update '"DestinationPath"'         "$DST_FROM" "$DST_TO")$'\n'
  SQL+=$(emit_update '"PreviousDestinationPath"' "$DST_FROM" "$DST_TO")$'\n'
fi

echo "Applying path remap to database '${PG_DB}':"
[[ -n "$SRC_FROM" ]] && echo "  SourcePath:      ${SRC_FROM} -> ${SRC_TO}"
[[ -n "$DST_FROM" ]] && echo "  DestinationPath: ${DST_FROM} -> ${DST_TO}"
echo

printf '%s' "$SQL" | docker compose exec -T "$PG_SERVICE" \
  sh -c 'PGPASSWORD="${POSTGRES_PASSWORD:-}" psql -U '"'$PG_USER'"' -d '"'$PG_DB'"' -v ON_ERROR_STOP=1'

echo
echo "Done. Verify the prefixes now match 'docker compose config | grep -i SourceDirectory',"
echo "then start the stack: docker compose up -d"
