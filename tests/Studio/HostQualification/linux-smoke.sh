#!/usr/bin/env bash
# Run inside Xvfb and a dedicated network namespace, with a packaged fixture.
set -euo pipefail
exe=$(realpath "$1")
out=$(realpath "$2")
mkdir -p "$out"
report="$out/host.json"
test ! -e "$report"
test ! -e "$report.observations"
export GDK_BACKEND=x11
openbox >"$out/window-manager.log" 2>&1 &
wm=$!
"$exe" --evidence "$report" >"$out/host.log" 2>&1 &
pid=$!
trap 'kill "$pid" "$wm" 2>/dev/null || true' EXIT
latest_observation() {
    local files=("$report.observations/"*.json)
    local last="${files[${#files[@]}-1]}"
    [[ -f "$last" ]] && printf '%s\n' "$last"
}
wait_json() {
    local query="$1" latest
    for ((i=0;i<120;i++)); do
        if latest=$(latest_observation) && jq -e "$query" "$latest" >/dev/null 2>&1; then cp "$latest" "$report"; return; fi
        if ! kill -0 "$pid" 2>/dev/null; then cat "$out/host.log"; return 1; fi
        sleep .25
    done
    echo "Timed out: $query" >&2
    cat "$out/host.log" >&2
    if latest=$(latest_observation); then cp "$latest" "$report"; cat "$report" >&2; fi
    return 1
}
wait_json '.Rendered and .Direction == "ltr" and .FixtureRevision == "host-qualification-v1" and .DocumentUrl == "app://localhost/"'
window=$(xdotool search --onlyvisible --name '^WOW DI Host Qualification$' | head -1)
test -n "$window"
xdotool windowactivate --sync "$window"
# A genuine native window and actual OS keyboard input, no direct JS click.
if ! jq -e '.FocusedId == "direction"' "$report" >/dev/null; then
    xdotool key --clearmodifiers Tab
    wait_json '.FocusedId == "direction"'
fi
keyboard_step() {
    local key="$1" event="$2" condition="$3" before
    before=$(jq -r '.EventCount' "$(latest_observation)")
    xdotool key --clearmodifiers "$key"
    wait_json "(.EventTrace | any(.Sequence > $before and .Name == \"$event\")) and .HasFocus and ($condition)"
}
keyboard_step Tab focus:confirm '.FocusedId == "confirm"'
keyboard_step shift+Tab focus:direction '.FocusedId == "direction"'
keyboard_step Return direction:rtl '.Direction == "rtl"'
keyboard_step Tab focus:confirm '.FocusedId == "confirm"'
keyboard_step Return confirm '.Events | index("confirm") != null'
import -window "$window" "$out/window.png"
xdotool key --clearmodifiers alt+F4
for ((i=0;i<80;i++)); do
    if ! kill -0 "$pid" 2>/dev/null; then wait "$pid"; cat "$report"; exit 0; fi
    sleep .25
done
echo "Host did not close normally" >&2
exit 1
