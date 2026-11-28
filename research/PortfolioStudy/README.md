# S08 portfolio study (research evidence for #33)

**Readiness label: RESEARCH.**

The CP-SAT (OR-Tools 9.15.6755) and HiGHS (1.16.0) workers here are research harnesses. Each one maps a neutral integer program straight onto the engine API, the same way the existing R07 qualification studies do. They are **not** the S02/S03 production adapters: those stay gated by R07 (#7).

Everything around the workers is the production path:
- `PortfolioRunner` (S08)
- `GovernedPool`/`GovernedMember`, running on the S07 governor with the Linux cgroup v2 process boundary
- `EvidenceClassifier` and `OriginalVerifier` (S05/S04)
- the DML front end that compiles each instance to UDIR

## What is measured

1. **Budget overspend between the deadline and the kill.**
   - A CPU burner runs with T∈{1,2} threads and deadlines of {200,500,1000,2000} ms, 10 repetitions each.
   - Per run we record the cgroup-measured CPU (`cpu.stat usage_usec`), the reserved core-ms (T×deadline), the overspend (CPU − reserved) and the wall overrun (elapsed − deadline).
   - Theoretical bound: with `cpu.max = T×100000/100000`, CPU ≤ T×(wall), so overspend ≤ T×(kill latency + one 100 ms quota period).
2. **Front-end scale probe.** A multidimensional knapsack is compiled with n from 50 to 3200, in the natural chained form `a + b + c…` and in a balanced form. The probe records the parser and compiler diagnostic, the UDIR envelope bytes and the compile time.
3. **Equal-budget comparison.** Three configurations are compared:
   - CP-SAT alone: 2 threads
   - HiGHS alone: 2 threads, `mip_rel_gap=0`
   - Race portfolio: CP-SAT 1 thread + HiGHS 1 thread

   They all get the same budget: 6000 core-ms, a 2-thread cap, a 1 GiB memory cap and a 3 s wall clock. Each engine's soft limit is 2550 ms. The instances are 35 seeded pure-integer programs that both engines support equally:
   - multidimensional 0-1 knapsack, n=30/60/100, m=5
   - generalized assignment, 5×20 and 8×30
   - general-integer covering, 30×20 and 60×40, with x∈[0,5]

   Every candidate is independently verified on the original UDIR model. The objective value is recomputed exactly from the integer assignment. Engine bounds are rounded only toward validity and stay solver attestations.

## Run

From the repository root, on Linux with cgroup v2 and systemd:

~~~sh
dotnet build src/Runtime.WorkerBootstrap -c Release
dotnet build research/PortfolioStudy -c Release
sudo systemd-run --wait --pipe --collect --nice=10 --property=Delegate=yes --property=MemoryMax=3G \
  --working-directory="$PWD" --setenv=WOWDI_STUDY_DOTNET="$(readlink -f "$(command -v dotnet)")" --setenv=HOME="$HOME" \
  /bin/bash "$PWD/research/PortfolioStudy/run-study.sh" research/PortfolioStudy/evidence   # add 'quick' for a smoke run
~~~

The output is `evidence/portfolio-study.json`, holding the raw per-run rows and the environment. The summary is in `evidence/SUMMARY.md`.
