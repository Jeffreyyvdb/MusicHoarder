#!/usr/bin/env bash
# The frontend's lint, type check, unit tests and production build, side by side, for CI
# (ci.yml, release.yml). The build is here so a PR that breaks it fails before the weekly release.
#
# Side by side they take as long as the slowest of them (the tests, then svelte-check, which is
# single-threaded) instead of their sum. Lint is oxlint, which already uses every core.
# Each log is printed whole once its command ends, so outputs never interleave, and the step fails
# if any of them does.
set -uo pipefail
cd "$(dirname "$0")/.."

echo "Runner: $(nproc) cores, $(free -g | awk '/^Mem:/ { print $2 }') GB memory"

# Sync once before anything reads .svelte-kit/. `check`, `test` and `build` sync again on their own,
# and on SvelteKit 3.2 every sync rewrites every route's $types.d.ts. svelte-check reads them in
# its first seconds, before the build's syncs; a read that met a rewrite would be a loud missing-
# `./$types` error, never a pass. vitest and oxlint do not read .svelte-kit/.
bunx svelte-kit sync || exit 1

logs=$(mktemp -d)
names=(lint check test build)
declare -A pids

run() {
  local name=$1
  shift
  (
    start=$SECONDS
    "$@" >"$logs/$name.log" 2>&1
    status=$?
    echo "$name: exit $status after $((SECONDS - start))s" >"$logs/$name.summary"
    exit "$status"
  ) &
  pids[$name]=$!
}

run lint bun run lint
run check bun run check
run test bun run test
run build bun run build

failed=()
for name in "${names[@]}"; do
  if wait "${pids[$name]}"; then
    echo "::group::$(cat "$logs/$name.summary")"
    cat "$logs/$name.log"
    echo "::endgroup::"
  else
    failed+=("$name")
    echo "::error title=frontend $name::$(cat "$logs/$name.summary")"
    cat "$logs/$name.log"
  fi
done

for name in "${names[@]}"; do cat "$logs/$name.summary"; done
[ ${#failed[@]} -eq 0 ]
