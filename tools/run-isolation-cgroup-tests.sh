#!/usr/bin/env bash
# Disposable hosted runner only, with an already delegated service. No host policy changes.
set -euo pipefail
relative=$(awk -F: '$1=="0" && $2=="" {print $3}' /proc/self/cgroup)
case "$relative" in /system.slice/run-*.service) ;; *) echo "Expected dedicated transient systemd service" >&2; exit 1;; esac
root="/sys/fs/cgroup$relative"
for controller in cpu memory pids; do grep -qw "$controller" "$root/cgroup.controllers"; done
mkdir "$root/host"
printf '%s\n' "$$" > "$root/host/cgroup.procs"
printf '+cpu +memory +pids\n' > "$root/cgroup.subtree_control"
export WOWDI_TEST_CGROUP_ROOT="$root"
export WOWDI_TEST_BOOTSTRAP="$PWD/src/Runtime.WorkerBootstrap/bin/Release/net10.0/WowDi.Runtime.WorkerBootstrap.dll"
export WOWDI_TEST_HOST_MARKER=synthetic-fixture-only
"$WOWDI_TEST_DOTNET" test tests/Enterprise.Isolation/WowDi.Enterprise.Isolation.Tests.csproj --no-build -c Release --filter 'Category=OSIsolation'
