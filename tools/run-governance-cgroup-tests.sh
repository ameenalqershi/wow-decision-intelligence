#!/usr/bin/env bash
# Run ONLY inside the disposable CI service with a delegated cgroup subtree.
set -euo pipefail
relative=$(awk -F: '$1=="0" && $2=="" {print $3}' /proc/self/cgroup)
case "$relative" in /system.slice/run-*.service) ;; *) echo "Expected dedicated transient systemd service" >&2; exit 1;; esac
root="/sys/fs/cgroup$relative"
test -f "$root/cgroup.controllers"
for controller in cpu memory pids; do grep -qw "$controller" "$root/cgroup.controllers"; done
mkdir "$root/host"
printf '%s\n' "$$" > "$root/host/cgroup.procs"
printf '+cpu +memory +pids\n' > "$root/cgroup.subtree_control"
export WOWDI_TEST_CGROUP_ROOT="$root"
export WOWDI_TEST_BOOTSTRAP="$PWD/src/Runtime.WorkerBootstrap/bin/Release/net10.0/WowDi.Runtime.WorkerBootstrap.dll"
export WOWDI_TEST_FIXTURE="$PWD/tests/Runtime.Governance.Fixture/bin/Release/net10.0/WowDi.Runtime.Governance.Fixture.dll"
"$WOWDI_TEST_DOTNET" test tests/Runtime.Governance/WowDi.Runtime.Governance.Tests.csproj --no-build -c Release --filter 'Category=OSContainment'
