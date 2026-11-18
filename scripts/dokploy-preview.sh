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
#   ...        ./scripts/dokploy-preview.sh reap      (DRY_RUN=1 lists what it would remove)
#
# What a preview leaves on the host: Dokploy names everything after the compose's appName,
# mh-pr-<n>-<suffix>. Its containers are mh-pr-<n>-<suffix>-<service>-1, its volumes
# mh-pr-<n>-<suffix>_<volume>, and, because the stack is an isolated deployment, a bridge network
# named mh-pr-<n>-<suffix> that Dokploy creates with `docker network create` outside compose.
# compose.delete never removes that network (it only disconnects Traefik and runs `compose down`,
# which leaves external networks alone), and every bridge takes a /16 or /20 from Docker's default
# address pools, of which there are about 30. So destroy removes the leftovers by name as well, and
# reap does the same for every PR that is no longer open.
#
# The Dokploy REST API mirrors its tRPC routes at https://<host>/api/<router>.<proc> with an
# x-api-key header — mutations are POST with a JSON body, read queries (e.g. project.all) are GET.
# Idempotent: provision finds an existing pr-<n> compose by name (so a new commit on the PR
# redeploys the same stack instead of creating a duplicate).
#
# Required env:
#   DOKPLOY_URL                     base URL of the Dokploy instance (no trailing slash needed)
#   DOKPLOY_API_KEY                 x-api-key
#   DOKPLOY_PREVIEW_ENVIRONMENT_ID  environment the preview composes live in (a dedicated
#                                   "previews" environment/project — never the prod environment)
#   PR                              pull request number (provision and destroy)
#   PREVIEW_BASE_DOMAIN             e.g. preview.musichoarder.app (PR host = pr-<n>.<this>; also the
#                                   WebAuthn relying-party id, shared across all pr-<n> subdomains)
#   API_IMAGE, FRONTEND_IMAGE       full ghcr image refs incl. :pr-<n> tag  (provision only)
#   PREVIEW_POSTGRES_PASSWORD       throwaway db password                   (provision only)
#   PREVIEW_OWNER_EMAIL             owner account for magic-link sign-in     (provision only)
# Optional env (provision):
#   PREVIEW_OWNER_SEED_CREDENTIAL   minified JSON of the owner's pre-registered passkey (public-key
#                                   material only) seeded into the empty preview DB so the owner can
#                                   log in via passkey without re-registering. Register once against
#                                   RpId=${PREVIEW_BASE_DOMAIN}, then capture the WebAuthnCredentials
#                                   row. Empty → passkey seed skipped (magic-link still works).
#   PREVIEW_SOURCE_DIR   default /srv/mh-preview/sample-source  (shared, read-only sample library)
#   PREVIEW_MAX_STACKS   default 5  (skip provisioning if this many pr-* composes already exist)
# Required env (destroy, reap): GH_TOKEN and GITHUB_REPOSITORY, to ask GitHub whether a PR is open.
#   PREVIEW_SPOTIFY_CLIENT_ID / PREVIEW_SPOTIFY_CLIENT_SECRET   Spotify app creds so the preview can complete the
#                                   OAuth token exchange. Empty → Spotify connect disabled in the preview.
#   PREVIEW_SPOTIFY_OAUTH_RELAY_URL  the single registered relay URL (https://<prod-frontend>/api/spotify/relay),
#                                   used verbatim as redirect_uri. The prod relay's return-origin allowlist must
#                                   include *.${PREVIEW_BASE_DOMAIN} so it can bounce back to this preview.
#   PREVIEW_SPOTIFY_OAUTH_STATE_KEY  shared HMAC key signing the OAuth state; MUST equal the prod relay's key.
#   PREVIEW_QUALITY_GRADING_API_KEY  OpenRouter (or other OpenAI-compatible) key for AI quality grading.
#                                   Empty → grading is a no-op in the preview.
#   PREVIEW_QUALITY_GRADING_MODEL    model id; default deepseek/deepseek-v4-flash.
#   PREVIEW_QUALITY_GRADING_BASE_URL OpenAI-compatible base URL; default https://openrouter.ai/api/v1.
#   PREVIEW_LYRICS_TRANSCRIPTION_API_KEY  Whisper key (Groq recommended) for experimental AI lyrics
#                                   transcription. Empty → the feature stays hidden in the preview UI.
#   PREVIEW_LYRICS_TRANSCRIPTION_MODEL / _BASE_URL / _LLM_MODEL  optional overrides; default to
#                                   Groq whisper-large-v3 + google/gemini-2.5-flash-lite.
set -euo pipefail

CMD="${1:-}"
: "${DOKPLOY_URL:?DOKPLOY_URL is required}"
: "${DOKPLOY_API_KEY:?DOKPLOY_API_KEY is required}"
: "${DOKPLOY_PREVIEW_ENVIRONMENT_ID:?DOKPLOY_PREVIEW_ENVIRONMENT_ID is required}"
: "${PR:=}"
[[ "$CMD" == reap || "$PR" =~ ^[0-9]+$ ]] || { echo "PR is required (a pull request number)" >&2; exit 64; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"  # robust regardless of caller CWD
BASE="${DOKPLOY_URL%/}"
NAME="pr-${PR}"                 # compose display name within the previews environment (lookup key)
APP_NAME="mh-pr-${PR}"          # appName prefix; Dokploy appends a random suffix (e.g. mh-pr-40-ab12)

# _curl <proc> <curl-args...>  ->  prints response body, fails on non-2xx.
# Dokploy's trpc-to-openapi layer maps queries to GET and mutations to POST, so the verb matters:
# `api` (POST) is for mutations, `get` is for read queries like project.all.
_curl() {
  local proc="$1"; shift
  local out status rc=0
  out="$(mktemp)"
  # --fail-with-body (curl >=7.76) keeps the response body on HTTP errors so Dokploy's validation
  # message is visible, unlike plain -f which discards it.
  status="$(curl --fail-with-body -sS --max-time 120 -o "$out" -w '%{http_code}' \
    -H "x-api-key: ${DOKPLOY_API_KEY}" "$@" \
    "${BASE}/api/${proc}" 2>"$out".err)" || rc=$?
  if [[ "$rc" -ne 0 || ! "$status" =~ ^2 ]]; then
    echo "::${CURL_ERROR_LEVEL:-error}::Dokploy ${proc} failed (HTTP ${status:-???})" >&2
    cat "$out" "$out".err >&2 || true
    rm -f "$out" "$out".err
    return 1
  fi
  cat "$out"
  rm -f "$out" "$out".err
}

# api <router>.<proc> [json-body]  -> POST mutation.
# NB: don't write `${2:-{}}` — bash parses that as `${2:-{}` plus a literal `}`, appending a stray
# brace to every body and producing "Invalid JSON". Default to an empty object explicitly.
api() {
  local body="${2:-}"
  [ -n "$body" ] || body='{}'
  _curl "$1" -X POST -H 'Content-Type: application/json' --data "$body"
}

# get <router>.<proc>  -> GET query
# A query always answers with JSON, so an empty body is a failed read, never an empty list.
get() {
  local body
  body="$(_curl "$1")" || return 1
  [[ "$body" =~ [^[:space:]] ]] || { echo "::${CURL_ERROR_LEVEL:-error}::Dokploy $1 answered with an empty body" >&2; return 1; }
  printf '%s\n' "$body"
}

# Both helpers take a pre-fetched project.all payload on stdin so a fresh provision needs only one
# API round-trip. The payload may be a bare array or nested under .data depending on the response
# shape — `(.data? // .)` tolerates both so lookups never silently miss (which would create
# duplicate stacks).
find_compose_id() {
  jq -r --arg env "$DOKPLOY_PREVIEW_ENVIRONMENT_ID" --arg name "$NAME" '
    [ (.data? // .)[].environments[] | select(.environmentId == $env) | .compose[]?
      | select(.name == $name) | .composeId ] | first // empty'
}

# composeStatus of one compose from a project.all payload on stdin ("gone" when it is not listed).
compose_status() {
  jq -r --arg id "$1" '
    [ (.data? // .)[].environments[].compose[]? | select(.composeId == $id) | .composeStatus // "unknown" ]
    | first // "gone"'
}

count_preview_stacks() {
  jq -r --arg env "$DOKPLOY_PREVIEW_ENVIRONMENT_ID" '
    [ (.data? // .)[].environments[] | select(.environmentId == $env) | .compose[]?
      | select(.name | startswith("pr-")) ] | length'
}

# The newest deployment of a compose, as one JSON object (empty when it has none).
latest_deployment() {
  get "deployment.allByCompose?composeId=$1" | jq -c '((.data? // .) | sort_by(.createdAt) | last) // empty'
}

# Waits for the deployment queued after <before> to end, then for <url>/login to answer.
# Fails with the reason when the deployment errors, or when it has not finished in time (the queue
# is shared with every other app on the server, and one stuck job holds up all of them).
wait_for_deploy() {
  local compose_id="$1" before="$2" url="$3"
  local timeout="${PREVIEW_DEPLOY_TIMEOUT_SECONDS:-1200}" waited=0 latest="" id="" status=""
  echo "Waiting for the deployment to finish (up to ${timeout}s)..."
  while :; do
    latest="$(latest_deployment "$compose_id" || true)"
    id="$(jq -r '.deploymentId // empty' <<<"${latest:-null}")"
    status="$(jq -r '.status // empty' <<<"${latest:-null}")"
    if [[ -n "$id" && "$id" != "$before" ]]; then
      case "$status" in
        done) break ;;
        error|cancelled)
          echo "::error::Dokploy deployment ${id} ended '${status}': $(jq -r '.errorMessage // "no message"' <<<"$latest")" >&2
          return 1 ;;
      esac
    fi
    if (( waited >= timeout )); then
      local state="not started yet"
      [[ -n "$id" && "$id" != "$before" ]] && state="$status"
      echo "::error::The preview deployment did not finish in ${timeout}s (state: ${state}). Dokploy runs one deployment at a time; check its queue for a stuck job." >&2
      return 1
    fi
    sleep 15
    waited=$((waited + 15))
  done

  echo "Deployment ${id} done; waiting for ${url} to answer..."
  local answered=0
  for _ in $(seq 1 40); do
    if curl -fsS -o /dev/null --max-time 10 "${url}/login"; then answered=1; break; fi
    sleep 15
  done
  if (( ! answered )); then
    echo "::error::${url}/login did not answer within 10 minutes of the deployment finishing." >&2
    return 1
  fi
}

provision() {
  : "${PREVIEW_BASE_DOMAIN:?PREVIEW_BASE_DOMAIN is required}"
  : "${API_IMAGE:?API_IMAGE is required}"
  : "${FRONTEND_IMAGE:?FRONTEND_IMAGE is required}"
  : "${PREVIEW_POSTGRES_PASSWORD:?PREVIEW_POSTGRES_PASSWORD is required}"
  : "${PREVIEW_OWNER_EMAIL:?PREVIEW_OWNER_EMAIL is required}"
  local source_dir="${PREVIEW_SOURCE_DIR:-/srv/mh-preview/sample-source}"
  local max="${PREVIEW_MAX_STACKS:-5}"
  local host="${NAME}.${PREVIEW_BASE_DOMAIN}"
  local public_url="https://${host}"

  # Optional YouTube cookies for the wishlist downloader: when a host cookies file is configured,
  # bind it in and tell the API to use it; otherwise leave both blank so the compose binds /dev/null
  # and the API runs cookie-less. Keeps cookies out of the repo (host file, like the sample source).
  local cookies_host="${PREVIEW_YTDLP_COOKIES_HOST_PATH:-}"
  local cookies_path=""
  if [[ -n "$cookies_host" ]]; then
    cookies_path="/data/cookies/youtube.txt"
  fi

  # The preview compose is generated from the AppHost (DEPLOY_TARGET=compose), same source as the prod
  # stack — see MusicHoarder.AppHost/ComposeFileExtensions.cs (ConfigureForPreview). Committed so this
  # script (and Dokploy) deploy it verbatim; CI regenerates + checks it.
  local compose_file
  compose_file="$(cat "${SCRIPT_DIR}/../MusicHoarder.AppHost/aspire-output-preview/docker-compose.yaml")"

  # Newline-separated KEY=value, exactly as Dokploy stores compose env (filled into ${...}).
  local env_block
  env_block="$(cat <<EOF
API_IMAGE=${API_IMAGE}
FRONTEND_IMAGE=${FRONTEND_IMAGE}
POSTGRES_PASSWORD=${PREVIEW_POSTGRES_PASSWORD}
OWNER_EMAIL=${PREVIEW_OWNER_EMAIL}
DEMO_USER_EMAIL=demo@musichoarder.local
FRONTEND_PUBLIC_BASE_URL=${public_url}
WEBAUTHN_RP_ID=${PREVIEW_BASE_DOMAIN}
OWNER_SEED_CREDENTIAL_JSON=${PREVIEW_OWNER_SEED_CREDENTIAL:-}
SOURCE_DIRECTORY=${source_dir}
DESTINATION_DIRECTORY=/music/destination
# The API listens on API_PORT (HTTP_PORTS); the generated compose references it for the published
# port, the /alive healthcheck, and the frontend's MUSICHOARDER_API_URL.
API_PORT=8080
# No demo media on previews — empty makes the seeder fall back to the synthetic demo seed.
DEMO_MEDIA_DIRECTORY=
# Frontend: analytics off, and the relay return-origin allowlist empty (previews route OAuth through
# the prod relay; they are not the relay themselves).
SPOTIFY_RETURN_ORIGIN_ALLOWLIST=
PUBLIC_UMAMI_SRC=
PUBLIC_UMAMI_WEBSITE_ID=
PUBLIC_UMAMI_RECORDER_SRC=
ACOUSTID_API_KEY=
SPOTIFY_CLIENT_ID=${PREVIEW_SPOTIFY_CLIENT_ID:-}
SPOTIFY_CLIENT_SECRET=${PREVIEW_SPOTIFY_CLIENT_SECRET:-}
SPOTIFY_OAUTH_RELAY_URL=${PREVIEW_SPOTIFY_OAUTH_RELAY_URL:-}
SPOTIFY_OAUTH_STATE_KEY=${PREVIEW_SPOTIFY_OAUTH_STATE_KEY:-}
# Wishlist downloads: enabled so a tester can use the manual "Download" trigger, but EXPLICIT-only —
# no background auto-download (AUTO=false) and no auto like-sync (interval 0), so a PR preview never
# starts fetching/syncing on its own.
ENABLE_WISHLIST_DOWNLOADS=true
AUTO_DOWNLOAD_WISHLIST=false
WISHLIST_SYNC_INTERVAL_MINUTES=0
# YouTube bot-check workaround for the preview datacenter IP (repo variable, optional). Pass raw
# yt-dlp flags here, e.g. --extractor-args youtube:player_client=tv -- empty means plain yt-dlp.
YTDLP_EXTRA_ARGS=${PREVIEW_YTDLP_EXTRA_ARGS:-}
# YouTube cookies: host file path bound into the container + the in-container path the API reads.
# Both blank unless PREVIEW_YTDLP_COOKIES_HOST_PATH is set (then cookies_path is the mount target).
YTDLP_COOKIES_HOST_PATH=${cookies_host}
YTDLP_COOKIES_PATH=${cookies_path}
QUALITY_GRADING_API_KEY=${PREVIEW_QUALITY_GRADING_API_KEY:-}
QUALITY_GRADING_MODEL=${PREVIEW_QUALITY_GRADING_MODEL:-deepseek/deepseek-v4-flash}
QUALITY_GRADING_BASE_URL=${PREVIEW_QUALITY_GRADING_BASE_URL:-https://openrouter.ai/api/v1}
LYRICS_TRANSCRIPTION_API_KEY=${PREVIEW_LYRICS_TRANSCRIPTION_API_KEY:-}
LYRICS_TRANSCRIPTION_BASE_URL=${PREVIEW_LYRICS_TRANSCRIPTION_BASE_URL:-https://api.groq.com/openai/v1}
LYRICS_TRANSCRIPTION_MODEL=${PREVIEW_LYRICS_TRANSCRIPTION_MODEL:-whisper-large-v3}
LYRICS_TRANSCRIPTION_LLM_MODEL=${PREVIEW_LYRICS_TRANSCRIPTION_LLM_MODEL:-google/gemini-2.5-flash-lite}
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

  # The newest deployment before this one, so the wait below can tell ours apart from it.
  # A failed read must not pass for "no deployment yet", or the previous one would count as ours.
  local before i
  for i in 1 2 3; do
    before="$(latest_deployment "$compose_id" | jq -r '.deploymentId // empty')" && break
    (( i < 3 )) || { echo "::error::Could not read the deployments of '${NAME}' before deploying." >&2; return 1; }
    sleep 5
  done

  echo "Deploying..."
  api compose.deploy "$(jq -n --arg id "$compose_id" '{composeId:$id}')" >/dev/null

  # compose.deploy only queues the deployment. Dokploy runs one deployment at a time, so wait for
  # ours to finish and for the site to answer before calling the preview ready.
  wait_for_deploy "$compose_id" "$before" "$public_url"

  echo "Preview ready: ${public_url}"
  # Surface the URL to the workflow (for the PR comment).
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    echo "preview_url=${public_url}" >>"$GITHUB_OUTPUT"
  fi
}

# Every preview resource on the host, one tab-separated line each: <kind> <pr> <name> <ref>, where
# kind is container, volume or network, and ref is what removing it takes (a container id, the
# volume name, or the network name). Matching is anchored on the appName shape, so nothing that is
# not a preview (production, other projects) can match, and mh-pr-69- never matches mh-pr-699-.
list_preview_resources() {
  # Each read is checked by hand: callers run this where `set -e` is off (inside `||`), and a failed
  # read that came back empty would otherwise look like "nothing left". The payloads go to jq as
  # files, since a host's volume list can outgrow a command-line argument.
  local dir rc=0
  dir="$(mktemp -d)"
  { get docker.getContainers >"$dir/c" &&
    get dockerVolume.getVolumes >"$dir/v" &&
    # Docker networks Dokploy has not imported yet (the isolated ones never are), and the ones it has.
    get network.networksToSync >"$dir/u" &&
    get network.all >"$dir/t" &&
    jq -rn --slurpfile c "$dir/c" --slurpfile v "$dir/v" --slurpfile u "$dir/u" --slurpfile t "$dir/t" '
      def rows($f): ($f[0] | (.data? // .)) // [];
      def pr: capture("^mh-pr-(?<pr>[0-9]+)-").pr;
      ( rows($c)[] | (.name // "") as $n | select($n | test("^mh-pr-[0-9]+-[a-z0-9]+-"))
          | ["container", ($n | pr), $n, .containerId] ),
      ( rows($v)[] | (.Name // "") as $n | select($n | test("^mh-pr-[0-9]+-[a-z0-9]+_"))
          | ["volume", ($n | pr), $n, $n] ),
      ( ((rows($u).importable // [])[].name // ""), (rows($t)[].name // "")
          | select(test("^mh-pr-[0-9]+-[a-z0-9]+(_[a-zA-Z0-9_.-]+)?$"))
          | ["network", pr, ., .] )
      | @tsv' | sort -u; } || rc=1
  rm -rf "$dir"
  return "$rc"
}

# Removes a Docker network by name through Dokploy, which only removes networks it tracks: import it
# first when it is not tracked yet. Docker refuses while anything is still attached to it.
remove_network() {
  local name="$1" pr="$2" sync tracked id found
  CURL_ERROR_LEVEL=notice api network.import "$(jq -n --arg n "$name" '{names:[$n]}')" >/dev/null || true
  # Untracked first, then tracked: a network only moves from untracked to tracked (an import running
  # beside this one), so one missing from both lists, read in this order, is gone from Docker.
  sync="$(get network.networksToSync)" || return 1
  tracked="$(get network.all)" || return 1
  id="$(jq -r --arg n "$name" '[(.data? // .)[] | select(.name == $n) | .networkId] | first // empty' <<<"$tracked")" || return 1
  if [[ -z "$id" ]]; then
    found="$(jq -r --arg n "$name" '((.data? // .).importable // []) | any(.name == $n)' <<<"$sync")" || return 1
    [[ "$found" == false ]] && { echo "Network ${name} is already gone."; return 0; }
    echo "::warning::network ${name} could not be imported into Dokploy" >&2
    return 1
  fi
  check_closed "$pr" || return $?
  CURL_ERROR_LEVEL=warning api network.remove "$(jq -n --arg id "$id" '{networkId:$id}')" >/dev/null
}

# Removes what a preview left behind after its compose is gone: containers first, then volumes,
# then the network they were attached to. A network that still has endpoints (a compose.delete
# that is still stopping the stack) is retried for two minutes before giving up.
purge_leftovers() {
  local pr="$1" all projects live resources kind rpr name ref failed=0 networks=()
  all="$(list_preview_resources)" || return 1
  # Read after the inventory: a pr-<n> compose that exists now belongs to a PR reopened since its
  # old stack was deleted, and anything listed may be part of its new stack. Leave all of it; the
  # PR's next close removes what is left.
  projects="$(get project.all)" || return 1
  live="$(find_compose_id <<<"$projects")" || return 1
  if [[ -n "$live" ]]; then
    echo "::warning::${NAME} has a compose again (${live}); leaving its Docker resources alone." >&2
    return 10
  fi
  # Every removal below comes right after a read of the PR's state, however long the one before
  # it took, so a PR reopened part-way keeps what is left.
  resources="$(awk -F'\t' -v pr="$pr" '$2 == pr' <<<"$all")"
  [[ -n "$resources" ]] || { echo "No Docker leftovers for ${NAME}."; return 0; }
  while IFS=$'\t' read -r kind rpr name ref; do
    [[ "$kind" == network ]] || check_closed "$pr" || return $?
    case "$kind" in
      container)
        echo "Removing leftover container ${name}"
        CURL_ERROR_LEVEL=warning api docker.removeContainer "$(jq -n --arg id "$ref" '{containerId:$id}')" >/dev/null || failed=1 ;;
      volume)
        echo "Removing leftover volume ${name}"
        CURL_ERROR_LEVEL=warning api dockerVolume.removeVolume "$(jq -n --arg v "$ref" '{volumeName:$v}')" >/dev/null || failed=1 ;;
      network) networks+=("$name") ;;
    esac
  done <<<"$resources"
  local net i
  for net in "${networks[@]}"; do
    echo "Removing network ${net}"
    local rc
    for i in $(seq 1 12); do
      rc=0
      remove_network "$net" "$pr" || rc=$?
      (( rc == 0 )) && continue 2
      (( rc == 10 )) && return 10
      sleep 10
    done
    echo "::error::network ${net} is still there two minutes after its stack was deleted." >&2
    failed=1
  done
  return "$failed"
}

# The PR's state on GitHub right now: OPEN, CLOSED, MERGED, or UNKNOWN when it cannot be read.
pr_state() {
  gh pr view "$1" --repo "$GITHUB_REPOSITORY" --json state -q .state 2>/dev/null || echo UNKNOWN
}

# Succeeds while the PR is closed or merged. Otherwise says why the preview stays and fails, with 10
# for an open PR (keeping it is right) and 1 when the state cannot be read. destroy passes the 10
# on, so reap can tell a kept preview from a failed one.
check_closed() {
  local state
  state="$(pr_state "$1")"
  case "$state" in
    CLOSED|MERGED) return 0 ;;
    OPEN) echo "PR #$1 is open; keeping its preview."; return 10 ;;
    *) echo "::warning::Could not read the state of PR #$1; keeping its preview." >&2; return 1 ;;
  esac
}

destroy() {
  : "${GH_TOKEN:?GH_TOKEN is required}"
  : "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
  local projects compose_id gone=1
  check_closed "$PR" || return $?
  projects="$(get project.all)" || return 1
  compose_id="$(find_compose_id <<<"$projects")" || return 1
  if [[ -z "$compose_id" ]]; then
    echo "No preview compose named '${NAME}' found."
  else
    # A PR closed mid-deploy cancels the workflow run, not Dokploy's deployment. Deleting while it
    # still runs `compose up` would let the stack come back after the delete, so let it end first.
    # Deployments still queued are dropped first: one that started between the last status read
    # and the delete would re-attach Traefik to the network afterwards. With the queue empty, only
    # a deployment already running is left. Dokploy's worker marks the compose itself as running
    # as its first write, before the deployment row exists, so both are read (after a pause for
    # that write). A failed read counts as still running. The queue is only cleared for a PR that
    # is closed right now, so a reopened PR's deployment is not dropped.
    check_closed "$PR" || return $?
    api compose.cleanQueues "$(jq -n --arg id "$compose_id" '{composeId:$id}')" >/dev/null || return 1
    sleep 5
    # A compose marked running with no running deployment row for two minutes is a leftover of a
    # Dokploy restart mid-deploy (a real deployment writes its row within seconds), not a deployment.
    # Likewise a deployment row still running 30 minutes after it started: provision gives a
    # deployment 20, so this one ended without Dokploy managing to record it.
    local latest status age waited=0 rowless=0
    while (( waited < 600 )); do
      if latest="$(latest_deployment "$compose_id")" && projects="$(get project.all)"; then
        [[ -n "$latest" ]] || latest='{"status":"none"}'
        status="$(jq -r '.status // "unknown"' <<<"$latest")" || status=unknown
        if [[ "$status" == running ]]; then
          age="$(jq -r '(.startedAt // .createdAt // "") | sub("\\.[0-9]+Z$"; "Z")
            | try (now - fromdateiso8601 | floor) catch -1' <<<"$latest")" || age=-1
          if (( age >= 1800 )); then
            echo "The running deployment of '${NAME}' started $((age / 60)) minutes ago; treating it as stale."
            break
          fi
        fi
        if [[ "$status" != running && "$status" != unknown ]]; then
          status="$(compose_status "$compose_id" <<<"$projects")" || status=unknown
          [[ "$status" != unknown ]] || rowless=0
          if [[ "$status" == running ]]; then
            rowless=$((rowless + 15))
            if (( rowless > 120 )); then
              echo "'${NAME}' is marked running but has had no running deployment for two minutes; treating the mark as stale."
              break
            fi
          fi
        else
          rowless=0
        fi
      else
        status=unknown
        rowless=0
      fi
      [[ "$status" == running || "$status" == unknown ]] || break
      (( waited == 0 )) && echo "A deployment of '${NAME}' may still be running (${status}); waiting for it to end..."
      sleep 15
      waited=$((waited + 15))
    done
    # A deployment that ends after the delete re-attaches Traefik to the stack's network, and Dokploy
    # has no call to detach it, so that network could never be removed. Leave the stack to a later
    # cleanup run instead.
    if (( waited >= 600 )); then
      echo "::error::'${NAME}' still shows a running deployment (${status}) after 10 minutes; keeping it for the next cleanup run." >&2
      return 1
    fi
    # Asked again after the wait: a PR reopened meanwhile redeploys this same compose.
    check_closed "$PR" || return $?
    echo "Deleting compose '${NAME}' (${compose_id}) and its volumes..."
    # compose.delete stops the stack and removes its volumes before it answers, which takes longer
    # than the proxy in front of Dokploy waits: the proxy answers 520 at ~30s while the delete
    # carries on and completes. So the reply alone proves nothing either way — the compose being
    # gone from project.all is what counts.
    if ! CURL_ERROR_LEVEL=warning api compose.delete "$(jq -n --arg id "$compose_id" '{composeId:$id, deleteVolumes:true}')" >/dev/null; then
      echo "compose.delete gave no clean answer; checking whether '${NAME}' is gone..."
      local i left
      gone=0
      for i in $(seq 1 24); do
        sleep 10
        # A failed or unreadable read prints nothing, which must not pass for "gone".
        projects="$(get project.all)" && left="$(find_compose_id <<<"$projects")" || continue
        if [[ -z "$left" ]]; then gone=1; break; fi
      done
      if (( ! gone )); then
        echo "::error::Preview '${NAME}' (${compose_id}) still exists 4 minutes after compose.delete." >&2
        return 1
      fi
    fi
    echo "Compose '${NAME}' deleted."
  fi
  # Also when there was no compose: a delete that failed half-way, or one made before this cleanup
  # existed, leaves the network (and maybe containers and volumes) with nothing pointing at them.
  purge_leftovers "$PR" || return $?
  echo "Preview '${NAME}' destroyed."
}

# Destroys every preview whose PR is not open: composes in the previews environment, and Docker
# leftovers on the host that no compose owns any more. With DRY_RUN=1 it only prints the plan.
reap() {
  : "${GH_TOKEN:?GH_TOKEN is required}"
  : "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
  local composes resources prs pr state failed=0
  local projects
  projects="$(get project.all)"
  composes="$(jq -r --arg env "$DOKPLOY_PREVIEW_ENVIRONMENT_ID" '
    (.data? // .)[].environments[] | select(.environmentId == $env) | .compose[]?
    | .name | select(test("^pr-[0-9]+$")) | ltrimstr("pr-")' <<<"$projects")"
  resources="$(list_preview_resources)"

  echo "Preview resources on the host:"
  printf 'KIND\tPR\tNAME\n%s\n' "$(cut -f1-3 <<<"$resources")" | column -t -s$'\t'
  prs="$( { printf '%s\n' "$composes"; cut -f2 <<<"$resources"; } | grep -E '^[0-9]+$' | sort -nu || true)"
  if [[ -z "$prs" ]]; then
    echo "No previews found — nothing to reap."
    return 0
  fi

  echo
  for pr in $prs; do
    # Asked per PR, right before acting on it, so a PR reopened during the sweep is kept. Only a
    # definite CLOSED or MERGED destroys; an error or a PR GitHub does not know keeps the preview.
    state="$(pr_state "$pr")"
    case "$state" in
      CLOSED|MERGED) ;;
      OPEN) echo "PR #${pr}: OPEN — keeping."; continue ;;
      *) echo "::warning::PR #${pr}: state ${state:-UNKNOWN} — keeping its preview; remove it by hand if the PR is gone." >&2; continue ;;
    esac
    if [[ "${DRY_RUN:-}" == 1 ]]; then
      echo "PR #${pr}: ${state} — would destroy."
      continue
    fi
    echo "PR #${pr}: ${state} — destroying."
    local rc=0
    ( PR="$pr" NAME="pr-${pr}" APP_NAME="mh-pr-${pr}"; destroy ) || rc=$?
    case "$rc" in
      # Drop the stale "preview" label if the close-event teardown missed it. Best-effort.
      0) gh pr edit "$pr" --repo "$GITHUB_REPOSITORY" --remove-label preview >/dev/null 2>&1 || true ;;
      10) ;;  # reopened during the sweep; destroy kept the preview
      # One failure must not stop the sweep; the next run retries what is left.
      *) echo "::warning::could not fully destroy the preview of #${pr}; the next run retries." >&2; failed=1 ;;
    esac
  done
  return "$failed"
}

case "$CMD" in
  provision) provision ;;
  destroy)   destroy || { rc=$?; (( rc == 10 )) && exit 0; exit "$rc"; } ;;
  reap)      reap ;;
  *) echo "Usage: PR=<n> $0 {provision|destroy} | $0 reap" >&2; exit 64 ;;
esac
