# UADIE research hypothesis dossiers (SP05)

**Decision under review.** Which adaptive mechanisms warrant a larger controlled trial? The five records below use the hypothesis IDs from the [innovation register](innovation.md). They qualify research decisions for EP04 and release reviewers. They introduce no fields, API, solver capabilities or production status. The [engine semantics](../design/engine.md) and [operating-mode boundaries](../design/operating-modes.md) govern verification and fallback.

## Decision rules fixed before replay

TC-SP05-A requires each case below to survive its own counterexample. For TC-SP05-B, a missing independent comparator, missing paired observations or an unavailable original-model checker yields **insufficient evidence** for superiority. A warm/cold comparison of two configurations of one prototype can isolate a mechanism but cannot by itself establish superiority over another method.

For future timed tests, freeze instance IDs, generator/solver versions, information cutoff, seed, family split, objective targets and host before looking at test data. Use the [paired comparison protocol](comparison-protocol.md) for arms A–E and raw measurements. The narrower mechanistic experiments below concern arms C and D. A claim about a planner or trained predictor requires the corresponding independent arm. Keep revisions of each instance together, separate training and tuning from the test families, and publish failed, unsupported and timed-out runs.

For H1–H4, a proposed first timed trial uses one dedicated core and 10 seconds end-to-end per revision per arm, including key generation, planning, startup and verification. Alternate arm order by seeded randomization. Predeclare the target verified objective and evidence level per instance using independent reference data. Record time to that target capped at 10 seconds (a miss consumes the cap), success rate and paired cost/gap at the deadline. Use the same target for every arm. Aggregate within family first, then resample entire paired families (10,000 draws, seed 163) and report 95% percentile intervals for mean differences. GO to wider trials only with zero false certificates, no lost target successes, and a strictly positive lower interval endpoint for baseline time minus candidate time against the designated independent comparator. Report all comparators and per-family tails. One family, a missing arm, or a nonpositive endpoint leaves benefit unestablished. H5 uses its separate semantic and diagnostic endpoints below. These are prospective research gates, not product tolerances.

## H1 · Exact decomposition across revisions

**Question and precedent.** Can a revision-local edit avoid solving unchanged blocks while retaining an exact optimum? Decomposition and component caching have established precedents. Even valid decomposition can weaken search bounds. The proposed contribution to test is the revision-specific independence witness and its cost. [Kitching and Bacchus, *Exploiting Decomposition in Constraint Optimization Problems*](https://www.cs.toronto.edu/~fbacchus/Papers/MB_CP2008.pdf)

**Before → after.** Cold-solve the whole revised model → expand changed semantics, identify separable blocks, solve misses, concatenate and check the source model. For finite nonempty domains, disjoint variable sets and complete constraint and additive cost scopes, write the feasible set as a product of local feasible sets and the objective as a constant plus the sum of local costs. Every feasible value is at least the sum of local minima plus the constant. Concatenating local minimizers attains equality. An infeasible block makes the product empty. An unproven local optimum gives no exact global certificate.

~~~text
expand derived symbols, shared parameters and policies in both revisions
traverse old and new incidences from changed inputs, including deleted edges
partition the current incidence graph over every hard row and objective term
if semantics or separation cannot be certified: request full solve
for each block: use an exact matching local proof or solve that block
compose assignments and evidence; check source feasibility and objective
~~~

With adjacency lists, full partitioning costs O(vertices + incidences) after expansion. Enumerating binary misses costs at most the sum over missed blocks of 2^k times the cost to evaluate one assignment of that block of size k. The spike repeatedly scans scopes. Dense coupling can restore the full exponential cost.

**Falsifier and trial.** In the four-variable fixture the locally preferred choices after a price edit jointly exceed a newly shared capacity. Keeping the old blocks would report 4 where the true minimum is 8. A joint cost term also changes the optimum without removing any feasible assignments. Compare cold independent full solving against partitioned cold, then enable reuse alone. Sweep disconnected, bridge-added, bridge-removed, dense and infeasible families at the registered edit rates (1%, 10%, 100%). Any mismatched objective, infeasibility assertion or rejected concatenation stops exact promotion. **GO to controlled trials** for finite binary additive models only. Seven named cases and the seeded differential suite support correctness on fixtures. Timed superiority remains unmeasured.

## H2 · Artifact-specific semantic reuse

**Question and precedent.** Does canonical semantic identity prevent stale reuse cheaply enough to justify lookup? Canonical solver representations and cross-process caching already exist. The candidate distinction is separate proof obligations for a hint, a bound and an exact block optimum. [US8150787B2, *Enhancing performance of a constraint solver across individual processes*](https://patents.google.com/patent/US8150787B2/en)

**Before → after.** Rebuild all artifacts → use a digest to find candidates, compare identity and evidence, then validate or rebuild. For exact optima require the same scoped domains, current independence witness, equal feasible set and equal objective under an explicit symbol mapping. Equality transfers the optimum by substitution. A feasible hint supplies an assignment only. For minimization, an old lower bound survives restriction of the feasible set with identical objective, but relaxing the set can invalidate it.

~~~text
select artifact kind and tenant namespace
canonicalize typed semantics, preserving multiplicity and symbol identity
probe digest bucket; compare canonical content and required version context
validate current kind-specific obligation; on any mismatch rebuild
remap and check assignments on the current original model
~~~

Sorting m fixed-width term descriptors and serializing L bytes costs O(m log m + L) for fixed comparison cost. Variable-length comparisons add their byte cost. A hash collision can force comparison with every entry in a bucket. Account for retained bytes and revalidation.

**Falsifier and trial.** Minimize binary x with x ≥ 1. Removing the row changes the minimum from 1 to 0. Reusing the old lower bound of 1 would certify an incorrect answer even though the objective and symbol IDs match. Replay reordered terms, edits to direction, unit, tenant and tolerance, identical digests for unequal content, and removal/restoration of rows. Compare independent cold reconstruction against structure-only caching and full guarded reuse. Ablate individual key fields in offline negative controls. Any stale exact result or cross-tenant hit stops reuse for that artifact class. **GO for local content-equality tests; REVISE before persistent reuse.** The single-tenant spike uses serialized full local content. Its reorder/restore hits and changed-price miss cover neither digest collisions nor multi-tenant isolation. Net cost is unmeasured.

## H3 · Choosing a solver under a fixed budget

**Question and precedent.** Can ranking after eligibility filtering improve verified time to target over a fixed compatible backend? Per-instance selection and held-out performance matrices are established. The proposed test charges both selection and independent verification to the caller's budget. [Bischl et al., *ASlib*](https://arxiv.org/abs/1506.02465)

**Before → after.** Choose a default backend → filter by model semantics, rank eligible backends with remaining resources, dispatch and verify. Eligibility requires inclusion of every required capability plus successful range, scaling and objective checks. Filtering is sound only if those checks and pinned manifests are accurate. A rank score proves nothing about the returned solution.

~~~text
reject manifests that fail any required semantic or numeric check
if none qualify: return unsupported
charge feature extraction; reserve time for startup and checking
take the highest-ranked eligible plan that still fits; enforce deadline
return independently checked incumbent, termination and bound evidence
~~~

For A manifests and R bounded-cost checks each, screening costs O(AR). Add feature extraction and ranking (linear in A for a best-score scan); solve time is separate. A budget counterexample uses 4 seconds for features, 6 for solving and 1 for verification. A 10-second budget cannot support this plan. An integer-only adapter also cannot represent x = 1/2 exactly without certified rescaling, however fast it appears.

**Trial and decision.** Use held-out scheduling, LP and mixed-integer families, stratified by eligibility; include unknown capabilities and unsupported cases. On families with overlapping eligibility compare with the single best compatible fixed backend selected on training data. Compare separately with deterministic capability-first rules and a hindsight oracle as a descriptive ceiling. Ablate learned ranking and expensive features. Charge offline training separately. Reject any unsupported dispatch or unverified status; use the common benefit gate on comparable families. **REVISE / collect data.** The pinned native fixtures do not compare selectors. Ranking benefit and generalization are unmeasured.

## H4 · Local repair after disruption

**Question and precedent.** Can repair around an outage deliver a verified target faster while recovering when the local boundary is too narrow? Removing and reinserting decisions in a neighborhood is established in LNS. The proposed test concerns adaptive expansion, evidence level and fallback cost. [Shaw, *Using Constraint Programming and Local Search Methods to Solve Vehicle Routing Problems*](https://neo.lcc.uma.es/vrp/wp-content/data/articles/CP4VRPshaw98.pdf)

**Before → after.** Full reschedule with previous plan as hint → free affected decisions, fix the complement, verify revised candidates, expand or cold-solve with remaining time. Fixing variables restricts feasible set F to a subset. A feasible repaired value is an upper bound for minimization; a local lower bound generally does not bound the full model. Only a valid global bound meeting the incumbent at the declared tolerance can close the gap.

~~~text
start with changed resources and their incident decisions
solve with unaffected decisions pinned; check on complete revised model
retain verified incumbents; label neighborhood infeasibility as local
on missed target expand pins' free neighborhood within remaining budget
drop all pins for fallback; on timeout report strongest verified evidence
~~~

For nested free sets S1 … Sr, work includes every restricted solve, every check and any fallback. Up to n strict single-variable expansions can precede full solving. Cap total wall and core-seconds across all attempts.

**Falsifier and trial.** After a disruption require x+y=1 for binary x,y and minimize 10x+y. Fixing y=0 yields verified cost 10. Freeing y yields cost 1. A second edit forcing x=0 makes the pinned problem infeasible although (0,1) remains feasible. Compare with full hinted solve on paired outage, material and workforce shocks. Include earliest-due-date where applicable and fixed-neighborhood LNS. Disable expansion alone, then disable hints. Stop on any false global proof or loss of a known feasible witness for the same revision in the retained-incumbent path. **REVISE / test adaptive repair.** Existing outage fixtures and H1 coupling cases exercise failure mechanisms. Adaptive expansion, stability costs and latency benefit are unmeasured.

## H5 · Independent verification of compiler rewrites

**Question and precedent.** Can an exact rewrite ledger preserve the source optimum and locate a failure more precisely? Solver-independent flattening already translates high-level constraints to primitives. The proposed distinction combines source-span provenance, unit checks and an evaluator independent of lowering. [MiniZinc Handbook, *FlatZinc and Flattening*](https://docs.minizinc.dev/en/stable/flattening.html)

**Before → after.** Inspect a backend row → reconstruct source decisions and evaluate original rows, units, objectives and relevant input spans. For source assignments x and lowered assignments z require sound reconstruction p(z), and for every feasible x an extension z with p(z)=x. Require equality of objective vectors in order and units. These two directions give equal feasible projections and attainable objective values. A single matching optimum is insufficient.

~~~text
for each rewrite: record applicability proof, origin spans and projection
reject unsupported scaling, overflow or units before lowering
for tiny fixtures: enumerate both feasible sets and compare projected costs
for each returned candidate: reconstruct, evaluate original model and cite origins
~~~

With finite source and lowered search spaces of sizes N and M, exhaustive comparison requires N source evaluations and M lowered evaluations plus projection and set comparison. Both sizes may be exponential. Runtime checking of one witness provides only that witness's feasibility and cost.

**Falsifier and trial.** Two soft penalties of weight 1 activated by x=1 total 2. Deduplicating them gives 1 while preserving feasibility. A second witness drops the feasible source choice x=1 from an unconstrained binary zero-cost model. The optimum stays 0 despite failure of projection coverage. Test duplicate hard and soft rows, unit mismatch, overflow and empty domains with named expected source spans. Compare against an independently authored direct source evaluator and an established lowering on the shared exact subset. Ablate origin tracking and per-rewrite checks separately. Freeze expected offending spans before running either system. Score exact offending-span set and rejection category per fixture; require zero projection/cost errors and a positive lower paired-family 95% interval for the diagnostic accuracy gain before claiming better attribution. Unsupported inputs remain in the diagnostic denominator. **REVISE / await compiler and comparator.** Existing scheduling and LP checkers cover their own fixtures. General compiler equivalence and diagnostic superiority are unmeasured.

## Replay and release review

Run from the repository root with .NET 10. The component study at fixture revision `f814cf3` fixes seed 20261127 and compares seven named cases plus 128 generated families at three revisions against a separately implemented exhaustive oracle. Its counts measure searched assignments. The five small witnesses below exercise the logical counterexamples independently of production paths.

~~~bash
dotnet run --project research/ComponentStudy -c Release > /tmp/sp05-components.json
diff -u research/evidence/component-study.json /tmp/sp05-components.json
dotnet run --file docs/research/sp05-witnesses.cs
dotnet run --project tools/Traceability
~~~

The release reviewer should inspect the [raw named cases](../../research/evidence/component-study.json), [schedule](../../research/evidence/linux-schedule.txt) and [coverage](../../research/evidence/linux-coverage.txt) records alongside the separate timed-trial requirements. The negative witnesses can refute a general rule; they cannot estimate its frequency. No new production field, method adoption, release-gate pass or novelty conclusion follows from these dossiers.

**Observed replay (Linux x86_64, .NET SDK 10.0.115).** All five negative controls passed. The 391 differential comparisons reproduced the archived JSON byte for byte. In the price edit the study visited 4 new local assignments versus 16 full assignments, with one cached block. Adding the bridge restored the count to 16. The native schedule fixture reproduced verified costs and bounds 3 and 12 before and after the outage. The coverage LP reproduced objective 1.5 and its analytic lower bound 1.5. These replays use synthetic fixtures; no held-out time-to-target, learned ranking, adaptive repair or compiler diagnostic comparison was run.
