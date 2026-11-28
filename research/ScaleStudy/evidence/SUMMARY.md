# X12/X13 research evidence: scalable exact representation and exact certificates. Linux x64, 2 vCPU shared host, 2026-11-28

**Readiness: RESEARCH.** UDIR is unchanged. HiGHS 1.16 runs as a research harness (S03 is gated by R07).

Every run executed in a `MemoryMax=3G`, nice-10 transient unit. Raw rows are in `scale-ladder.jsonl` and `public-benchmarks.jsonl`.

## 1. Old (UDIR) vs new (sparse) on the same model

The model is a 0-1 knapsack, n items × 5 rows, with nnz = 6n.

| nnz | UDIR B/nnz | UDIR compile ms | UDIR verify ms | sparse B/nnz | sparse build ms | sparse verify ms | Same objective |
|---|---|---|---|---|---|---|---|
| 300 | 608.9 | 232 | 26 | 12.6 | 10.5 | 9.7 | yes |
| 600 | 605.9 | 263 | 35 | 10.8 | 10.6 | 10.8 | yes |
| 1,200 | 607.8 | 374 | 60 | 9.8 | 12.3 | 10.4 | yes |
| 2,400 | **rejected** (`envelope.size`) | | | 9.3 | 11.6 | 9.8 | n/a |

## 2. Capacity ladder (sparse; capacitated multi-item lot sizing)

| Target | Columns | Rows | Nonzeros | Build ms | Encode+SHA ms | MB on disk | B/nnz | Decode+validate ms | Verify ms (1 / 2 threads) | Peak RSS MB | Mutation detected |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 10⁴ nnz | 3,300 | 2,240 | 9,845 | 30 | 51 | 0.14 | 15.2 | 9 | 20 / 6 | 139 | yes |
| 10⁵ nnz | 33,300 | 22,240 | 99,345 | 130 | 47 | 1.43 | 15.1 | 11 | 47 / 24 | 814 * | yes |
| 10⁶ vars | 999,960 | 666,680 | 2,983,214 | 1,116 | 231 | 42.8 | 15.0 | 41 | 396 / 158 | 443 | yes |
| **10⁷ nnz** | 3,333,300 | 2,222,240 | **9,944,345** | 2,232 | 628 | 142.6 | 15.0 | 167 | 935 / 584 | 1,327 | yes |

\* The 10⁵ row includes the HiGHS MIP solve, which dominates its memory.

- Every row passed: the round-trip digest is equal, the constructed plan is exactly feasible, and a single-unit corruption is located on the right row.
- All verification ran on the checked Int128 fast path, with 0 rows falling back to exact rationals.

## 3. Build vs solve vs prove (lot sizing; HiGHS 1.16, 2 threads)

| nnz | LP relaxation | Exact certified bound | MIP (limit) | MIP incumbent exactly feasible | Certified gap |
|---|---|---|---|---|---|
| 9,845 | optimal, 0.02 s | 45,898 (LP 45,897.28, integer-rounded), 103 ms | time limit at 30 s | yes (raw float point also exact) | 35.1 % |
| 99,345 | optimal, 0.38 s | 478,776, 716 ms | time limit at 60 s | **rounded yes**; raw float point no (2.4e-12) | 35.4 % |
| 994,345 | optimal, 8.4 s | 4,880,265, 3.9 s | **no incumbent**; ran 158 s against a 60 s limit | n/a | n/a |

Lot sizing has a weak LP relaxation, so the certified gap reflects the relaxation, not the incumbent. The solver's own tree bound (69,478 and 718,246) is a solver attestation and is not certified.

## 4. Public instances with reference values

The set is 9 Netlib LPs, 4 MIPLIB-3 MIPs (HiGHS `check/instances`) and 15 MIPLIB 2017 instances, with 30 s per instance.

**LP accuracy.** HiGHS matches the published optimum on 6/9 LPs (relative error ≤ 2e-8): afiro, adlittle, israel, 25fv47, bandm and etamacro. The three deviations:
- `e226`: the Netlib value excludes the objective constant (a convention difference).
- `greenbea`: the classic Netlib value is known to be inaccurate (Koch, *The final NETLIB-LP results*, Oper. Res. Lett. 32, 2004).

- `80bau3b`: deviates by 8e-6, cause not investigated.

**LP certificates (exact bound from cleaned duals and the implied-bound box).**
- Finite on 6/9 LPs and tight on 4/9: afiro, adlittle, israel and etamacro, within ≤ 3e-10 relative.
- Loose on bandm and e226, where the implied bounds are weak.
- None on 25fv47, 80bau3b and greenbea, where a free or unbounded column has a dual residual.
- On afiro and adlittle the published reference lies a few 1e-9 below the exact certified bound, only because the reference is printed with 11 significant digits.

**MILP.** 18 instances were solvable within the limit or time-limited:

| Result | Count | Instances or detail |
|---|---|---|
| Proved optimal by HiGHS | 7 | every one matches its reference |
| Incumbent exactly feasible after integer rounding | 6/18 | 4 pure-integer models, plus 2 mixed models whose continuous values came out integral |
| Mixed with continuous variables | 12 | the float point is never exactly feasible (max residual from below 1e-16 up to 1e-6). Exact acceptance needs an exact continuous repair (fix integers, then an exact LP), which is future work |
| Exact LP-relaxation bound below every known optimum | all | never a contradiction |

On the known-infeasible `p2m2p1m1p0n100`, HiGHS stopped at its time limit and still returned a point. The exact verifier rejected it (violation 80,424).

## 5. Test scale and mutation testing (Stryker.NET 5.1.0)

| Suite | Cases | Result |
|---|---|---|
| Three-way differential (UDIR OriginalVerifier, sparse-from-lowering, sparse-direct) | 50,000 models × 6 = 300,000 assignments | 100 % agreement |
| Weak duality under arbitrary multipliers | 300,000 | 0 violations (85,636 finite bounds) |
| Bound ≤ enumerated integer optimum | 30,000 models × 5 | 0 violations; 92,395 exact proofs |
| Codec single-byte corruption | 50,000 | 46,535 rejected, 3,465 decoded to a **different** digest, 0 silent equality |
| Fast vs exact vs parallel verifier | 50,000 | identical |

Mutation scores are below the ≥ 90 % target (see `mutation/scores.txt`):

| Component | First run | After targeted tests |
|---|---|---|
| Portfolio | 60.0 % | 67.0 % |
| Sparse | 65.7 % | 73.9 % |
| Certificates | 30.4 % | 60.3 % |
