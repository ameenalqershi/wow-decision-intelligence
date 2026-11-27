# Engineering audit and resumption record

**Snapshot.** Source commit 3f470d7c25534107e3a4b2dd11fdcbe61fb3d5ab, 27 November 2026. Live retrieval included all open and closed issue titles, bodies, labels, milestones, assignments and comments. Baseline row results are in [baseline-issues.json](baseline-issues.json). The repository is public. Two native research jobs succeeded on [Windows and Linux](https://github.com/ameenalqershi/wow-decision-intelligence/actions/runs/46007232903). This run exercised the three research programs; qualification of the desktop, packaged CLI and complete vertical slice remains open.

| Measurement | Before | After planning revision |
|---|---:|---:|
| GitHub issues excluding PRs | 88 | 184 |
| New issues | — | 96 |
| Existing issue acceptance/dependency bodies expanded | — | 5 (#37, #70, #72, #84, #86) |
| Existing active dossier clarified by comment | — | 1 (#8) |
| Native sub-issue relationships added | 0 in the tracked plan | 94 |
| Direct engineering dependency edges | 161 | 387 |
| Cycles / unknown dependencies | 0 / 0 | 0 / 0 |
| Traced clauses in source inventory | No formal matrix | 634 |
| Stable pilot data fields | No formal field-ID dictionary | 17 |
| Shared application port IDs | No formal port-ID dictionary | 8 |
| Design specifications (Markdown under docs/design, including visual contract) | 5 | 32 |
| Editable Mermaid blocks / SVG designs in docs/design | 3 / 0 | 52 / 47 |

All 88 pre-audit issue bodies matched the existing title, acceptance text and listed dependencies. At capture there were nine closed issues and 79 open, including the active #8 reservation. The baseline had acceptance prose, no stable test IDs or clause-level requirement IDs, and no machine-checked cross-reference to source artifacts. Existing comments and closure were retained. A structurally valid graph allowed local workflows to depend on enterprise service delivery.

## Coverage at the original audit merge (#186)

| Planning classification | Clauses |
|---|---:|
| Partially Covered | 501 |
| Research Hypothesis | 42 |
| Fully Covered | 1 |
| Deferred with Justification | 90 |
| Missing (explicitly classified in this inventory) | 0 |
| Conflicting / Not Applicable | 0 |

A partial row can lack approved field definitions, API schemas, test fixtures, packaging or review. Some row-level criteria cover a feature group and need clause-specific refinement. The source register identifies the missing verbatim historical source. Zero entries classified Missing does not establish complete extraction. The strict coverage check is expected to reject the current plan. Product implementation percentages cannot be inferred from these counts.

## Dependency decisions

| Scope | Decision and verification |
|---|---|
| #8 / #7 | Conditional phase-zero dossier can record Revise or Stop while qualification is open. G02 cannot ship native capabilities without #7. The active reservation was preserved. |
| #37 | Require planner, qualified schedule adapter and authorization explicitly. Original-model verification and evidence are inherited via S11 and S10. |
| #70 | Local foundation depends on architecture. The remote path is RT05 and requires S12 and E01. CLI05 has an exclusion test against transitive enterprise dependencies. |
| #72 | EDG incidence and cross-revision reachability are explicit prerequisites. |
| #84 | Separate QA01–QA04 suites cover local/SDK, desktop, CLI and enterprise. Tier gates consume only the applicable suite. |
| #86 | Release policy defines six gates. G01 research, G02 factory MVP, G03 production, G04 enterprise, G05 multi-sector, G06 scale. No later sector or enterprise capability blocks the factory MVP. |

**Longest precedence chain (16 issues).** F01 → F05 → M03 → M05 → M06 → M07 → M08 → S02 → V03 → V04 → UI15 → SL01 → G02 → G03 → G04 → G06. This is an unweighted chain, not elapsed project duration. Reliable calendar estimates require measured effort and contributor capacity.

**Dependency-ready at the baseline state.** R06 (#6), R07 (#7), F03 (#11), F04 (#12), F05 (#13), U01 (#70), U02 (#71), Q04 (#86), CLI01 (#113), DS01 (#125), SP01 (#159), SP02 (#160), SP03 (#161), SP04 (#162), SP05 (#163), EP01 (#179), EP02 (#180), EP03 (#181), EP04 (#182), EP06 (#184). Assignments, native claim tags, spec approval and path ownership must still be checked at claim time. Examples of independent paths include F05 contract compatibility, R07 native qualification and the design-token study; a ready epic is coordination work.

## Release readiness and research

| Gate | Evidence available | Outstanding |
|---|---|---|
| Research | Finite binary reuse comparison and native two-OS fixture runs | Phase-zero review, adapter notices and dataset/family limits |
| MVP | Factory math spec, research outage fixture and reference designs | Packaged local runtime, verified end-to-end import → EDG → UDIR → solve → evidence → CLI/Studio, interactive Gantt and both-OS qualification |
| Production | Proposed fields, ports and versioning rules | Signed artifacts, installation, rollback, restore and support evidence |
| Enterprise | Authorization and lifecycle design | Tenant isolation, durable leases, approvals, load/failure tests |
| Multi-sector | Independent small LP allocation research fixture | Workforce use case through the shipped common engine and SDK |
| Scale | Paired benchmark protocol | Saturation, fairness, held-out workloads and operational limits |

H1–H5 remain research-scoped. The five-arm comparison has a protocol and no measured superiority result. Vendor redistribution review, permission for pilot data, host accessibility, per-module contract completeness, visual calibration and actual disaster recovery remain risks. Future domain items are feasibility studies.

## Resume

1. Use the live issue status and reservations. This planning branch belongs to #89; #8 has its own reservation. The other onboarding PR is outside this change.
2. Run the C# validator, its negative fixtures and the live drift check before adopting a new scope baseline. Record dated evidence in the implementation PR; partial features remain open.
3. Resolve SP01 field mappings, SP02 module design review and original historical-source coverage. Use F05 to establish schema/extension compatibility before production compiler work. Qualify Photino interactively on Windows and Linux under U02. Coordinate with dedicated UI/CLI contributors.
4. Freeze the factory input and source model; implement the first complete local replay under SL01, then SL02 for workforce. Archive negative results, resource measurements and independent original-model checks.

**الخلاصة.** توثق المصفوفة ما وصل إلى مرحلة التخطيط وما يحتاج حقولاً أو عقوداً أو أدلة إضافية. نجاح التجارب الرياضية على النظامين لا يثبت جاهزية التطبيق أو المنتج للإصدار.

**Executed checks.** On the Linux development host the initial live C# reconciliation read 184 issues, 634 clauses and 387 direct edges. Ten mutation fixtures rejected unknown/cyclic prerequisites, duplicate GitHub numbers, missing requirements/test mappings/references/fields, omitted engineering prerequisites, forbidden enterprise coupling and an untraced inventory row. An eleventh strict-coverage fixture rejected an intentionally missing requirement. The prior Node scope validator accepted 184 mapped issues.

[Engineering views, complete network and release roadmap](engineering-views.md) · [GitHub Project](https://github.com/users/ameenalqershi/projects/2)

The original archive baseline was merged in PR #186. The first production contract boundary, F05, was merged in #187 with 18 xUnit cases and Windows/Linux runs. The diagram files under docs/audit are report illustrations in addition to the design-document baseline counts above. Live issues and the project board hold current execution status.

**Follow-up.** All 184 issues are present on the repository-linked engineering Project. The audit retains source-snapshot counts above. F05 and M01 now have implementation, field specifications and test evidence in the matrix. The revised Studio contracts separate simulation, allocation, explanations, benchmarking, connector mappings, tenant administration and settings. The latest counts are printed by the .NET validator. Check actual issue state and reservations before starting work. The original brief and approved per-feature contracts are still needed to establish exhaustive full coverage.

## SP02 coordination follow-up (27 November 2026)

PR #190 reconciles the earlier audit changes with merged production domain contracts using a field-level three-way merge keyed by scope ID. The source inventory remains 184 issues and 634 requirements. The 24 integration/acceptance findings supplied by #203 were adjudicated: 7 mandatory hard edges, 1 already transitive, 16 conditional capability gates. The updated hard graph has 394 edges and 94 parent relationships. Both the hard graph and the union of reviewed relationships are acyclic; the existing independence rules for local CLI and factory MVP remain intact. The editable DOT and rendered SVG are regenerated from the hard graph.

The seven specialized logical port boundaries are adopted at design level in [edition 1](../design/specialized-studio-ports-v1.md), preserving the 17 SP01 fields and eight original ports. Feature providers retain responsibility for their concrete wire payloads, independent domain or operational oracles and acceptance; no deployed handler, independent sign-off or final mockup approval is inferred. Machine-readable decisions are in [specialized ports](../../planning/specialized-ports.json) and [integration gates](../../planning/studio-integration-gates.json). The actual provider work remains open where unimplemented. The audit is not a strict full-product coverage pass.

Linux checks: Traceability and Node ledger validators; 16 negative/strict fixtures (including specialized port and gate mutations); 46/46 existing contract tests; SHA-256 and archive entry byte comparisons. Hosted CI and live issue reconciliation must pass before merge. #160/#203 own final module and visual review. #129's actual isolated solve and verified evidence remain dependent on engine capability, even though workspace storage has its own implementation.
