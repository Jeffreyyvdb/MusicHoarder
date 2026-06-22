#!/usr/bin/env bash
#
# Guards the Aspire-generated deploy composes against silent drift from the AppHost, and the
# hand-maintained self-host template against missing provider/feature keys.
#
# Both the prod and the per-PR preview composes are COMMITTED, generated artifacts:
#   MusicHoarder.AppHost/aspire-output/docker-compose.yaml          (DEPLOY_TARGET=swarm, musichoarder.app)
#   MusicHoarder.AppHost/aspire-output-preview/docker-compose.yaml  (DEPLOY_TARGET=compose, PR previews)
# Neither is regenerated at deploy time — Dokploy/the preview script deploy them verbatim — so when the
# AppHost gains an env mapping but a file isn't regenerated, that container never receives the setting.
# That is exactly how AI lyrics transcription shipped invisible in prod.
#
# This is a PRESENCE check (not a regenerate-and-diff): regenerating in CI is fragile against Aspire
# CLI/output formatting churn. We assert instead that every env KEY the AppHost declares is present in
# the generated compose(s) that should carry it, and that the self-host template mirrors the
# provider/feature keys.
#
# Fix on failure: regenerate and commit the affected compose —
#   aspire publish -o MusicHoarder.AppHost/aspire-output --non-interactive                   # prod
#   DEPLOY_TARGET=compose aspire publish -o MusicHoarder.AppHost/aspire-output-preview --non-interactive  # preview
# — and/or add the missing key to docker-compose.yml.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APPHOST="$ROOT/MusicHoarder.AppHost/AppHost.cs"
COMPOSE_EXT="$ROOT/MusicHoarder.AppHost/ComposeFileExtensions.cs"
PROD_COMPOSE="$ROOT/MusicHoarder.AppHost/aspire-output/docker-compose.yaml"
PREVIEW_COMPOSE="$ROOT/MusicHoarder.AppHost/aspire-output-preview/docker-compose.yaml"
SELFHOST_COMPOSE="$ROOT/docker-compose.yml"

for f in "$APPHOST" "$COMPOSE_EXT" "$PROD_COMPOSE" "$PREVIEW_COMPOSE" "$SELFHOST_COMPOSE"; do
  [ -f "$f" ] || { echo "::error::expected file not found: $f"; exit 1; }
done

fail=0

# Env keys declared in code, across the three forms that feed the generator:
#   - static     `.WithEnvironment("Key", value)`              (AppHost.cs)
#   - extension   `api.Environment["Key"] = "${VAR:-default}"`  (ComposeFileExtensions.cs)
#   - dynamic     `context.EnvironmentVariables["Key"] = ...`   (AppHost.cs context lambda)
declared_keys="$(
  {
    grep -oE '\.WithEnvironment\("[^"]+"' "$APPHOST"               | sed -E 's/.*\("([^"]+)".*/\1/'
    grep -oE 'api\.Environment\["[^"]+"\]' "$COMPOSE_EXT"          | sed -E 's/.*\["([^"]+)"\].*/\1/'
    grep -oE 'context\.EnvironmentVariables\["[^"]+"\]' "$APPHOST" | sed -E 's/.*\["([^"]+)"\].*/\1/'
  } | sort -u
)"

# Keys set only by the preview shaper (ConfigureForPreview) — present in the preview compose, absent
# from prod by design. Excluded from the prod check below.
PREVIEW_ONLY_KEYS="MusicEnricher__AutoStartPipeline Spotify__WishlistSyncIntervalMinutes WebAuthn__RpId Auth__OwnerSeedCredentialJson"

# present_in <compose-file> <key>  → 0 if the generated compose maps the key (`      Key: "..."`).
present_in() { grep -qE "^[[:space:]]+${2}:[[:space:]]" "$1"; }

# ---------------------------------------------------------------------------
# Check 1: each generated compose carries the declared keys it should.
#   - prod    → all declared keys except the preview-only ones
#   - preview → all declared keys (shared + preview-only)
# ---------------------------------------------------------------------------
check_generated() {
  local compose="$1" label="$2" skip="$3" key missing=()
  while IFS= read -r key; do
    [ -n "$key" ] || continue
    case " $skip " in *" $key "*) continue ;; esac
    present_in "$compose" "$key" || missing+=("$key")
  done <<< "$declared_keys"

  if [ "${#missing[@]}" -gt 0 ]; then
    fail=1
    echo "::error::${label} is out of sync with the AppHost — missing env mappings:"
    printf '  - %s\n' "${missing[@]}"
    echo "  Fix: regenerate it (see the header of this script) and commit."
    echo ""
  fi
}

check_generated "$PROD_COMPOSE"    "aspire-output/docker-compose.yaml (prod)"            "$PREVIEW_ONLY_KEYS"
check_generated "$PREVIEW_COMPOSE" "aspire-output-preview/docker-compose.yaml (preview)" ""

# ---------------------------------------------------------------------------
# Check 2: provider/feature parity with the hand-maintained self-host template.
#
# docker-compose.yml is the one compose NOT generated from the AppHost (it is a human-facing published
# artifact: end-user `:-` defaults, GHCR image refs, inline docs). Its provider/feature env must still
# mirror the prod compose. Scoped to the prefixes most likely to be forgotten when a provider/feature is
# wired; Spotify__/Auth__/Resend__ legitimately diverge (self-host uses a direct Spotify redirect).
# ---------------------------------------------------------------------------
PROD_ONLY_KEYS="MusicEnricher__DemoMediaDirectory"

prod_feature_keys="$(
  grep -oE '^[[:space:]]+(MusicEnricher|QualityGrading|LyricsTranscription)__[A-Za-z]+:' "$PROD_COMPOSE" \
    | sed -E 's/^[[:space:]]+([^:]+):/\1/' | sort -u
)"

missing_selfhost=()
while IFS= read -r key; do
  [ -n "$key" ] || continue
  case " $PROD_ONLY_KEYS $PREVIEW_ONLY_KEYS " in *" $key "*) continue ;; esac
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
echo "  - prod + preview composes carry their AppHost-declared env keys"
echo "  - self-host template mirrors the prod compose's provider/feature keys"
