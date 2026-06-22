#!/usr/bin/env bash
#
# Guards the Aspire-generated deploy composes against silent drift from the AppHost.
#
# All three deploy composes are COMMITTED, generated artifacts (DEPLOY_TARGET picks the shape):
#   MusicHoarder.AppHost/aspire-output/docker-compose.yaml          swarm    musichoarder.app (Dokploy stack)
#   MusicHoarder.AppHost/aspire-output-preview/docker-compose.yaml  compose  per-PR previews
#   docker-compose.yml                                              selfhost published self-host template
# None is regenerated at deploy time, so when the AppHost gains an env mapping but a file isn't
# regenerated, that container never receives the setting — how AI lyrics transcription shipped invisible.
#
# This is a PRESENCE check (not a regenerate-and-diff, which is fragile against Aspire CLI/output
# formatting churn): every env KEY the AppHost declares must appear in each generated compose that
# should carry it. Per-target shapers intentionally add/drop a few keys; those are listed below.
#
# Fix on failure: regenerate and commit the affected compose —
#   aspire publish -o MusicHoarder.AppHost/aspire-output --non-interactive                                  # swarm
#   DEPLOY_TARGET=compose  aspire publish -o MusicHoarder.AppHost/aspire-output-preview --non-interactive   # preview
#   DEPLOY_TARGET=selfhost aspire publish -o /tmp/sh && cp /tmp/sh/docker-compose.yaml docker-compose.yml   # self-host
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

# Keys a per-target shaper intentionally adds or drops, so they're skipped where they don't belong:
PREVIEW_ONLY="MusicEnricher__AutoStartPipeline Spotify__WishlistSyncIntervalMinutes WebAuthn__RpId Auth__OwnerSeedCredentialJson"  # ConfigureForPreview only
SELFHOST_ONLY="Spotify__OAuthRedirectBaseUrl"                                                                                     # ConfigureForSelfHost only
SELFHOST_DROPS="MusicEnricher__DemoMediaDirectory Spotify__OAuthRelayUrl Spotify__OAuthStateSigningKey SPOTIFY_OAUTH_STATE_SIGNING_KEY SPOTIFY_RETURN_ORIGIN_ALLOWLIST"  # relay/demo keys self-host removes (api + frontend)

# present_in <compose-file> <key>  → 0 if the generated compose maps the key (`      Key: "..."`).
present_in() { grep -qE "^[[:space:]]+${2}:[[:space:]]" "$1"; }

# check_generated <compose> <label> <skip-keys>
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

check_generated "$PROD_COMPOSE"    "aspire-output (swarm/prod)"        "$PREVIEW_ONLY $SELFHOST_ONLY"
check_generated "$PREVIEW_COMPOSE" "aspire-output-preview (preview)"   "$SELFHOST_ONLY"
check_generated "$SELFHOST_COMPOSE" "docker-compose.yml (self-host)"   "$PREVIEW_ONLY $SELFHOST_DROPS"

if [ "$fail" -ne 0 ]; then
  exit 1
fi

echo "compose drift check passed ✓"
echo "  - all three generated composes carry their AppHost-declared env keys"
