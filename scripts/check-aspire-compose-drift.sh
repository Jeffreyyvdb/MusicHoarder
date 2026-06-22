#!/usr/bin/env bash
#
# Guards the Aspire-generated prod compose against silent drift from the AppHost, and the
# hand-maintained self-host compose against missing provider/feature keys.
#
# MusicHoarder.AppHost/aspire-output/docker-compose.yaml is a COMMITTED, generated artifact that
# Dokploy deploys verbatim (no regeneration at release time). When the AppHost gains an env mapping
# but the output file isn't regenerated, the deployed container never receives that setting — which
# is exactly how the AI lyrics feature shipped invisible in prod (the LyricsTranscription__* mappings
# were declared in the AppHost but absent from the generated compose).
#
# This is a PRESENCE check (not a regenerate-and-diff): regenerating in CI is fragile against Aspire
# output churn and would bake local-restore package defaults into the artifact. We assert instead that
# every env KEY the AppHost declares is present in the generated compose, and that every provider/
# feature KEY in the generated compose is also present in the self-host compose.
#
# Fix on failure: regenerate and commit the prod compose —
#   aspire publish -o MusicHoarder.AppHost/aspire-output --non-interactive
# — and/or add the missing key to docker-compose.yml.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APPHOST="$ROOT/MusicHoarder.AppHost/AppHost.cs"
COMPOSE_EXT="$ROOT/MusicHoarder.AppHost/ComposeFileExtensions.cs"
PROD_COMPOSE="$ROOT/MusicHoarder.AppHost/aspire-output/docker-compose.yaml"
SELFHOST_COMPOSE="$ROOT/docker-compose.yml"

for f in "$APPHOST" "$COMPOSE_EXT" "$PROD_COMPOSE" "$SELFHOST_COMPOSE"; do
  [ -f "$f" ] || { echo "::error::expected file not found: $f"; exit 1; }
done

fail=0

# ---------------------------------------------------------------------------
# Check 1: every env KEY the AppHost declares must exist in the generated prod compose.
#
# Three declaration forms feed the generator, all of which have produced real drift bugs:
#   - static     `.WithEnvironment("Key", value)`              (AppHost.cs)
#   - extension   `api.Environment["Key"] = "${VAR:-default}"`  (ComposeFileExtensions.cs)
#   - dynamic     `context.EnvironmentVariables["Key"] = ...`   (AppHost.cs context lambda)
# ---------------------------------------------------------------------------
declared_keys="$(
  {
    grep -oE '\.WithEnvironment\("[^"]+"' "$APPHOST"            | sed -E 's/.*\("([^"]+)".*/\1/'
    grep -oE 'api\.Environment\["[^"]+"\]' "$COMPOSE_EXT"       | sed -E 's/.*\["([^"]+)"\].*/\1/'
    grep -oE 'context\.EnvironmentVariables\["[^"]+"\]' "$APPHOST" | sed -E 's/.*\["([^"]+)"\].*/\1/'
  } | sort -u
)"

missing_prod=()
while IFS= read -r key; do
  [ -n "$key" ] || continue
  # Generated compose maps env as `      Key: "..."` under each service's `environment:`.
  if ! grep -qE "^[[:space:]]+${key}:[[:space:]]" "$PROD_COMPOSE"; then
    missing_prod+=("$key")
  fi
done <<< "$declared_keys"

if [ "${#missing_prod[@]}" -gt 0 ]; then
  fail=1
  echo "::error::aspire-output/docker-compose.yaml is out of sync with the AppHost — missing env mappings:"
  printf '  - %s\n' "${missing_prod[@]}"
  echo "  Fix: aspire publish -o MusicHoarder.AppHost/aspire-output --non-interactive (then commit)"
  echo ""
fi

# ---------------------------------------------------------------------------
# Check 2: provider/feature parity with the hand-maintained self-host compose.
#
# Scoped to the prefixes most likely to be forgotten when a new provider/feature is wired
# (MusicEnricher__/QualityGrading__/LyricsTranscription__). Spotify__/Auth__/Resend__ are
# deliberately excluded — the self-host stack uses a simpler direct Spotify redirect (no relay)
# and a different demo/auth surface, so those keys legitimately diverge.
#
# PROD_ONLY_KEYS lists keys that exist in the prod compose but intentionally have no self-host
# counterpart (demo-only infrastructure).
# ---------------------------------------------------------------------------
PROD_ONLY_KEYS="MusicEnricher__DemoMediaDirectory"

prod_feature_keys="$(
  grep -oE '^[[:space:]]+(MusicEnricher|QualityGrading|LyricsTranscription)__[A-Za-z]+:' "$PROD_COMPOSE" \
    | sed -E 's/^[[:space:]]+([^:]+):/\1/' | sort -u
)"

missing_selfhost=()
while IFS= read -r key; do
  [ -n "$key" ] || continue
  case " $PROD_ONLY_KEYS " in *" $key "*) continue ;; esac
  # Self-host compose uses list form: `      - Key=value`.
  if ! grep -qE "^[[:space:]]+-[[:space:]]*${key}=" "$SELFHOST_COMPOSE"; then
    missing_selfhost+=("$key")
  fi
done <<< "$prod_feature_keys"

if [ "${#missing_selfhost[@]}" -gt 0 ]; then
  fail=1
  echo "::error::docker-compose.yml (self-host) is missing provider/feature keys present in the prod compose:"
  printf '  - %s\n' "${missing_selfhost[@]}"
  echo "  Fix: add each key to the musichoarder service environment in docker-compose.yml"
  echo "  (or, if it is genuinely prod-only, add it to PROD_ONLY_KEYS in this script)."
  echo ""
fi

if [ "$fail" -ne 0 ]; then
  exit 1
fi

echo "compose drift check passed ✓"
echo "  - prod compose has all $(wc -w <<< "$declared_keys" | tr -d ' ') AppHost-declared env keys"
echo "  - self-host compose has all provider/feature keys from the prod compose"
