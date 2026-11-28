# Engineering and Mathematical Audit Ledger

Baseline: `e29c62e36b3942a8e4f67dbec00cc1c30b2d3d5e`, 2026-11-28 UTC.
This is an append-only qualification record (new dated entries supersede decisions, not evidence).
Do not infer independent verification from an issue closure or CI. Requalify affected entries on semantic changes. No external execution authorization follows from this ledger.

## Decision vocabulary
- Independently Verified: independent evidence meets the full stated acceptance scope.
- Verified Within Explicit Limits: independently reproduced evidence only for the stated bounded profile.
- Correction Required: confirmed counterexample or contract violation; link corrective issue.
- Missing Evidence: independent proof or reproduction is missing (not a defect claim).
- Incomplete: implementation or qualification not complete.
- Reopened: linked issue reopened due to verified failure.

Each detailed entry must carry issue/PR, exact source revision, requirements, affected paths/contracts, assumptions and risk, independent derivation, observed evidence, failures/limits, correction and before/after tests, predecessors/consumers, and decision. Record exact commands and environment. Pending index entries below are not completed reviews.

## 2026-11-28 — P0 intake and initial independent qualification

Environment: development Linux x64, SDK 10.0.115. Base main matches the supplied baseline; no additional commits at initial fetch. The original shared checkout had two modified research lockfiles; untouched. Worktree /root/wowdi-83-master-differential is reserved by work-claims/83. Root ENGINEERING-DIRECTIVE.md was absent at this revision; AGENTS.md and CONTRIBUTING.md govern coordination, with the owner's separate directive supplying the audit scope.

### #83 / TC-Q01-A — compiler differential qualification
Requirements: REQ-Q01; high risk; predecessors #20, #21; consumer #85. Paths: tests/Dml.Lowering/MasterDifferentialTests.cs, Dml.Binding, Dml.Semantics, Dml.Lowering and UDIR contracts; no production behavior change. Existing lowering CI already watches this test directory.
Independent derivation: for each integer pair x,y in [-3,3], let a,b,r range over {-3,-1,0,2}, {-2,0,3}, {-3,0,4}, with both objective directions. Original feasibility is exactly a*x+b*y <= r and x-y >= -2. Multiplying the first inequality by any strictly positive number preserves its truth. Objective is (b*x-a*y+3)/4. A native positive scale s and offset o reconstruct the original objective as (sum(c_i*x_i)+o)/s, regardless of direction. Decimal operations are exact for the small bounded dyadic operands in this grid; no compiler arithmetic is used for the oracle.
Observed: 72 primary models, 72 positive-row-scaling metamorphic models; 49 complete assignments per primary model = 3,528 points (and 3,528 scaled-model feasibility comparisons). Each original/normalized/native row is checked separately against the direct formula, as well as joint feasibility, domain endpoints, objective reconstruction and direction, row spans and nonlinear diagnostic span. Existing + new lowering suite: 13 passed, 0 failed, 0 skipped on Linux after the new tests. Command: `dotnet test tests/Dml.Lowering/WowDi.Dml.Lowering.Tests.csproj -c Release --nologo -p:RestoreLockedMode=true`. Independent evaluator is test-only C# and uses no compiler helpers. No observed defect in this profile. Before adding the new tests, existing tests were not separately rerun; do not fabricate a before count. Limits: no universal proof, no general arbitrary-rational or nonlinear semantics, no native backend runtime, no presolve composition claim, no Windows evidence for new tests at this entry. Decision: Verified Within Explicit Limits; hosted qualification pending.

### #29 / PR #220 — S04
REQ-S04, DIR-S04-1, TC-S04-A; high risk. Paths: src/Verification.Independent, UDIR/S01 and Manufacturing.Data source contracts. Predecessors #16,#26; consumers #30,#34,#35,#42,#50,#54,#65,#117,#135,#140,#147.
Initial code/design review: original DAG is evaluated rather than lowered rows; exact rationals, hard-row rejection, soft observations and source binding; pilot source profile is separate. Re-execution of existing suite: 14 passed, 0 failed, 0 skipped. This is reproduction of existing tests, not a new independent oracle. Still required: independently generated source-model and scheduling cases, frozen fact authority, adversarial malformed and cross-model witnesses, complete arithmetic/profile coverage. No correction implemented; no counterexample confirmed in this pass. Decision: Missing Evidence.

### #30 / PR #229 — S05
REQ-S05, TC-S05-A; high risk. Path src/Verification.Bounds; depends on #29; consumers #35,#41,#65.
Mathematical invariant: for minimization L <= optimum <= U, for maximization incumbent <= optimum <= upper bound. Gap = absolute difference, relative gap = absolute gap / max(1, absolute incumbent) in declared units. A solver assertion with zero gap is not an independent proof. Enumeration proves optimum only if the complete finite Cartesian domain is exhausted and every candidate is correctly evaluated; empty feasible set after complete evaluation proves infeasibility. Current profile caps assignments at 4096 and estimated work at 1,000,000, excludes multiobjective/soft/scenario/continuous enumeration and LP certificates. Review of EvidenceClassifier and design supports these declared boundaries; re-executed existing 8 tests, all passed. New independently generated oracle, invalid evidence and certificate review still required. No correction implemented. Decision: Missing Evidence.

### #35 / PR #233 — S10
REQ-S10, TC-S10-A; high risk. Paths src/Verification.Evidence and M11 registry/S04/S05 contracts. Depends on #24,#29,#30; consumers #36,#51,#64,#74,#100,#106,#120.
Design requires authoritative ledger/attempt binding; hashing alone cannot authenticate a compromised ledger. Re-executed existing 8 tests, all passed, including the existing SQLite tests. Full storage/replay implementation inspection, independent tampering/concurrent admission/fenced publication and recovery evidence remain outstanding. No claim of PostgreSQL, cross-file atomicity or Windows qualification from this run. No correction implemented. Decision: Missing Evidence.

Reproduction commands for the above three existing suites: `dotnet test tests/Verification.Independent/WowDi.Verification.Independent.Tests.csproj -c Release --nologo -p:RestoreLockedMode=true`, analogous paths for Verification.Bounds and Verification.Evidence. All exit 0, no skipped tests. These results cannot substitute for independent acceptance.

### #7 — native qualification coordination
REQ-R07, DIR-R07-1, TC-R07-A; risk medium. Prerequisite #2 is now closed; #7 remains assigned and blocked, with prior explicit requests for ownership transfer. No remote reservation tag observed at initial fetch; absence of a tag does not override assignment. Do not claim silently. Prior hosted research run 46007232903 does not qualify redistribution, clean installation or complete ABI/notice inventory. #27 and #28 remain gated. Decision: Incomplete.

## Remaining directive index
For each row, issue/PR is the intake mapping, not a claim of successful audit. Source revision is the baseline above unless a later entry explicitly states otherwise. Requirements, risks, paths, proofs, tests, corrections and dependency analysis must be filled from the original specifications and observed code when each review begins. No unreviewed entry is certified.

| Scope | Issues / PRs | Decision |
|---|---|---|
| Shared foundations | #13, #14, #15, #16, #17, #24, #26, #89 | Missing Evidence |
| Compiler | #18/#221, #19/#223, #20/#224, #21/#227, #22/#216, #23/#214, #25/#228, #31/#230 | Missing Evidence |
| Numerical invariants | #134/#232 (active) | Incomplete |
| Industrial models | #48/#218, #49/#219, #128/#202 | Missing Evidence |
| Runtime governance | #32/#217/#225, #129/#212, #132/#235 | Missing Evidence |
| Security and decisions | #12/#192, #36/#234, #55/#197, #56/#215, #75/#196, #76/#211, #79/#222, #81/#226, #148/#200 | Missing Evidence |
| Studio / CLI | #71/#198, #90/#213, #127/#204, #113/#191, #122/#194, #123/#193 | Missing Evidence |
| Research | #163/#205, #159/#206, #160/#203, #161/#231, #38, #47 | Missing Evidence |
| Remaining native/runtime | #27, #28, #33, #34 | Incomplete |
| Exact reuse and evaluation | #39, #40, #41, #42, #43, #44, #45, #46 | Incomplete |
| Additional math / robustness | #135, #136, #137, #142, #147 | Incomplete |

Next audit priorities: independent S04/S05 generated oracles and S10 adversarial replay, foundations #16/#17, #25 projection/lift, #32/#31, R07 coordination; do not override active claims. Separate mathematical validity, qualified hardware/OS, and production readiness.

## 2026-11-28 — subsequent independent qualification and ledger correction

- #147 / PR #238 / commit 6801f5e: 64 deterministic generated source models, 1,600 assignments, independent integer arithmetic and exhaustive min/max oracle for rational objectives with denominator 3; original S04, native affine representation and S05 exact proof agreement. Forged objective values, invalid bound direction/order, false certificate, stale scope and incomplete enumeration rejected or downgraded; four minimal invalid-source fixtures. Locked Linux Release 68/68, hosted robustness jobs passed on both Linux and Windows at that commit. See tests/CompilerRobustness/README.md on PR #238. Within this explicitly bounded profile, #29/#30 now have independent oracle evidence; overall decision remains Missing Evidence pending broader source, LP certificate, multiobjective and host integration review. #147: Verified Within Explicit Limits; no universal proof.
- #25 / PR #228 / TC-M12-A: reviewed SafePresolve.Apply and reconstruction. Removing a literal-true predicate or a duplicate of an identical hard predicate preserves the conjunction. For unused variable u with nonempty admitted domain D and no occurrence in original DAG, F=F_retained × D and f is independent of u, so projection and lift with any valid fixed u preserve feasibility and objective; soft rows cannot be discarded. Temporal and unknown/scenario profiles are retained. This derivation assumes original DAG/type admission and canonical-node equivalence; it is not qualification of the adapter path. Existing locked Linux Release suite rerun: 5/5 passed, no skipped tests (`dotnet test tests/Dml.Presolve/WowDi.Dml.Presolve.Tests.csproj -c Release --nologo -p:RestoreLockedMode=true`). New independent projected-domain oracle and native composition remain outstanding. Decision: Missing Evidence.
- #83 hosted lowering conformance passed on Linux and Windows at f0ba24b; the global github-ledger check failed, as it did for #238, because live issue #236 was not mapped (verified set difference: unmapped {236}, missing {}). Corrected planning registration only, in PR #239 / merge abcb22463402fddfb4f278f0049d8020ac156a75; all five checks passed. Research ownership and implementation were not changed. New X11/REQ-X11 records preserve original #236 acceptance; 185 issues, 635 requirements, 396 dependency edges, 94 sub-issue relationships. Hosted checks must rerun on the new branch revisions.

## 2026-11-28 — X02 independent component proof

#39 / REQ-X02 / TC-X02-A, baseline 4d604f3bc317343f33eaae7ebff6bf49de34e44f; medium risk, predecessors #15/#20/#38; consumers #42/#43. Added src/Optimization.Separability, tests/Optimization.Separability, docs/design/exact-separability.md and dedicated two-platform workflow. Mathematical induction derives a total integer affine form from every original expression; union source constraint read sets, not solver rows; all admitted hard predicates then factor by component and the single affine objective adds across them. Full finite local enumeration and product-set proof establish optimum or infeasibility within the explicit profile; S04 rechecks the entire returned candidate. Nonlinear, soft, unknown, nonfinite, noninteger and future capabilities fail closed, no external implicit resource semantics claimed. Independent direct full enumeration of 132 four-variable cases (10,692 reference points), plus negative and boundary cases, passed 20/20 Linux locked Release tests. No prior production component existed; no fabricated before result. No cache or native solver support, no performance claim, Windows CI pending at this entry. See design for exact limits/commands and proof. Decision: Verified Within Explicit Limits.

## 2026-11-28 — X03 canonical identity and reuse permissions

#40 / REQ-X03 / TC-X03-A; medium risk; baseline 4d604f3 (rebased by merge on X02 commit 76422a8). Predecessors #24/#20; consumer #41. Added src/Optimization.Fingerprints, independent tests, design and two-platform workflow. Canonical domain-separated typed projections separate structure, data, constraint/objective DAG slices, solver configuration and exact source provenance. Validate actual frozen input bytes, compare full canonical bytes after matching digests, reject malformed Unicode and unregistered/unsupported context; no cross-tenant/model permission. A prior assignment is only a hint requiring full current original-model verification; exact-identity plans still require host rebinding; no bound/evidence transfer. Seven Linux locked Release tests pass; no prior production component, no fabricated before-test result. See docs/design/canonical-fingerprints.md for limits and replay. Decision: Verified Within Explicit Limits; Windows CI pending at this entry.
