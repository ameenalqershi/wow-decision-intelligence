# Independent engineering review

Reviewed by the separate engineering-audit agent on 2026-11-28, against `e29c62e36b3942a8e4f67dbec00cc1c30b2d3d5e`. Reviewer-authored finite tests are reproducible locally. Separate agent review is a second implementation and inspection within the same AI system. External human peer review is outstanding.

## 1. Existing experiments and project boundaries

The repository's #38 binary study is small enough to inspect end to end. `research/ComponentStudy/ExactStudy.cs` closes bit-mask scopes across hard capacities and activated cost terms. Its single-tenant dictionary uses the sorted full JSON descriptor, including global bit positions. `Oracle.cs` evaluates original rows variable-by-variable. Source inspection identifies no shared candidate evaluator, cache or partitioner. Both algorithms still share a data model and runtime, leaving common-mode model interpretation errors possible. The oracle checks both reported cost and the returned assignment. The 391 comparisons consist of seven named revisions and three comparisons for each of 128 seeded generated families. The generated binary dimensions range from two through eight. The implementation rejects model sizes outside 1–12. Exact integer accumulation may overflow and abort outside the bounded fixture coefficients. The cache has no eviction, durable provenance, cross-tenant isolation or persistent proof payload. It is fit for this bounded experiment.

The issue tracker read on the review date reported #38, #47 and #163 closed, #39–46 and #142 open. #44 requires reusable benchmark infrastructure. #45 concerns reuse, portfolio and incremental ablations. #46 requires genuinely held-out families and learned versus fixed selection. #142's enterprise-trained, human, conventional, adaptive and predictive arms answer a distinct operational question. A research prototype with several exact search variants supplies partial evidence for #44/#45. Neither such a prototype nor successful unit tests close the other questions. The accepted prior hypothesis dossier is at `docs/research/hypothesis-dossiers.md`, and the five-arm protocol is at `docs/research/comparison-protocol.md`.

## 2. Replayed evidence

Working directory was the repository root. Runtime was .NET SDK 10.0.115 on Linux x86_64. Run the following commands sequentially for a replay. Stdout is retained alongside this document, files numbered 0 through 3 in command order. The second command exercises six independent SP05 negative-control assertions. The comparator checks the JSON for exact byte equality with the pinned repository archive.

```sh
dotnet run --project research/ComponentStudy -c Release > /tmp/component-replay.json
cmp research/evidence/component-study.json /tmp/component-replay.json
dotnet run --file docs/research/sp05-witnesses.cs
dotnet run --project research/ScheduleStudy -c Release -p:RestoreLockedMode=true -p:NuGetAudit=false
dotnet run --project research/CoverageStudy -c Release -p:RestoreLockedMode=true -p:NuGetAudit=false
```

| Scenario | Exact result observed | Permitted interpretation |
|---|---|---|
| Binary component price change | Optimum 2 → 4. New local assignments visited, 4 of 16 global masks | Avoided assignments on this fixture. No elapsed-time ratio follows. |
| Shared capacity or a joint cost | One component, 16 visits, optimum 8 for each case | Partitioning from old constraints alone can lose correctness. |
| Removing bridge | Two hits, zero newly visited masks, optimum 4 | Earlier whole-content records may be reused when local semantics recur. |
| Native CP-SAT outage | Verified costs and solver bounds 3 and 12 | Native solver attestation accompanied by checked costs on this one schedule. |
| Native HiGHS workforce LP | Cost and analytic lower bound both 1.5 | Exact optimum for this fixture. Production MILP adapter qualification is separate. |

## 3. Additional check of transported lower bounds

The independent Python script `engineering-review-transfer.py` enumerates every nonempty feasible subset of a four-state space, every nonempty restriction of each such subset and every pair of functions into {-1,0,1}. The generated truth table has no solver or cache dependency. `engineering-review-transfer.json` records 426,465 bound inequalities and zero failures. The unmodified-objective subset covers 5,265 restrictions and 3,939 instances with an old minimizing witness still feasible.

For original feasible set F, revised nonempty F' contained in F, old objective f, new objective g, lower bound L valid on F and any d no greater than g(x)−f(x) for every x in F', pointwise g(x) ≥ L+d proves the transported lower bound. Enumerating the sharp d is an audit device. In a large optimizer the method is attractive only when a cheap independently checked lower bound on the shift is available. A relaxed domain requires a new implication proof. For old F={1}, F'={0,1}, f(x)=x, reusing L=1 would overstate the new optimum by one. A transported bound conveys no feasible incumbent. Numerical tolerances, units, objective direction and infinite values require their own guards.

## 4. Integration review

The existing `src/Verification.Bounds/EvidenceClassifier.cs` binds evidence to expected model bytes, tenant, run, catalog and facts. It caps enumeration at 4,096 assignments and estimated work at 1,000,000. Its supported profile is a single unit-weight objective with hard constraints, no scenarios and restricted capabilities. Its status promotion performs exact enumeration and an equality check. A feasible reconstructed candidate alone cannot inherit an optimal status. A transport proof in a research file will require a new replayable proof kind, scope derivation and tampering tests before the production classifier could safely recognize it. Prefer an additive versioned extension under #30/#40/#41 with a dedicated independent checker.

The component experiment's string content equality avoids collision-only hits but serialization and dictionary storage scale with descriptor size. A service implementation should meter peak resident memory across every child, count evicted entries and record failures to acquire a budget. Bind the consistency check and result publication atomically to a revision, so late results for an obsolete revision cannot be promoted to current status. Incomplete proof, missing scope or a timeout must yield explicit partial status. Preserve the caller's deadline through every fallback.

## 5. Independent ADL comparison against CP-SAT

The original optimization arms and their built-in exhaustive checker were inspected separately. The gate uses the shared existing Oracle evaluator for witness feasibility, so this review compiled a separate CP-SAT translation and independent witness evaluator. The comparator operates on original unary costs and cardinality rows, uses one worker, a two-second per-model cutoff and rejects every status except native Optimal or Infeasible. All six ADL candidates saw the same sequence, including infeasible intermediate revisions. The 176 published source/revision records include repeated base models by design. Another 96 seeded five-revision sequences (480 records) add previously absent nonadjacent and overlapping exact-one rows, duplicate rows, relaxation, restoration, signed weight changes and infeasibility. These synthetic checks exercise mathematical correctness; CP-SAT runtime was outside the paired performance protocol.

Result in `engineering-review-adl.json`: **3,936 candidate verdicts, zero disagreements**, with 112 infeasible model records and 270 accepted transfers across both transport candidates. This qualifies these verdicts within the declared finite grammar. Every numerical optimum and mask was independently reevaluated. The trial establishes neither a wall-clock advantage over CP-SAT nor a native production proof pipeline.

```sh
dotnet build docs/research/uadie/evidence/engineering-review-adl.csproj -c Release -p:NuGetAudit=false --artifacts-path /tmp/uadie-review-build
dotnet /tmp/uadie-review-build/bin/engineering-review-adl/release/engineering-review-adl.dll
```

The new lab links the existing component fixture's exact study and oracle. Full search and hint search use cost floors from unassigned unary variables, so a leaf visit is a different work unit from a component submask. Compare CPU and verified times across arms; interpret these counts only within the named algorithm. The pair transfer relaxation greedily chooses disjoint pairs; rearranging overlapping pair rows can change how often transport succeeds but preserves validity. Restriction uses syntactic containment of old rows, which conservatively rejects some equivalent formulations. A successful transfer leaves the component cache stale until the next fallback, affecting hit counts and cost but preserving safety through full-content matching. Keep the finite model profiles explicit when presenting the method.

The three new holdout families and four familiar family types answer different generalization questions. Tiny wall times include clock access, JIT/order effects and operating-system jitter. Peak working set is cumulative over each process, so it cannot identify peak memory of an individual revision. The 10-second launch guard covers many revisions. The elapsed field named auditTicks includes metric bookkeeping and the enumerative oracle. The bounded search restart budget in production is still prospective. Existing SP05 requests independent timed comparator and additional statistical checks before timed promotion.
