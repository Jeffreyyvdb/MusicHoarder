#!/usr/bin/env bash
# The frontend's lint, type check, unit tests and production build, side by side, for CI
# (ci.yml, release.yml). The build is here so a PR that breaks it fails before the weekly release.
#
# Side by side they take about as long as the slowest of them instead of their sum. Lint is
# oxlint, which already uses every core. Each log is printed whole once its command ends, so
# outputs never interleave, and the step fails if any of them does.
#
# Only one process may write .svelte-kit/ at a time. `svelte-kit sync` rewrites its generated
# files on every run (SvelteKit 3.2 rewrites every route's $types.d.ts), and a sync or a type
# check that reads a file another sync is halfway through writing fails with "Unexpected end of
# JSON input" or a missing ./$types: a red that depends on timing. So this script syncs once, runs
# lint, check and test from their package.json commands with their own sync taken out, and starts
# the build, whose Vite plugin syncs on its own, only after lint and check (the two that read the
# generated types) have finished. The tests may still overlap the build, because vitest.config.ts
# loads no SvelteKit plugin and so vitest never reads or writes .svelte-kit/.
set -uo pipefail
cd "$(dirname "$0")/.."

echo "Runner: $(nproc) cores, $(free -g | awk '/^Mem:/ { print $2 }') GB memory"

bunx svelte-kit sync || exit 1

# A package.json script with its `svelte-kit sync` steps removed.
without_sync() {
  bun -e '
    const script = require("./package.json").scripts[process.argv[1]];
    const command = script.replaceAll("svelte-kit sync && ", "");
    if (command.includes("svelte-kit sync")) throw new Error(`cannot take the sync out of: ${script}`);
    console.log(command);
  ' "$1"
}

logs=$(mktemp -d)
names=(lint check test build)
declare -A pids status

run() {
  local name=$1
  shift
  (
    start=$SECONDS
    "$@" >"$logs/$name.log" 2>&1
    code=$?
    echo "$name: exit $code after $((SECONDS - start))s" >"$logs/$name.summary"
    exit "$code"
  ) &
  pids[$name]=$!
}

for name in lint check test; do
  command=$(without_sync "$name") || exit 1
  run "$name" env PATH="$PWD/node_modules/.bin:$PATH" bash -c "$command"
done
for name in lint check; do
  wait "${pids[$name]}"
  status[$name]=$?
done
run build bun run build
for name in test build; do
  wait "${pids[$name]}"
  status[$name]=$?
done

failed=()
for name in "${names[@]}"; do
  if ((status[$name] == 0)); then
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
