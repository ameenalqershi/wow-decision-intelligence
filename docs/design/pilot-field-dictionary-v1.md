# Factory pilot field dictionary · edition 1

This edition indexes the 17 field IDs and eight application ports of the factory replay at revision `f814cf3`. The edition identifies the documentation baseline. Wire compatibility is negotiated separately by contract family and major/minor in the envelope. The data owner validates the payload after envelope validation. [Pilot contract and entity relationships](pilot-contracts.md) · [Compatibility and migration](contract-compatibility.md)

## Reading a pilot record

The trusted tenant and model identify the registry namespace. A revision freezes machines and operations. Each assignment refers to an operation in that revision and belongs to a run; evidence links the run to the checked revision and bundle bytes. Compare identifiers ordinally, preserving UTF-8 spelling and case through import, storage and display. Authorization must use the trusted tenant context before lookup. A digest identifies content; access still requires authorization. [Graph snapshot](decision-graph.md) · [Enterprise authorization](enterprise-identity.md)

All rows below are **tenant-confidential**, including derived metrics and identifiers when associated with a factory run. Only explicitly published synthetic fixtures may be treated as public. **R** means required when its containing record applies, with no inferred default. **N** permits JSON null. The dictionary supplies no omission-to-zero rule; request-specific omission is owned by the port. Machine integers use JSON numbers within the exact supported range. Wider exact values need a versioned string conversion. Precision limits beyond the published profile, database columns, SDK type names and JSON payload layouts await their owning contracts. Logical storage in the table describes ownership, not a DDL migration.

| Field ID / JSON key | Type, units, presence | Source → logical storage and relationship | Valid representation / rejected condition |
|---|---|---|---|
| F-REV / revisionId | R, nonblank immutable text | Registry → frozen revision within tenant and model; scopes every model read | `"r17"` / mutable alias or foreign revision |
| F-MACHINE / machineId | R, nonblank text | Resource import → machine key unique in revision; operation references it | `"M1"` / missing referenced machine |
| F-OP / operationId | R, nonblank text | Order import → operation key unique in revision; assignment references it | `"A"` / duplicate operation within revision |
| F-DURATION / durationMinutes | R, integer minutes | Operations → source interval length in frozen model | `3` / zero, negative or above horizon |
| F-RELEASE / releaseMinute | R, integer minutes from horizon origin | Operation availability → source lower limit on start | `0` / outside closed interval [0,horizon] |
| F-DUE / dueMinute | R, nonnegative integer minutes from the same origin | Order commitment → source due time for tardiness | `4` / missing or negative; no upper horizon bound specified |
| F-WEIGHT / tardinessWeight | R, nonnegative exact cost per minute | Order cost snapshot → objective coefficient in the fixture's single currency | `2` / negative or implicit currency change |
| F-START / startMinute | R per assigned operation, integer minutes | Candidate → start for run, revision and operation; occupies [start,start+duration) on machine | `3` / before release, end beyond horizon or resource overlap |
| F-RUN / runId | R, opaque text | Job registry → run key unique in tenant; referenced by evidence and cancellation | `"run-7"` / foreign tenant lookup |
| F-STATUS / mathematicalStatus | R, case-sensitive enum | Worker and proof policy → run's mathematical outcome | `"Feasible"` / unsupported Optimal assertion |
| F-TERM / termination | R, case-sensitive enum | Worker → reason the run ended, independent of mathematical status | `"Timeout"` / mathematical outcome in termination slot |
| F-OBJ / objectiveValue | N, finite exact value in objective units | Original model verifier → checked objective of candidate | `12` or `null` before evaluation / nonnumeric value |
| F-BOUND / bestBound | N, finite value in same objective units | Adapter → scoped bound for direction, revision and evidence | `null` or evidenced bound / incompatible direction or scope |
| F-EVID / evidenceDigest | R on evidence export, lowercase SHA-256 hex text | Exact immutable bundle bytes → digest linked to revision and run | hash of exported bytes / malformed digest or changed bytes |
| F-ELAPSED / elapsedMs | N, nonnegative integer milliseconds | Monotonic runtime clock → elapsed duration observed for run | `56` / negative; unavailable is null |
| F-CPU / cpuMs | N, nonnegative integer milliseconds | Worker accounting → aggregate CPU of process tree for run | `31` / wrong unit or unknown counter reported as zero |
| F-MEM / peakBytes | N, nonnegative integer bytes | Worker accounting → peak process-tree measurement with platform and scope | `8192` / negative or undocumented measurement scope |

Mathematical status admits Optimal, Feasible, Infeasible, Unbounded and Unknown. Termination admits Completed, Timeout, Cancelled and Failed. A Timeout may have a verified incumbent. An Optimal assertion requires compatible evidence; neither a successful process exit nor a finite objective supplies that evidence by itself. For a minimizing schedule the original-model check uses the sum of weight × max(0, start + duration − due) over operations. Ordering, units, revision and scope must match before comparing this objective with a bound. [Verification and evidence policy](engine.md)

## Layer trace

In the table, the API column names the principal **logical ports**, not HTTP routes. The SDK column describes the value the typed consumer must preserve; no public DTO is specified here. CLI structured output uses the exact JSON key/type in the first table when that command exposes the value. Command families and Studio uses below are design consumers. Current CLI host registration, UI mockups and fixtures alone do not establish end-to-end implementation. [Command and output contract](cli-contract.md) · [Runtime modes and boundaries](operating-modes.md) · [Studio module specifications](studio/UI15.md)

| ID | API context | SDK meaning | CLI context | Studio or other presentation |
|---|---|---|---|---|
| F-REV | API-MODEL, API-SOLVE, API-EVIDENCE | Immutable revision reference | model inspect, solve, evidence inspect | Frozen revision context, e.g. UI04/UI17 |
| F-MACHINE | API-MODEL, API-GRAPH, API-EVIDENCE | Revision-scoped resource reference | model/graph inspect, schedule result | UI05 graph node, UI15 lane |
| F-OP | API-MODEL, API-GRAPH, API-EVIDENCE | Revision-scoped operation reference | model inspect, solve schedule row | UI04 operation, UI15 item |
| F-DURATION | API-MODEL, API-EVIDENCE | Exact interval width | model inspect, schedule row | UI07 constraint and UI15 width in minutes |
| F-RELEASE | API-MODEL | Availability lower bound | model inspect | Earliest-start constraint; module-specific binding pending |
| F-DUE | API-MODEL | Due minute, objective input | model inspect | Due marker; module-specific binding pending |
| F-WEIGHT | API-MODEL | Exact cost per minute and currency context | model inspect | UI08 objective contribution |
| F-START | API-EVIDENCE | Candidate start in frozen horizon | solve schedule row | UI15 Gantt left edge, UI16 allocation |
| F-RUN | API-SOLVE, API-RUN, API-EVIDENCE | Tenant-scoped run handle | solve, evidence inspect | UI11 monitor, UI17 evidence |
| F-STATUS | API-RUN, API-EVIDENCE | Mathematical outcome with evidence level | solve, decision verify | UI17 evidence status |
| F-TERM | API-RUN | Independent termination reason | solve result | UI11 monitor and UI17 evidence |
| F-OBJ | API-RUN, API-EVIDENCE | Checked value or unavailable | solve, evidence inspect | UI11 objective and UI18 explanation |
| F-BOUND | API-RUN, API-EVIDENCE | Bound with direction, scope and evidence | solve, evidence inspect | UI11 monitor and UI18 explanation |
| F-EVID | API-EVIDENCE | Bundle content reference | decision verify, evidence export | UI17 evidence link |
| F-ELAPSED | API-RUN | Optional wall duration in ms | solve, benchmark report | UI11 monitor, UI19 profiler |
| F-CPU | API-RUN | Optional aggregate CPU in ms | benchmark report | UI19 profiler; UI10 budget is separate from consumption |
| F-MEM | API-RUN | Optional peak in bytes | benchmark report | UI19 profiler; UI10 budget is separate from consumption |

API-WORKSPACE selects the workspace for revision lookup. API-GRAPH filters visible incidence in that revision. API-SCENARIO forks a revision and compares compatible results. API-COMMAND selects the invocation and output mode. These contexts reuse field identity and units. API-SOLVE returns the run handle; API-RUN reads run state and observations; API-EVIDENCE verifies or exports the immutable bundle. A budget input must never masquerade as an observed metric. Specialized Studio ports under review in #190 and edit payloads in #160 require owner approval before becoming wire mappings. The per-module RTM lists display consumers and context; it does not declare every displayed field to be a direct response property of a screen's primary port. [Requirements](../../planning/requirements.json) · [Pilot ER diagram and retry rules](pilot-contracts.md)

## Preservation, gaps and conformance

* Keep null, observed zero and positive measurements distinguishable through typed values and JSON. A localized display can show 8192 bytes as 8 KiB by exact division by 1024, and 56 ms as 0.056 s by exact division by 1000. Preserve the raw value and unit for re-export, including values that would round in a compact label.
* Gantt projection converts start and duration to [start,end) in the same horizon. Recover duration by exact subtraction. Touching intervals share no occupied minute. Reject overflow, an unknown horizon or a different calendar before applying a conversion. Pixel geometry or formatted dates cannot recover source precision.
* Keep objective and bound in their source objective units. The published pilot does not define a general currency-exchange rule or a wider numeric string schema. Preserve exact values, use a reviewed versioned conversion, or reject a lossy conversion. Display text, digest case folding and relabeling of seconds as milliseconds are unsuitable as machine input.
* V01 and M03 own detailed model semantics; M09/M10 own SDK/import schemas, M11 the durable registry, S04/S05 the verifier and bound evidence, S10 the evidence store, and S12 the enterprise transport. The pilot fixture does not prescribe their concrete types or tables. Specialized UI edits, calibration of progress, provider-specific counters, multiobjective wire formats and post-MVP sectors remain open with those owners. The parent specification effort #181 and release reviewers can use this edition as a field-level baseline.

Run `dotnet test tests/Contracts --filter FullyQualifiedName~PilotFieldDictionaryTests` from the repository root. TC-SP01-A compares both tables to the pilot field inventory, passes field examples through the real envelope reader, and tests each value against an independent fixture context. TC-SP01-B checks exact representation changes and null/zero preservation. The synthetic exercise uses small exact numeric values and trusted fixture facts in place of unimplemented port handlers. It does not measure the desktop renderer, persistent store, OS counters or authorization middleware.

تُربط كل قيمة بمالكها ونسخة النموذج ووحدتها. يكون الصفر قياساً فعلياً، أما القيمة الغائبة فتُمثّل بـ `null`. عند تغيير وحدة العرض يحتفظ التصدير بالقيمة الأصلية؛ ولا يتحول انتهاء المهلة إلى حكم رياضي بعدم الجدوى.
