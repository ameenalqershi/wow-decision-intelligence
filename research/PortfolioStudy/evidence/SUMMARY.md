# S08 portfolio evidence (#33). Linux x64, 2 vCPU shared host, .NET 10.0.404, 2026-11-28

**Readiness: RESEARCH.** The engines run through the production S08/S07/S05 path, but the CP-SAT and HiGHS workers are research harnesses. The S02/S03 adapters are not qualified (R07 is open). The host is a 2-core machine shared with other workloads, so timings are indicative, not reference-hardware numbers.

Two independent full runs:

| File | Worker | Configurations |
|---|---|---|
| `portfolio-study-run1.json` | initial worker | cpsat, highs, portfolio |
| `portfolio-study-run2.json` | start-up-aware soft limit | cpsat, highs, portfolio, portfolio-stop |

## 1. Simulated invariants (tests/Solver.Portfolio)

`randomized-10000-summary.json` covers 10,000 seeded cases with 24,985 simulated members and 15 behaviours, checked against the real S05 verifier and an independent brute-force oracle.

**Result: 0 invariant violations.** The checked invariants:
- thread cap
- no over-commit
- an accepted decision is verified and attainable
- nothing verified is better than the selection
- proofs are true
- no orphaned executions
- false attestations are flagged
- falsified bounds are excluded

What the suite exercised:
- 4,264 corrupt or forged candidates were rejected.
- False claims were flagged across members: 138 optimality claims and 130 infeasibility claims.
- 194 members ignored cancellation; all were abandoned and their resources quarantined.
- 3,417 results were proven optimal, and all match the oracle.

Run time: 98 s.

## 2. Budget overspend: deadline to kill (cgroup v2 cpu.max, 160 burner runs over two studies)

| | run 1 (80) | run 2 (80) |
|---|---|---|
| overspend core-ms max / p95 / p50 | 16 / 8 / 0 | 14 / 6 / 0 |
| wall overrun ms max / p95 / p50 | 114 / 31 / 25 | 33 / 29 / 25 |
| measured CPU / reserved, max | 1.08 | 1.03 |
| cleanup confirmed | 80/80 | 80/80 |

**Bound:** overspend ≤ T × (kill latency + one 100 ms quota period). The worst observation was 16 core-ms at T=1, well inside the bound of 1 × (114 + 100) ms. The ledger charges measured CPU, so any overspend is reported and never netted.

## 3. Front-end scale probe

The model is a knapsack with m=5, so it has 6n nonzero coefficients.

| Form | Accepted | First failure |
|---|---|---|
| Natural chained sum `a + b + …` | n ≤ 50 (300 nnz) | n=100: `DML-LIMIT-007`, expression depth 64 |
| Balanced sum | n ≤ 200 (1,200 nnz, 740 KB UDIR) | n=400: `envelope.size` (1 MiB); n ≥ 1600: `DML-LIMIT-002` (65,536 tokens) |

The serialized UDIR costs about 615 bytes per nonzero. **The current front end is about four orders of magnitude below the 10⁷-nonzero target.**

## 4. Equal-budget comparison (run 2: 35 instances × 4 configurations)

All configurations get 6000 core-ms, a 2-thread cap, 1 GiB of memory and a 3 s wall clock. Every accepted value was independently verified on the original model: 140/140 runs were accepted and 0 engine candidates were rejected.

| Config | Best-known reached | Attested optimum (gap 0) | Median wall ms | Median charged core-ms |
|---|---|---|---|---|
| CP-SAT, 2 threads (= current S06 single choice) | 27/35 | 18 | 1257 | 2031 |
| HiGHS, 2 threads | 25/35 | 19 | 2216 | 1953 |
| Portfolio race, 1+1 | **33/35** | 21 | 2706 | 2659 |
| Portfolio race + stop on attested optimum | **33/35** | 19 | 2760 | 2652 |

Paired objective comparison (two-sided sign test):

| Pair | Wins | Losses | Ties | p |
|---|---|---|---|---|
| portfolio-stop vs CP-SAT | 8 | 1 | 26 | 0.039 |
| portfolio-stop vs HiGHS | 10 | 0 | 25 | 0.002 |
| portfolio vs CP-SAT | 8 | 2 | 25 | 0.109 (not significant) |
| portfolio vs HiGHS | 9 | 1 | 25 | 0.021 |

Run 1 points the same way but did not reach significance: portfolio vs CP-SAT 7/2/26 (p=0.18) and portfolio vs HiGHS 6/2/27 (p=0.29).

Where the gain comes from:
- All of it is on the harder families: knapsack n ≥ 60 (14/15 vs 11 and 8) and assignment 8×30 (9/10 vs 6 and 7).
- On covering, every configuration is optimal.

Cost on easy instances (17 instances where every configuration attests the same optimum):

| Config | Median wall ms | Median charged core-ms |
|---|---|---|
| CP-SAT | 456 | 408 |
| HiGHS | 540 | 536 |
| Portfolio | 894 | 808 |
| Portfolio-stop | 714 | 732 |

**A portfolio is slower and costlier when one engine alone suffices.**

## 5. Defects found by real runs (fixed or open)

1. **HiGHS stdout noise (fixed).** HiGHS prints a deprecation warning on stdout, which broke result decoding. Worker output is now framed (`WOWDI_RESULT` line).
2. **Soft limit ignored start-up (fixed).** In run 1, three runs were killed at the hard deadline and lost their incumbent, because .NET plus native start-up (0.3–0.5 s) was not subtracted from the engine time limit. Run 2 fixed this, leaving 0 lost incumbents in single runs and 1/70 in portfolio members.
3. **Residual processes (open, monitored).** `worker.residualProcesses` occurred once in roughly 260 governed runs, in the first smoke run with CP-SAT at 2 workers. It was not reproduced in 245 later runs or in a direct cgroup experiment. The boundary discarded the candidate and quarantined the slot, as designed.
4. **Soft limit not guaranteed by engines (open).** One CP-SAT member (1 worker) still ran to the hard kill in run 2. The grace should become adaptive, from the measured overrun distribution.

## Conclusions

On this host, the multi-engine race:
- improves verified solution quality against either engine alone at equal budget on the harder instances, which is significant for the stop variant
- never accepted an unverified result

It costs about 1.5–2× time and CPU on easy instances, so it should be chosen by predicted difficulty, which is X09's job.

All of this is research evidence, not a production claim. Production use needs:
- R07, then S02/S03
- repeated runs on reference hardware
- benchmark libraries (MIPLIB, MiniZinc)
- a scalable model representation
