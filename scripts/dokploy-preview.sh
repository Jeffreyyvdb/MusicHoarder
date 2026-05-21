#!/usr/bin/env bash
# Provision / tear down a per-PR MusicHoarder preview stack on Dokploy.
#
# Why this exists: Dokploy's built-in Preview Deployments only support single "Application"
# services, not Docker Compose apps (https://github.com/Dokploy/dokploy/issues/2028). MusicHoarder
# is a 3-service compose stack, so we roll our own: one isolated compose stack per pull request,
# reachable at https://pr-<n>.<PREVIEW_BASE_DOMAIN>, torn down when the PR closes.
#
# Usage:
#   PR=123 ... ./scripts/dokploy-preview.sh provision
#   PR=123 ... ./scripts/dokploy-preview.sh destroy
#
# The Dokploy REST API mirrors its tRPC routes: POST https://<host>/api/<router>.<proc> with an
# x-api-key header and a JSON body. Idempotent: provision finds an existing pr-<n> compose by name
# (so a new commit on the PR redeploys the same stack instead of creating a duplicate).
#
# Required env:
#   DOKPLOY_URL                     base URL of the Dokploy instance (no trailing slash needed)
#   DOKPLOY_API_KEY                 x-api-key
#   DOKPLOY_PREVIEW_ENVIRONMENT_ID  environment the preview composes live in (a dedicated
#                                   "previews" environment/project — never the prod environment)
#   PR                              pull request number
#   PREVIEW_BASE_DOMAIN             e.g. preview.musichoarder.example.com (PR host = pr-<n>.<this>)
#   API_IMAGE, FRONTEND_IMAGE       full ghcr image refs incl. :pr-<n> tag  (provision only)
#   PREVIEW_POSTGRES_PASSWORD       throwaway db password                   (provision only)
#   PREVIEW_OWNER_EMAIL             owner account for magic-link sign-in     (provision only)
# Optional env (provision):
#   PREVIEW_SOURCE_DIR   default /srv/mh-preview/sample-source  (shared, read-only sample library)
#   PREVIEW_DEST_ROOT    default /srv/mh-preview                (per-PR dest = <root>/pr-<n>/dest)
#   PREVIEW_MAX_STACKS   default 5  (skip provisioning if this many pr-* composes already exist)
set -euo pipefail

CMD="${1:-}"
: "${DOKPLOY_URL:?DOKPLOY_URL is required}"
: "${DOKPLOY_API_KEY:?DOKPLOY_API_KEY is required}"
: "${DOKPLOY_PREVIEW_ENVIRONMENT_ID:?DOKPLOY_PREVIEW_ENVIRONMENT_ID is required}"
: "${PR:?PR is required}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"  # robust regardless of caller CWD
BASE="${DOKPLOY_URL%/}"
NAME="pr-${PR}"                 # compose display name within the previews environment
APP_NAME="mh-pr-${PR}"          # docker stack / isolated-network name (must be globally unique)

# _curl <proc> <curl-args...>  ->  prints response body, fails on non-2xx.
# Dokploy's trpc-to-openapi layer maps queries to GET and mutations to POST, so the verb matters:
# `api` (POST) is for mutations, `get` is for read queries like project.all.
_curl() {
  local proc="$1"; shift
  local out status
  out="$(mktemp)"
  # --fail-with-body (curl >=7.76) keeps the response body on HTTP errors so Dokploy's validation
  # message is visible, unlike plain -f which discards it.
  status="$(curl --fail-with-body -sS --max-time 120 -o "$out" -w '%{http_code}' \
    -H "x-api-key: ${DOKPLOY_API_KEY}" "$@" \
    "${BASE}/api/${proc}" 2>"$out".err || true)"
  if [[ ! "$status" =~ ^2 ]]; then
    echo "::error::Dokploy ${proc} failed (HTTP ${status:-???})" >&2
    cat "$out" "$out".err >&2 || true
    rm -f "$out" "$out".err
    return 1
  fi
  cat "$out"
  rm -f "$out" "$out".err
}

# api <router>.<proc> [json-body]  -> POST mutation
api() { _curl "$1" -X POST -H 'Content-Type: application/json' --data "${2:-{}}"; }

# get <router>.<proc>  -> GET query
get() { _curl "$1"; }

# Both helpers take a pre-fetched project.all payload on stdin so a fresh provision needs only one
# API round-trip. project.all over raw REST returns a bare array; the MCP wrapper nests it under
# .data — `(.data? // .)` tolerates both so lookups never silently miss (→ duplicate stacks).
find_compose_id() {
  jq -r --arg env "$DOKPLOY_PREVIEW_ENVIRONMENT_ID" --arg name "$NAME" '
    [ (.data? // .)[].environments[] | select(.environmentId == $env) | .compose[]?
      | select(.name == $name) | .composeId ] | first // empty'
}

count_preview_stacks() {
  jq -r --arg env "$DOKPLOY_PREVIEW_ENVIRONMENT_ID" '
    [ (.data? // .)[].environments[] | select(.environmentId == $env) | .compose[]?
      | select(.name | startswith("pr-")) ] | length'
}

provision() {
  : "${PREVIEW_BASE_DOMAIN:?PREVIEW_BASE_DOMAIN is required}"
  : "${API_IMAGE:?API_IMAGE is required}"
  : "${FRONTEND_IMAGE:?FRONTEND_IMAGE is required}"
  : "${PREVIEW_POSTGRES_PASSWORD:?PREVIEW_POSTGRES_PASSWORD is required}"
  : "${PREVIEW_OWNER_EMAIL:?PREVIEW_OWNER_EMAIL is required}"
  local source_dir="${PREVIEW_SOURCE_DIR:-/srv/mh-preview/sample-source}"
  local dest_root="${PREVIEW_DEST_ROOT:-/srv/mh-preview}"
  local max="${PREVIEW_MAX_STACKS:-5}"
  local host="${NAME}.${PREVIEW_BASE_DOMAIN}"
  local public_url="https://${host}"

  local compose_file
  compose_file="$(cat "${SCRIPT_DIR}/../docker-compose.preview.yaml")"

  # Newline-separated KEY=value, exactly as Dokploy stores compose env (filled into ${...}).
  local env_block
  env_block="$(cat <<EOF
API_IMAGE=${API_IMAGE}
FRONTEND_IMAGE=${FRONTEND_IMAGE}
POSTGRES_PASSWORD=${PREVIEW_POSTGRES_PASSWORD}
OWNER_EMAIL=${PREVIEW_OWNER_EMAIL}
DEMO_USER_EMAIL=demo@musichoarder.local
FRONTEND_PUBLIC_BASE_URL=${public_url}
SOURCE_DIRECTORY=${source_dir}
DESTINATION_DIRECTORY=${dest_root}/${NAME}/dest
ACOUSTID_API_KEY=
SPOTIFY_CLIENT_ID=
SPOTIFY_CLIENT_SECRET=
RESEND_API_KEY=
RESEND_FROM_ADDRESS=noreply@musichoarder.local
EOF
)"

  # One project.all fetch serves both the existing-stack lookup and the cap count.
  local projects compose_id
  projects="$(get project.all)"
  compose_id="$(printf '%s' "$projects" | find_compose_id)"

  if [[ -z "$compose_id" ]]; then
    local existing
    existing="$(printf '%s' "$projects" | count_preview_stacks)"
    if (( existing >= max )); then
      # Non-fatal: the author's PR shouldn't show a red check because *other* PRs hold open
      # previews. Skip cleanly (no preview_url → the comment step is a no-op) and let genuine
      # provisioning errors below still fail loudly.
      echo "::warning::Preview stack cap reached (${existing}/${max}). Skipping preview for ${NAME} — close an open PR's preview or raise PREVIEW_MAX_STACKS." >&2
      return 0
    fi
    echo "Creating compose '${NAME}' (appName=${APP_NAME})..."
    compose_id="$(api compose.create "$(jq -n \
      --arg name "$NAME" --arg env "$DOKPLOY_PREVIEW_ENVIRONMENT_ID" --arg appName "$APP_NAME" \
      '{name:$name, environmentId:$env, appName:$appName, composeType:"docker-compose"}')" \
      | jq -r '.composeId // .data.composeId')"
    if [[ -z "$compose_id" || "$compose_id" == "null" ]]; then
      echo "::error::compose.create did not return a composeId" >&2
      return 1
    fi
  else
    echo "Reusing existing compose '${NAME}' (${compose_id}) — redeploying."
  fi

  echo "Updating compose source + env (isolated)..."
  api compose.update "$(jq -n \
    --arg id "$compose_id" --arg file "$compose_file" --arg env "$env_block" \
    '{composeId:$id, sourceType:"raw", composeType:"docker-compose",
      composeFile:$file, env:$env,
      isolatedDeployment:true, isolatedDeploymentsVolume:true, randomize:false}')" >/dev/null

  # Attach the PR domain to the frontend service (idempotent: ignore "already exists").
  echo "Ensuring domain ${host} -> frontend:8001 ..."
  api domain.create "$(jq -n \
    --arg id "$compose_id" --arg host "$host" \
    '{composeId:$id, host:$host, serviceName:"frontend", port:8001,
      https:true, certificateType:"letsencrypt", domainType:"compose"}')" >/dev/null \
    || echo "::notice::domain.create returned non-2xx (likely already exists) — continuing."

  echo "Deploying..."
  api compose.deploy "$(jq -n --arg id "$compose_id" '{composeId:$id}')" >/dev/null

  echo "Preview ready: ${public_url}"
  # Surface the URL to the workflow (for the PR comment).
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    echo "preview_url=${public_url}" >>"$GITHUB_OUTPUT"
  fi
}

destroy() {
  local compose_id
  compose_id="$(get project.all | find_compose_id)"
  if [[ -z "$compose_id" ]]; then
    echo "No preview compose named '${NAME}' found — nothing to destroy."
    return 0
  fi
  echo "Deleting compose '${NAME}' (${compose_id}) and its volumes..."
  api compose.delete "$(jq -n --arg id "$compose_id" '{composeId:$id, deleteVolumes:true}')" >/dev/null
  echo "Preview '${NAME}' destroyed."
}

case "$CMD" in
  provision) provision ;;
  destroy)   destroy ;;
  *) echo "Usage: PR=<n> $0 {provision|destroy}" >&2; exit 64 ;;
esac
