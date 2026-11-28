#!/usr/bin/env bash
set -euo pipefail
split=${1:?split}
[[ "$split" == orientation-dev || "$split" == orientation-holdout ]]
cd "$(git rev-parse --show-toplevel)"
base=docs/research/uadie/experiments/orientation-v1
dest="$base/$split"
mkdir "$dest"
dll="$PWD/research/AlgorithmDiscoveryLab/bin/Release/net10.0/AlgorithmDiscoveryLab.dll"
core=$(taskset -pc $$ | sed 's/.*: //' | cut -d, -f1 | cut -d- -f1)
arms=(full warm partition reuse box pair parity parity-warm orientation orientation-adaptive)
{
  echo "commit=$(git rev-parse HEAD)"
  echo "split=$split"
  echo "utc=$(date -u +%FT%TZ)"
  uname -a
  dotnet --version
  grep -m1 'model name' /proc/cpuinfo
  echo "core=$core"
  cat /proc/self/cgroup
  unit=$(sed -n 's#.*\/\([^/]*\.scope\)$#\1#p' /proc/self/cgroup | head -1)
  if [[ -n "$unit" ]]; then systemctl --user show "$unit" -p MemoryMax -p CPUQuotaPerSecUSec; fi
  sha256sum "$dll"
} > "$dest/environment.txt"
for rep in 0 1 2; do
  for i in "${!arms[@]}"; do
    arm=${arms[$(((i+rep*3)%${#arms[@]}))]}
    echo "$(date -u +%FT%TZ) $rep $arm" >> "$dest/launches.txt"
    timeout -k 2s 15s taskset -c "$core" dotnet "$dll" "$arm" "$split" "$rep" > "$dest/$rep-$arm.jsonl" 2> "$dest/$rep-$arm.stderr" || { echo "FAILED $rep $arm" >> "$dest/launches.txt"; exit 1; }
    test "$(wc -l < "$dest/$rep-$arm.jsonl")" = 160
    jq -e -s 'all(.[];.valid==true)' "$dest/$rep-$arm.jsonl" >/dev/null
    echo "OK $rep $arm" >> "$dest/launches.txt"
  done
done
echo COMPLETE >> "$dest/launches.txt"
