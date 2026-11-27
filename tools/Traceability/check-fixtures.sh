#!/usr/bin/env bash
set -euo pipefail
root=$(git rev-parse --show-toplevel)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
dotnet build "$root/tools/Traceability" -c Release --nologo >/dev/null
dll="$root/tools/Traceability/bin/Release/net10.0/Traceability.dll"
git -C "$root" ls-files -z --cached --others --exclude-standard | tar -C "$root" -cf - --null -T - | tar -C "$tmp" -xf -
cd "$tmp"
dotnet "$dll" > baseline
reject() {
    local file=$1 mutation=$2 diagnostic=$3
    jq "$mutation" "$root/$file" > "$file"
    if dotnet "$dll" >out 2>&1; then
        echo "Unexpected pass: $mutation" >&2
        return 1
    fi
    grep -F "$diagnostic" out
    cp "$root/$file" "$file"
}
reject planning/backlog.json '.[0].deps=["UNKNOWN"]' 'Unknown dependency'
reject planning/backlog.json '.[0].deps=[.[0].id]' 'Dependency cycle'
reject planning/issue-map.json '.R01=.R02' 'repeated issue number'
reject planning/requirements.json '.[0].requirements=[]' 'Empty or mismapped'
reject planning/requirements.json '.[0].requirements[0].tests=["absent"]' 'Missing test mapping'
reject planning/requirements.json '.[0].diagrams=["absent.svg"]' 'Missing or invalid reference'
reject planning/requirements.json '.[0].fields=["F-ABSENT"]' 'Unknown field or API'
reject planning/backlog.json 'map(if .id=="S12" then .deps-= ["S06"] else . end)' 'S12 requires S06'
reject planning/backlog.json 'map(if .id=="CLI05" then .deps+= ["S12"] else . end)' 'must be independent'
reject planning/source-inventory.json '."Supplement §4" += ["unmapped"]' 'untraced requirement'
jq '.[0].requirements[0] += {coverage:"Missing",reason:"Negative fixture"}' "$root/planning/requirements.json" > planning/requirements.json
if dotnet "$dll" --require-full >out 2>&1; then
    echo 'Strict check accepted a known missing requirement.' >&2
    exit 1
fi
grep -F 'Outstanding coverage' out
cp "$root/planning/requirements.json" planning/requirements.json
reject planning/studio-integration-gates.json '.[0].providerIssue=99999' 'Studio gate identity mismatch'
reject planning/studio-integration-gates.json '.[0].disposition="hard"' 'Studio gate disposition mismatch'
reject planning/studio-integration-gates.json '.[0].provider=.[0].consumer | .[0].providerIssue=.[0].consumerIssue' 'Studio gate cycle'
reject planning/specialized-ports.json '.[0].approval="deployed"' 'Specialized port scope/approval mismatch'
reject planning/specialized-ports.json '.[0].owners=["UNKNOWN"]' 'Specialized port owner mismatch'
