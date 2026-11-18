#!/usr/bin/env bash
# The frontend's lint, type check and unit tests, side by side, for CI (ci.yml, release.yml).
#
# A GitHub runner has 4 cores. One after another these took ~2.5 minutes on about one of them:
# svelte-check is single-threaded, and so is ESLint unless told otherwise (its typed parsing of
# .svelte files is most of that time). Here ESLint gets worker threads and svelte-check and
# vitest run beside it. Each log is printed whole once its command ends, so outputs never
# interleave, and the step fails if any of the three does.
set -uo pipefail
cd "$(dirname "$0")/.."

echo "Runner: $(nproc) cores, $(free -g | awk '/^Mem:/ { print $2 }') GB memory"

# `check` and `test` each start with their own `svelte-kit sync`. Sync writes a file only when its
# content changes, so after this one they write nothing and cannot read a half-written file.
bunx svelte-kit sync || exit 1

logs=$(mktemp -d)
names=(lint check test)
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

run lint bun run lint --concurrency auto
run check bun run check
run test bun run test

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
