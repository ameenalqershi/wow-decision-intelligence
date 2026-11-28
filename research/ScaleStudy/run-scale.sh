#!/usr/bin/env bash
# Scale ladder (proposed X12). Run inside a memory-capped transient unit, e.g.
#   systemd-run --wait --pipe --collect --nice=10 --property=MemoryMax=3G --working-directory="$PWD" \
#     --setenv=DOTNET="$(readlink -f "$(command -v dotnet)")" /bin/bash research/ScaleStudy/run-scale.sh OUT.jsonl
set -uo pipefail
out=${1:-scale-results.jsonl}; : > "$out"
B="$PWD/research/ScaleStudy/Bench/bin/Release/net10.0/ScaleBench.dll"
run(){ echo "== $*" >&2; /usr/bin/time -f '{"wallSeconds":%e,"maxRssKB":%M}' -o /tmp/scale-time.$$ "$DOTNET" "$B" "$@" | grep '^RESULT ' | sed 's/^RESULT //' > /tmp/scale-one.$$;
  if [ -s /tmp/scale-one.$$ ]; then python3 -I -c "import json,sys;a=json.load(open('/tmp/scale-one.$$'));a['process']=json.load(open('/tmp/scale-time.$$'));print(json.dumps(a))" >> "$out"; else echo "{\"args\":\"$*\",\"failed\":true}" >> "$out"; fi; }
for n in 50 100 200 400; do run compare $n; done
run scale 10000 1 30
run scale 100000 1 60
run scale 1000000 1 60
run scale 10000000 0 0
run vars 1000000 0 0
rm -f /tmp/scale-time.$$ /tmp/scale-one.$$
