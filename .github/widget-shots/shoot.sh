#!/usr/bin/env bash
# shoot.sh <App.app> <output folder>: one simulator per scene, the widgets added through the
# Home Screen's own gallery, then a light and a dark screenshot.
set -euo pipefail
APP="$1"; OUT="$2"; HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$OUT"
RUNTIME="$(xcrun simctl list runtimes --json | jq -r '[.runtimes[] | select(.platform == "iOS" and .isAvailable)] | last | .identifier')"
TYPE="$(xcrun simctl list devicetypes --json | jq -r '[.devicetypes[] | select(.name | test("^iPhone [0-9]+ Pro$"))] | last | .identifier')"
echo "runtime $RUNTIME, device $TYPE"

scene() {
  local name="$1"; shift
  local udid; udid="$(xcrun simctl create "shots-$name" "$TYPE" "$RUNTIME")"
  xcrun simctl boot "$udid"
  xcrun simctl bootstatus "$udid" -b > /dev/null
  xcrun simctl status_bar "$udid" override --time "9:41" --batteryState charged --batteryLevel 100 --cellularBars 4 --wifiBars 3 || true
  xcrun simctl ui "$udid" appearance light
  xcrun simctl install "$udid" "$APP"
  # iOS offers an app's widgets once the app has run; its start also resets the stored track.
  xcrun simctl launch "$udid" com.musichoarder.app
  sleep 10
  xcrun simctl terminate "$udid" com.musichoarder.app || true
  local group; group="$(xcrun simctl get_app_container "$udid" com.musichoarder.app group.com.musichoarder.app)"
  echo "group container: $group"
  python3 "$HERE/stage.py" "$group"
  local step=0
  for swipes in "$@"; do
    step=$((step + 1))
    maestro --device "$udid" test -e SWIPES="$swipes" -e OUT="$OUT/$name" -e STEP="$step" "$HERE/add-widget.yaml" \
      || echo "::warning::$name: adding widget $swipes failed"
  done
  sleep 8
  xcrun simctl io "$udid" screenshot "$OUT/$name-light.png"
  xcrun simctl ui "$udid" appearance dark
  sleep 8
  xcrun simctl io "$udid" screenshot "$OUT/$name-dark.png"
  xcrun simctl shutdown "$udid"
}

scene now-playing 1 0
scene last-played 3 2
