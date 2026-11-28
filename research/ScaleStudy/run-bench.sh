#!/usr/bin/env bash
# Public instances with reference values + solve ladder. Usage (memory-capped transient unit):
#   systemd-run --wait --pipe --collect --nice=10 --property=MemoryMax=3G --working-directory="$PWD" \
#     --setenv=DOTNET=... /bin/bash research/ScaleStudy/run-bench.sh BENCH_DIR OUT.jsonl LIMIT_SECONDS
set -uo pipefail
dir=$1; out=$2; limit=${3:-30}; : > "$out"
B="$PWD/research/ScaleStudy/Bench/bin/Release/net10.0/ScaleBench.dll"
run(){ echo "== $*" >&2; /usr/bin/time -f '{"wallSeconds":%e,"maxRssKB":%M}' -o /tmp/bench-time.$$ "$DOTNET" "$B" "$@" 2>/tmp/bench-err.$$ | grep '^RESULT ' | sed 's/^RESULT //' > /tmp/bench-one.$$;
  if [ -s /tmp/bench-one.$$ ]; then python3 -I -c "import json;a=json.load(open('/tmp/bench-one.$$'));a['process']=json.load(open('/tmp/bench-time.$$'));print(json.dumps(a))" >> "$out";
  else python3 -I -c "import json,sys;print(json.dumps({'args':sys.argv[1],'failed':True,'stderr':open('/tmp/bench-err.$$').read()[-600:]}))" "$*" >> "$out"; fi; }
while IFS=$'\t' read -r file ref; do run mps "$dir/$file" "$ref" "$limit"; done < "$dir/refs.tsv"
run scale 10000 1 30
run scale 100000 1 60
run scale 1000000 1 60
rm -f /tmp/bench-time.$$ /tmp/bench-one.$$ /tmp/bench-err.$$
