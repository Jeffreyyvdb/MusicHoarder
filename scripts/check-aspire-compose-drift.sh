#!/usr/bin/env bash
#
# Guards the Aspire-generated prod compose against silent drift from the AppHost, and the
# plain-compose deploy targets (self-host template + per-PR preview) against missing provider/feature keys.
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

# Plain-compose deploy targets that must mirror the prod compose's provider/feature env keys.
# Both run via `docker compose up` (not swarm stack deploy like prod), so they keep their own
# orchestrator-appropriate structure — but the env CONTRACT must not diverge from what the AppHost
# declares, which is the failure mode that shipped AI lyrics transcription invisible.
#   - docker-compose.yml           the published self-host template (GHCR images)
#   - docker-compose.preview.yaml  the per-PR preview stack posted to Dokploy by dokploy-preview.sh
PLAIN_COMPOSES=(
  "$ROOT/docker-compose.yml"
  "$ROOT/docker-compose.preview.yaml"
)

for f in "$APPHOST" "$COMPOSE_EXT" "$PROD_COMPOSE" "${PLAIN_COMPOSES[@]}"; do
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
# Check 2: provider/feature parity between the prod compose and every plain-compose deploy target.
#
# musichoarder.app deploys the prod compose via Docker Swarm stack deploy; the self-host template and
# the per-PR preview both run via plain `docker compose up`. They keep their own orchestrator shape
# (restart/pull_policy/healthcheck/traefik vs swarm deploy blocks), but their provider/feature ENV
# must mirror the prod compose — otherwise a new feature ships with the env missing on a target, which
# is exactly how AI lyrics transcription went live invisible.
#
# Scoped to the prefixes most likely to be forgotten when a new provider/feature is wired
# (MusicEnricher__/QualityGrading__/LyricsTranscription__). Spotify__/Auth__/Resend__ are excluded —
# the plain-compose targets legitimately diverge there (e.g. self-host uses a direct Spotify redirect
# instead of the relay, and a different demo/auth surface).
#
# PROD_ONLY_KEYS lists keys that exist in the prod compose but intentionally have no plain-compose
# counterpart (demo-only infrastructure that previews/self-host don't run).
# ---------------------------------------------------------------------------
PROD_ONLY_KEYS="MusicEnricher__DemoMediaDirectory"

prod_feature_keys="$(
  grep -oE '^[[:space:]]+(MusicEnricher|QualityGrading|LyricsTranscription)__[A-Za-z]+:' "$PROD_COMPOSE" \
    | sed -E 's/^[[:space:]]+([^:]+):/\1/' | sort -u
)"

for compose in "${PLAIN_COMPOSES[@]}"; do
  rel="${compose#"$ROOT"/}"
  missing=()
  while IFS= read -r key; do
    [ -n "$key" ] || continue
    case " $PROD_ONLY_KEYS " in *" $key "*) continue ;; esac
    # Plain composes use list form: `      - Key=value`.
    if ! grep -qE "^[[:space:]]+-[[:space:]]*${key}=" "$compose"; then
      missing+=("$key")
    fi
  done <<< "$prod_feature_keys"

  if [ "${#missing[@]}" -gt 0 ]; then
    fail=1
    echo "::error::${rel} is missing provider/feature keys present in the prod compose:"
    printf '  - %s\n' "${missing[@]}"
    echo "  Fix: add each key to the service environment in ${rel}"
    echo "  (or, if it is genuinely prod-only, add it to PROD_ONLY_KEYS in this script)."
    echo ""
  fi
done

if [ "$fail" -ne 0 ]; then
  exit 1
fi

echo "compose drift check passed ✓"
echo "  - prod compose has all $(wc -w <<< "$declared_keys" | tr -d ' ') AppHost-declared env keys"
echo "  - ${#PLAIN_COMPOSES[@]} plain composes mirror the prod compose's provider/feature keys"
