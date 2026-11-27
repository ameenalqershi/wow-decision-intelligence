# SP02 final design disposition

The Studio workstream reviewed the visual and interaction specification
against merged audit #190 (main 2e55134), accepted logical ports edition 1,
UDIR #16, graph #14/#15, solver #26, registry #24, SP01 #159, connector #55,
enterprise identity #75 and persistence #76. This is an engineering design
review under the owner's SP02 completion directive, not an independent
third-party review or an assertion that features have been implemented.

The archived UI05 SVG and PNG are immutable; their illustrative values
are not actual solver observations. For the other 22 modules the reviewed
screen studies specify information architecture, chrome, pane proportions,
action availability and module-specific context. Their state variants
define loading, empty, invalid, unavailable and failure treatments.
The reviewed Arabic variants establish RTL placement and LTR identifier
isolation. These are design inputs for implementation. Only feature
screenshots from executing the approved runtime fixture at pinned DPI,
font, renderer and viewport can establish feature visual acceptance.
Do not substitute any static design study for an executed screenshot.

## Review decisions

| Module | Visual and interaction review | Feature acceptance preserved |
|---|---|---|
| UI01 | Reference shell, ordered nav, workspace switch, tab row, splitter, status, unsaved recovery. | SH-01–10; local shell independent of provider/native capability qualification. |
| UI02 | Recent paths and explicit local/remote choice, backup preview and digest inspector. | Exact manifest and atomic restore; no remote fallback to unrelated local data. |
| UI03 | Recent runs table, independent health panels and observed metric placeholders. | Refresh source independently, follow exact revision; unknown never healthy. |
| UI04 | Typed draft graph and inspector, keyboard addition, invalid draft kept. | M09/M07 normalize same model as DML; authoritative commit only. |
| UI05 | Original graph/monitor reference preserved, independent state variants and action surface. | Complete incidence, selection and impact, no invented metrics. |
| UI06 | Source editor and diagnostic inspector, scope/edit version visible. | UTF-16 spans, reject stale results, formatter equivalence. |
| UI07 | Predicate, bound, unit, penalty and original constraint-check inspector. | Boolean expression and hard/soft semantics, dimensional validation, proof not guessed. |
| UI08 | Ordered goal table, priority entry and vector preview. | Exact weights/tolerance, no unsupported Pareto or scalar aggregation. |
| UI09 | Separate representation stages and source mapping inspector. | Actual lowering/reconstruction, explicit missing representation. |
| UI10 | Manifest and budget editor, eligibility inspector. | Capability-first admission; budgets distinct from measurement. |
| UI11 | Event table and convergence region, run status and scoped cancel. | Recorded sequence and metrics, no inferred progress, acknowledged cancel. |
| UI12 | Evidence tree, reuse and fallback inspector, baseline comparison. | Observed trace only; no inferred separation/reuse. |
| UI13 | Frozen baseline/alternative selectors and comparison inspector. | Compatible objective units, retain baseline, no invented risk. |
| UI14 | Experiment configuration, seed/replay inspector and actual samples placeholders. | API-SIMULATION accepted logical profile; provider replay and sample conformance. |
| UI15 | Machine lanes, operation/interval inspector and schedule table path. | Half-open intervals, actual assignment/calendar evidence and original checks. |
| UI16 | Resource/horizon selector, capacity/contributor table, explicit undefined utilization. | API-ALLOCATION accepted logical profile; same unit/scope and zero-denominator case. |
| UI17 | Original-row table, objective/certificate panels, verification/digest inspector. | Independent checks distinct from solver attestation and mathematical status. |
| UI18 | Baseline/forced alternative selectors and evidence inspector. | API-EXPLAIN accepted logical profile; compatible budget and qualified LP sensitivity only. |
| UI19 | Trial table, regression chart region and environment manifest. | API-BENCHMARK accepted logical profile; paired trials, failures, measured scope. |
| UI20 | Schema map, transformation and staged preview/diagnostics. | API-MAPPING accepted logical profile; authorized atomic import and redaction. |
| UI21 | Effective permission matrix, versioned SDK/extension views and policy inspector. | API-ADMIN accepted logical profile; server authorization and provider mutation gates. |
| UI22 | Registered command search, selection/context and restricted shell capability. | Shared command admission, explicit structured args, focus restoration. |
| UI23 | Presentation and effective settings editor, diagnostic inspector. | API-SETTINGS accepted logical profile; atomic apply, reread, redaction and DS03 gate. |

## Acceptance evidence

- TC-SP02-A: 23 detailed module review specifications; 120 distinct
  requirement action/oracle pairs; 164 explicit feature state transitions;
  23 primary visual sources (one immutable original), 23 structural
  wireframes, 115 scoped state variants, 23 action surfaces and 23
  Arabic RTL variants. The reference-derived dark chrome is unchanged.
  Static screenshots of all primary sources were visually inspected.
  Actual Chromium bounding-box and state checks passed on the 22 new
  primary studies and 184 additional variants. Corrected unavailable
  actions that formerly looked enabled and used the approved warning token.
- TC-SP02-B: UI05 archive hash unchanged; design-only SVG and evidence
  labels distinguish the static review from runtime output. The static
  review approval is not approval of a runtime screenshot baseline.
  Feature issues must supply executed screenshots and acceptance results.
- The 17 SP01 definitions are linked verbatim for applicable modules.
  Seven specialized port profiles are approved at logical level in #190.
  Unimplemented provider fields/DTOs and executable conformance remain
  owned by the identified provider; the screen must not infer availability.
- All 24 integration relationships are adopted from #190: 7 hard,
  1 transitive and 16 capability gates. No cycles or changes to shared
  mathematical semantics. Direct and transitive consumers are traced.
- Local contract regressions: 174 tests across seven domain suites;
  independent #190 reports 210 regression tests. No conflation of
  design verification and implemented feature acceptance.

## Review environment and artifacts

Primary review: Chromium 141.0.7390.37 on Linux, 720×460 logical SVG,
device scale factor 1, Segoe UI requested and Liberation Sans fallback
on the review host (Arabic uses DejaVu Sans fallback as needed).
Native host font, accessibility tree, actual display topology and
OS screenshot tolerances are not qualified by the static review.
The six contact sheets are actual browser-rendered design images:
[UI01–06](evidence/visual/final-0.png), [UI07–12](evidence/visual/final-6.png),
[UI13–18](evidence/visual/final-12.png), [UI19–23](evidence/visual/final-18.png),
[states](evidence/visual/variants-0.png), [actions and RTL](evidence/visual/variants-1.png).
See [module matrix](modules/matrix.json) and [provider gate review](modules/dependency-review.md).

The UI-owned specification is accepted for feature implementation subject
to the official claim graph and conditional provider acceptance. This
does not independently approve an unimplemented provider payload, claim
support for a native host, or waive any feature/QA/release requirement.
