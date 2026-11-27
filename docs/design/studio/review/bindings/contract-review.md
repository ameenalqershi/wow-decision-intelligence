# SP02 contract compatibility review — accepted domain sources

Baseline cf664c1. The pilot dictionary accepted in #159 covers 17 fields,
eight baseline ports. UDIR #16, solver contract #26, graph #14/#15,
registry #24, connector #55 and identity #75 provide typed seams.
This review binds presentation to those seams, not directly to their
storage implementations. An API label is a logical application port,
not a REST route, SDK method or public JSON DTO. The application
boundary adapts the authoritative records for local and enterprise
hosts. No invented route, database column, solver semantics or DTO is
approved here.

## Field profiles

- P: [SP01 edition 1](../../pilot-field-dictionary-v1.md), required
  text and integer/minute fields; nullable measured numbers; exact
  objective/bound semantics. No inferred default or implicit unit
  conversion. All fields tenant-confidential unless synthetic.
- U: [UDIR v1](../../udir-v1.md), actual immutable records in
  [Model.cs](../../../../src/Udir/Model.cs). Canonical exact strings
  (integer or reduced rational), no conversion to double/decimal for
  authoritative editing. Optional null members absent on UDIR wire.
  Source spans are UTF-16 half-open ranges; bind by full frozen context.
  Structural validation is not proof of feasibility.
- G: [EDG](../../decision-graph.md) and [impact](../../revision-impact.md).
  GraphRevision, GraphEntity, GraphRelationship, SourceStamp, TimeWindow,
  CapacityLimit. Complete incidence, ordinal ID, typed nullable window/
  capacity, half-open absolute instants, explicit capacity unit. EDG
  decimal capacity is not a general UDIR exact-number transport.
- S: [solver contract](../../solver-contract.md), SolverCapabilities,
  SolverResult, ObjectiveObservation, ObjectiveBound, Evidence and
  Measurement. Exact objective strings; measured nullable Int64 values.
  Adapter status/evidence distinct from independent verifier result.
  Per-objective bound direction and stage scope; never merge objectives
  into a scalar without an approved policy.
- R: [registry](../../model-registry.md): immutable tenant/model/revision
  key, UDIR and EDG digests, exact input/source bytes. Trusted tenant
  supplied by application, no UI direct SQL. Atomic publish, no partial
  revision on failure.
- C: [connector manifest](../../connector-contract.md): typed versioned
  mapping, provenance, bounded cursor and retry policies, source row keys,
  UTC source timestamp, required and allowNull independently; opaque
  credential reference only, not credential value. Mapping declaration
  validation is not validated source import or authorization.
- E: [identity](../../enterprise-identity.md): server TenantAccess,
  TenantResource, TenantActor, TenantPermission (View/Write/Propose/
  Approve/Execute). Token claims alone do not grant authorization.
  Tenant binding precedes lookup. Public REST status/route mapping and
  role mutation remain server-owned.
- L: local presentation only: view selection, focused control, filter,
  navigation, density, locale, split position and tab arrangement.
  Never changes model revision or permits an action. Persist only
  through the local presentation store; no secret or sensitive raw
  payload in navigation history.

## Per-module field projection and outstanding owner seams

The field names below refer to actual records or approved pilot IDs.
Each entry names the owner of the missing feature-level payload rather
than inventing a field or promising that an endpoint exists. Required
and optional rules are inherited from the named source above, including
no implicit defaults. A projection may label a datum unavailable; it
must not silently default a missing required field. Input controls use
the domain owner validator before publication.

| Module | Existing binding (read or validated draft) | Direction and validation | Remaining owner seam, not approved |
|---|---|---|---|
| UI01 | P:F-REV; R revision key; L shell layout contract | Switch authorized workspace then bind revision; persist only layout; keep previous context on failure | RT01 workspace handle/schema/availability; U02 display placement |
| UI02 | P:F-REV; R revision key, model/graph hashes, input/source bytes | Open or restore via application port; verify exact content before replacing anything | RT01 workspace manifest, backup format, remote connection lifecycle |
| UI03 | P:F-RUN,F-TERM,F-EVID,F-ELAPSED,F-CPU,F-MEM; S:Measurement, SolverResult identity/termination | Read-only per-source panels; null vs zero; no raw exception details | RT01/RT02 job summary, health/freshness and connector health projection |
| UI04 | U:Model identity, Types, Symbols, Expressions, Constraints, Objectives, Sources, SourceSpan; G:entity/relationship; P:F-REV,F-MACHINE,F-OP | Edit a draft, bind symbol/type/expression IDs, then validate and publish new revision; no direct graph mutation | M05/M09 visual editing and compilation commands, undo and diagnostics; no DML syntax invention |
| UI05 | G:GraphRevision, GraphEntity, GraphRelationship, SourceStamp, TimeWindow, CapacityLimit; revision impact result; P:F-REV,F-MACHINE,F-OP | Read immutable incidence, filter presentation only; compare same tenant/model; keep deleted IDs | API-GRAPH query/filter response, viewport paging and capability manifest; M02 supplies impact semantics |
| UI06 | U:SourceDocument.Id/Sha256/Utf16Length; SourceSpan.SourceId/Start/Length; Model.RevisionId | Read/edit exact source and correlate spans with edit version; no arbitrary execution | M06/M07 parser, completion, diagnostics, formatter and live compile service |
| UI07 | U:Constraint.Id/PredicateId/Hard/Penalty/Span; Expression; ScalarType; Symbol.Lower/Upper; P:F-DURATION | Hard no penalty, soft positive exact penalty; Boolean predicate, bounds in declared domain; structural checks before dimensional validation | M04 dimensional algebra and unit conversion; conflict proof from owning verifier, not UI heuristic |
| UI08 | U:Objective.Id/ExpressionId/Direction/Priority/Weight/Tolerance/Span; P:F-WEIGHT,F-OBJ; S:ObjectiveObservation | Unique positive priority, positive weight, nonnegative tolerance; preserve vector in declared units | NUM02 contribution/normalization and supported multiobjective policy; no Pareto claim under udir/1 |
| UI09 | U:Model, SourceDocument, SourceSpan, Expression DAG; G:GraphRevision | Read-only exact revision and span, preserve identity between views; unavailable transform remains unavailable | M08/M12 normalized/solver representation, transformation and reconstruction ledger |
| UI10 | U:Model.Requires/Types/TimeBasis; S:SolverIdentity, SolverCapabilities.Features/Domains/MaxCoefficientMagnitude/MaxHorizonTicks/MaxThreads/SupportsHints/SupportsInterruption/SupportsBounds | Read manifest before selection; exact required tokens; limits optional; reject unsupported settings before submit | S06/S07 planner selection, requested time/CPU/memory budget and options DTO; measurements are not budget fields |
| UI11 | P:F-RUN,F-STATUS,F-TERM,F-OBJ,F-BOUND,F-ELAPSED,F-CPU,F-MEM; S:SolverResult, Measurement, ObjectiveObservation/Bound, Evidence | Read snapshot/events; scope by run and revision; cancellation acknowledged by runtime; adapter evidence separate from verified | S07/S10 event ordering/resumption, cancellation, progress source and independent verification projection |
| UI12 | G:revision impact; S:SolverResult identity/Solver; P:F-REV,F-RUN,F-EVID | Read recorded backend/reuse evidence; no graph reachability used as a certificate | X05 decomposition/invalidation/reuse/fallback trace and comparability contract; native search capability |
| UI13 | U:Scenario.Id/ParentId/Stage/InformationSet/Probability/Overrides/Span, ScenarioValue.SymbolId/Value; S:ObjectiveObservation; P:F-REV,F-MACHINE,F-OBJ | Compare compatible frozen revisions; override only parameter/resource exact value; probability absent distinct from zero | A01 scenario fork, delta, comparative risk and common budget/metric context |
| UI14 | U:Scenario tree and TimeBasis; P:F-REV,F-OBJ | Read frozen context, validate config through owner; never infer distribution from probabilities | A04 simulation configuration/seed/RNG/actual samples/quantile/termination; proposed API-SIMULATION in #190 |
| UI15 | P:F-REV,F-MACHINE,F-OP,F-START,F-DURATION,F-RELEASE,F-DUE,F-WEIGHT; G:TimeWindow/SourceStamp | Half-open intervals, same horizon origin, preserve exact input; independently checked outcome before feasible label | V03/V04 calendars, order/assignment records and scenario scheduling; S04/S10 verified row projection |
| UI16 | P:F-MACHINE,F-START,F-DURATION; G:CapacityLimit, TimeWindow, GraphRevision | Same horizon/unit; zero capacity undefined utilization, not idle; read contributing assignment identities | V03 allocation/capacity/horizon and occupied intervals; proposed API-ALLOCATION in #190 |
| UI17 | P:F-RUN,F-REV,F-STATUS,F-TERM,F-EVID,F-OBJ,F-BOUND; S:SolverResult/StatusEvidence/ObjectiveObservation; U:SourceSpan | Validate exact bundle bytes and full scope; separate adapter evidence, independent checks, and absent certificate | S10 bundle/audit/replay; S04/S05 original row checks and verified certificate/claim mapping |
| UI18 | U:Constraint/Objective/SourceSpan; S:ObjectiveObservation/Bound/Evidence; P:F-REV,F-OBJ,F-BOUND,F-EVID | Read matched baseline/alternative and exact evidence; no marginal value for unsupported discrete models | A02/A03 counterfactual/reason/sensitivity/infeasibility result; proposed API-EXPLAIN in #190 |
| UI19 | S:SolverIdentity, Measurement; P:F-RUN,F-CPU,F-MEM,F-ELAPSED | Compare measured values only in same scope/unit; keep failed or unmatched trial visible | X07 suite/instance/seed/budget/hardware/trial/failure/statistics manifest; proposed API-BENCHMARK in #190 |
| UI20 | C:ConnectorManifest, FieldMapping, ProvenancePolicy, CursorPolicy, RetryPolicy, ConnectorStreamIdentity, ConnectorReadBatch/SourceRow; P:F-REV,F-MACHINE,F-DURATION | Validate declaration, preview through importer; atomic commit only after admission; redact credential reference and cursor | I02/I04 normalized rows, staged commit, checkpoint/health; proposed API-MAPPING in #190 |
| UI21 | E:TenantActor/TenantResource/TenantPermission/TenantOidcOptions; C:manifest metadata; P:F-REV | Server authorization before read/mutation; never derive authorization from role display, no secrets in UI | E02/U06/S12 membership mutations, administrative audit, SDK/extension registry; proposed API-ADMIN in #190 |
| UI22 | L registered command and local history; P:F-REV; API-COMMAND logical vocabulary and cli-contract.md | Resolve registered command under same context/permission for shortcuts/menu/search; no arbitrary shell | CLI10/runtime command invocation schema, external capability policy and redacted history |
| UI23 | DS01 theme/density; DS03 language proposed until #127; E:diagnostic redaction policy; P:F-REV,F-RUN | Validate local draft, atomic write, reread effective config; never log credentials | RT01/CLI10 effective engine config/schema/health; proposed API-SETTINGS in #190 |

## Compatibility cases that must not be hidden by UI adaptation

- UDIR penalty and objective weight are strictly positive; SP01 factory
  tardiness weight is nonnegative, including zero. F-WEIGHT is a source
  coefficient, not UDIR Objective.Weight. A zero tardiness coefficient
  must not become a zero Objective.Weight or be replaced by one.
- F-OBJ/F-BOUND pilot numbers and S01 exact strings have different
  transport representations. Do not silently cast exact rational to
  JSON number; the application owner must version/validate conversion.
- S01 MaxThreads is a capability limit, not a requested thread count.
  Measurement.CpuMs and PeakBytes are observed, not resource budgets.
- S01 StatusEvidence supports SolverAttestation or IndependentCertificate.
  Presence is not proof that an independent verifier actually accepted
  the original model; S04/S05 and S10 own that decision.
- A source span uses UTF-16 units, not UTF-8 byte offset, grapheme index
  or visual RTL position. Keep ID and span in LTR isolated machine text.
- A graph capacity uses decimal amount; exact UDIR types have a broader
  rational profile. No lossy implicit conversion for visual editing.
- F-STATUS and F-TERM are independent. A cancelled/timeout run may retain
  a verified incumbent. A null metric is not zero. A missing row is
  not a successful empty result.
- Connector required and allowNull are independent. A valid mapping
  manifest is neither an authorized credential nor a validated import.
- The proposed seven ports in #190 remain unmerged. Their original
  generic pilot bindings must not be routed to incorrect services
  just to render a module. Until accepted, display unavailable capability.

## Ownership disposition

The UI-owned projection, labels, exact value formatting, selection and
recovery can be reviewed now. Feature-owned schema and REST/SDK bindings
cannot be approved by the UI workstream on behalf of their owners.
The detailed requirement matrices distinguish implemented domain seam
from future application port. No change to common contracts is introduced.
