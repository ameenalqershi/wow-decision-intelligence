# SP02 — module design review checkpoint (not design approval)

Reviewed base: `3128c0e` (merged DS02). All 23 module studies and their
linked wireframes/high-fidelity sources were checked against the archived
reference, pilot dictionary and dependency ledger. The reference SVG remains
the source of truth for the English dark design. Other SVGs are draft
screen studies only. The source-review.json is a reproducible structural
inventory, not a test result for an implemented Studio feature.

## Common review findings

- The dark studies share a 720×460 shell but most are placeholder studies
  with a generic selector, main rectangle and inspector. For example UI01
  and UI14 use sidebar fill for title/status instead of reference chrome
  (#1B2331); active navigation is a rounded rectangle without the reference
  accent marker. They must not be substituted for the official UI05 reference
  or approved as screenshot baselines. Feature-specific geometry and states
  need reviewed mockups.
- The generic Loading/Ready/Empty/Failed diagrams are not sufficient for
  editing, cancellation, stale replies, permissions or partial results.
  Module-specific state and recovery acceptance is recorded below.
- Every module currently advertises a pilot subset and an unreviewed
  specialized payload. The port dictionary is a shared vocabulary, not an
  executable contract for all editing/analytics/security operations.
- Check implementation gates against the issue ledger. No UI development
  may bypass missing dependencies by treating diagrams as finished APIs.
  Source edits overlapping PR #190 are intentionally excluded from this
  branch. Final review must be against its merged version.
- All panels must support keyboard focus, named controls, text alternatives,
  RTL and mixed-direction IDs, no unavailable metric rendered as zero, and
  explicit workspace/tenant/revision identity where relevant. Empty, invalid,
  denied and unavailable are distinct states; keep the last valid revision.
  Native DPI/monitor qualification belongs to DS03/U02/QA02.

## Module-specific review and acceptance gates

The following are review findings and proposed acceptance scenarios, not
approved API routes or storage fields. Each named datum requires the owning
contract team's approval before a feature writes or relies on it.

| Module | Required specialized data / missing contract | Keyboard and functional acceptance | Failure/recovery and visual qualification |
|---|---|---|---|
| UI01 Shell | Workspace context, stable tab ID, layout schema/version, splitter ratio, display topology and saved revision; F-REV alone cannot restore a layout. | Tab into nav and work area; resize splitter by keys; reopen layout and switch tabs without changing revision. | Unknown tab or disconnected display restores visible defaults; persistence failure keeps session; preserve reference chrome and nav order, active marker, status bar. |
| UI02 Workspace manager | Local/remote workspace handle, mode, schema, recent paths, backup manifest and restore target, connection state; do not store credentials in UI history. | Keyboard create/open, select recent, restore to different directory; compare immutable revision and evidence digests. | Unreadable path, incompatible backup, denied remote connection are distinct; preview before replacement; never overwrite current workspace on failed restore. |
| UI03 Overview | Health source and freshness, exact run+revision links, active job states, metric unit/scope and connector health. | Navigate empty and populated dashboard to exact run/revision; keyboard refresh and inspect integration. | Unknown health/measurement is not healthy or zero; stale data timestamp visible; separate loading, empty, failed source. |
| UI04 Model designer | Typed resource/variable/constraint/objective IDs, draft revision, source mapping, edit/undo commands and diagnostics. | Add and connect by keyboard as well as pointer; build Batch fixture; compare normalized model with DSL. | Invalid references mark draft; saved revision remains intact; keyboard equivalent to drag-and-drop; canvas+property inspector mockup must show actual controls. |
| UI05 Graph explorer | GraphRevision, EntityKind/RelationKind, owner/targets, source stamp, old/new revision impact and filter semantics; consume M01/M02 and DS04 primitives. | Select hyperedge and inspect all IDs; search, pan/zoom and filter; compare reachability and before/after diff. | Filter never changes graph truth; deletion remains visible, invalid revision diagnostic; official SVG governs this module; no fake monitor metrics. |
| UI06 DML editor | Document/edit version, exact source spans, typed symbols, diagnostics, formatter and completion responses. | Edit, navigate diagnostic, fix and format; verify typed tree equivalence; keyboard access to suggestions. | Stale compilation discarded; invalid code remains a draft; no execution of arbitrary DML; preserve editor cursor and selection. |
| UI07 Constraint editor | Constraint identity, hard/soft kind, expression, dimension, unit, bound and penalty; proof-backed conflict evidence. | Save equivalent capacity in two units and compare normalized values and inclusivity. | Reject dimension mismatch and negative/unsupported penalty; distinguish proven conflict from heuristic warning; preserve valid draft. |
| UI08 Objective editor | Objective/term IDs, direction, priority, weight, units, vector and contribution provenance. | Change direction/weight and priority by keyboard; compare known assignment to evaluated vector. | Reject invalid weights/duplicate priorities and unsupported Pareto mode; do not collapse lexicographic vector into misleading scalar. |
| UI09 Model inspector | AST/UDIR/normalized/solver representation IDs, source spans, transform IDs and reconstruction evidence. | Select representation and follow one normalized row back to exact source, compare transformation ledger. | Unsupported lowering visible as unavailable; do not invent solver representation; preserve selected row across view changes only if mapping exists. |
| UI10 Solver configuration | Capability manifest/version, eligible profiles, requested limits vs measured CPU/memory, parallelism, options and reason codes. | Set limits by keyboard and explain auto eligibility; round-trip planner request; no run from invalid config. | Reject unsupported options before submit; display unavailable adapter, no implicit unsafe fallback. |
| UI11 Optimization monitor | Run and event sequence, solver transitions, elapsed/CPU/memory scope, incumbent/bound direction, evidence status and cancellation acknowledgment. | Replay event gaps/duplicates; select actual point and cancel; preserve verified incumbent and absent bound. | Deduplicate, refresh snapshot after gap, label indeterminate completion; null is not zero; show cancellation pending and terminal reason separately. |
| UI12 Algorithm visualizer | Capability-tagged search events, component/revision IDs, reused/invalidated IDs, fallback reason, evidence link, comparable baseline. | Compare isolated edit and new coupling; follow reuse and fallback to observed evidence. | No synthetic search tree if backend lacks events; show unavailable feature rather than invented trace; preserve evidence IDs. |
| UI13 Scenarios | Baseline/alternative revision and run IDs, typed delta, comparison compatibility, objective units and risk definition. | Choose baseline by keys; inspect changed source IDs and comparable cost delta. | Missing baseline or incompatible objective suppresses savings; failed alternative remains distinct from no change. |
| UI14 Simulation | Experiment ID/config version, RNG algorithm/seed, event trace, count, distribution parameters and truncation status. | Configure seeded replay; verify quantiles, sample count and event trace; keyboard inspect distribution table. | Reject invalid parameters, show truncated count and no fabricated samples; current mockup needs seed/configuration, progress and empty/error surfaces. |
| UI15 Scheduling | Calendar IDs, half-open downtime/availability, order and operation IDs, release/due/duration, start/end, revision and checks. | Inspect Gantt operation before/after outage by keyboard; compare machine interval and original checks. | Overlap and blocked plans never rendered as feasible; accessible schedule table with same interval data; show no feasible schedule separately from failed query. |
| UI16 Allocation | Resource capacity/unit, frozen horizon, occupied intervals, contributing operation IDs, utilization numerator/denominator and provenance. | Filter horizon and drill down to contributors; reconcile used minutes and capacity. | Zero capacity = undefined utilization; distinguish missing from idle; heatmap must have numeric/text equivalent and not rely on color. |
| UI17 Evidence | Exact run+revision, separate mathematical/termination/verification status, evidence level, digest, row checks, objective vector and certificate. | Open timed-out verified incumbent; inspect exact source rows and export/reverify digest. | Reject tampered/cross-revision bundle; absent certificate never proof; separate unknown, failed and verified. |
| UI18 Explainability | Baseline and forced alternative, constraints, matching inputs/budget, reason/evidence kind, sensitivity eligibility. | Compare counterfactual and trace each claim to evidence; keyboard inspect binding rows. | Unsupported discrete sensitivity unavailable; explain infeasibility only with evidence; never show speculative cause as proven. |
| UI19 Benchmarks | Suite/instance/seed, hardware/runtime, solver manifest, trial IDs, paired metrics and failure counts, statistics and comparable units. | Inspect paired samples, failed trials and environment manifest; export exact trial identities. | Reject mismatched scope/units; no missing trial converted to zero; show uncertainty and unmatched instances. |
| UI20 Integration mapping | Connector manifest/schema/version, source/target fields, typed transformations/units, staged validation and rejected-row provenance. | Map by keyboard, preview normalization, inspect rejected row and commit only valid staged import. | Atomic invalid import, redact secrets, explicit access denial/expired credentials; avoid executing arbitrary transformations. |
| UI21 Admin/developer | Tenant/user/membership/role identities, permission/action scope, API configuration, SDK version, extension manifest and audit history. | Test two tenant accounts, read-only role and denied administrative action; navigate versioned SDK examples. | Server rejects cross-tenant even with modified client; redact secrets; no UI-only authorization; avoid admin success on denied action. |
| UI22 Command palette | Registered command ID, permission, mode, workspace context, argument schema and history retention. | Search by keyboard, invoke same command by shortcut and palette with same context/permission. | No arbitrary shell by default; denied/unknown command is explicit, safe arguments and confirmation for privileged actions; restore focus on close. |
| UI23 Settings | Effective vs draft config, scope/version, theme/culture, logging levels, redacted diagnostic manifest and validation. | Change theme/language, restart, verify persisted effective settings and export redacted report. | Invalid config retains last valid; no secret in logs; partial diagnostics labeled unavailable; keep keyboard focus on invalid field. |

## Gate to close SP02

1. Re-run source review on merged #190 and compare all source hashes.
2. Resolve gate discrepancies and have owning teams review specialized fields
   and port mappings. Do not invent contracts in the Studio workstream.
3. Review actual feature-specific high-fidelity mockups, including loading,
   invalid input, empty/unavailable, failure/recovery, and keyboard paths.
4. Approve screenshots only after pinning fixture, font, viewport, DPI,
   renderer and theme. UI05 official reference remains immutable.
5. Record per-module disposition and reviewer/commit, then update planning
   through coordinated ownership. This checkpoint cannot close SP02.
