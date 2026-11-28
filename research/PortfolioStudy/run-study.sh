#!/usr/bin/env bash
# Run the S08 portfolio study inside a dedicated transient systemd unit with a delegated cgroup v2 subtree,
# mirroring tools/run-governance-cgroup-tests.sh. Example (from the repository root, as a user allowed to run systemd-run):
#   sudo systemd-run --wait --pipe --collect --nice=10 --property=Delegate=yes --property=MemoryMax=3G \
#     --working-directory="$PWD" --setenv=WOWDI_STUDY_DOTNET="$(readlink -f "$(command -v dotnet)")" \
#     /bin/bash "$PWD/research/PortfolioStudy/run-study.sh" research/PortfolioStudy/evidence [quick]
set -euo pipefail
relative=$(awk -F: '$1=="0" && $2=="" {print $3}' /proc/self/cgroup)
case "$relative" in /system.slice/run-*.service) ;; *) echo "Expected dedicated transient systemd service" >&2; exit 1;; esac
root="/sys/fs/cgroup$relative"
for controller in cpu memory pids; do grep -qw "$controller" "$root/cgroup.controllers"; done
mkdir "$root/host"
printf '%s\n' "$$" > "$root/host/cgroup.procs"
printf '+cpu +memory +pids\n' > "$root/cgroup.subtree_control"
export WOWDI_STUDY_CGROUP_ROOT="$root"
export WOWDI_STUDY_BOOTSTRAP="$PWD/src/Runtime.WorkerBootstrap/bin/Release/net10.0/WowDi.Runtime.WorkerBootstrap.dll"
"$WOWDI_STUDY_DOTNET" "$PWD/research/PortfolioStudy/bin/Release/net10.0/PortfolioStudy.dll" study "$@"
