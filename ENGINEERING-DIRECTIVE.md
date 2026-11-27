# WOW DI — Engineering directive and contributor entry point

This document consolidates the owner's founding requirements and subsequent architecture/UI/audit amendments into a repository-native reference. It is not a verbatim transcript, a claim of completed features, or a substitute for the detailed approved specifications. Read with [AGENTS.md](AGENTS.md), [CONTRIBUTING.md](CONTRIBUTING.md), the latest approved issue ledger and the design documents. If a requirement conflicts with a current spec, record the conflict and obtain resolution before implementing; do not silently weaken it.

## Product mission and boundaries

WOW Decision Intelligence is an independent, programmable, extensible decision intelligence platform, not a clone of a competitor, an ERP, or merely a UI around a solver. UADIE (Universal Adaptive Decision Intelligence Engine) is the working research-engine name. C#/.NET is the production stack. Do not require paid AI services, a language model, internet access for the mathematical core, Python production components or WOW ERP. Integrate with WOW ERP optionally. No unsupported novelty, performance, optimality, security or platform support claims.

Product surfaces: Engine, Studio, CLI, Server, SDK, Worker and Integration Hub. Local/offline (SQLite, workspaces, models, history, evidence, import/export), enterprise server (tenant identity, durable jobs, PostgreSQL initially), headless (Linux/Windows/container/CI), embedded .NET SDK (NuGet), and hybrid local/remote routing. Reuse the same application use cases; keep core independent of UI, persistence, native adapters and network transports. Start with a modular host and isolated solver workers; justify extraction with measurements.

## Research and core contracts

Research competing planning platforms and solver/modeling products separately; cite primary sources, document licensing, prior art and unknowns. Evaluate operations research methods (LP/MILP, CP/CP-SAT, nonlinear/dynamic/graph optimization, multiobjective, robust/stochastic/online optimization, simulation, metaheuristics, decomposition, sensitivity, warm starts and algorithm selection). Scope support by capability and release; research does not imply implementation.

- EDG: versioned entity/resource/activity/decision/constraint/objective/event/policy/risk hypergraph; complete dependency scopes, old/new incidence, provenance and revisions.
- UDIR: typed variables/domains/expressions/constraints/objectives, dimensions, calendars, units/currency, uncertainty and source spans. Explicit numeric and approximation semantics.
- DML: bounded declarative grammar, AST, binder, diagnostics, normalization, exactness ledger, capability-checked lowering; visual editor, typed C# SDK and versioned JSON Schema must converge on equivalent models. No arbitrary executable input.
- Runtime: capability-first solver selection, pinned OR-Tools CP-SAT and HiGHS adapters subject to OS/license qualification, explainable selection, resource quotas, worker isolation, cancellation, safe hints, budgeted portfolio, versioned extension registry. Evaluate other engines only with evidence.
- Independent verifier evaluates the original model, recalculates objectives and validates bounds/certificates where possible. Separate feasibility, optimality, termination, proof strength and unknown gap. Fail closed on invalid witness. Preserve source, tenant, version, input hashes and replay metadata.
- Research hypothesis: certified decomposition + dependency-aware incremental reoptimization + semantic fingerprinting + safe reuse + adaptive planning. Reuse only if semantic identity/evidence and independence conditions hold. Check complete constraints/objective scopes and old/new dependencies, expand or cold-solve on coupling or failed quality/verification. Local repair is not proof of global optimality. Do not treat graph SCCs as proof of mathematical separability. Research online reoptimization, numerical invariants, Pareto/lexicographic semantics and uncertainty with explicit unsupported cases.
- For each hypothesis record question, prior art, formal assumptions, algorithm/pseudocode, correctness/complexity, counterexamples, baselines, datasets, budgets, ablations, reproducible results, limitations, novelty and go/revise/stop. Compare human planning, trained AI, conventional optimization, adaptive engine, and adaptive engine with forecasts on held-out data and fair budgets. Publish negative results. Measure cost/profit where available, feasibility, gap, latency, CPU, RSS, training/inference, adaptation, robustness and reproducibility. Do not infer runtime speedup from assignment counts.
- Scenario branching, discrete-event/Monte Carlo simulation, sensitivity, alternatives, conflict analysis and explainability must be evidence-grounded. Optional prediction/LLM adapters (.NET interfaces, ML.NET/ONNX/Microsoft.Extensions.AI when qualified) may forecast or propose DML, never bypass validation/approval or invent evidence. Operate fully without them.

## Desktop and command line

Microsoft Blazor/Razor/C# is the primary UI stack (including administration/developer surfaces). Fluent Design/Fluent UI Blazor and shared components. Photino.Blazor is a third-party candidate host: prove install/startup, offline operation, rendering and accessibility on Windows and Linux before claiming support. Do not replace with React/Angular/Vue/TypeScript as primary UI or claim first-party Linux desktop support without evidence. Record blockers and request architectural resolution.

Official SVG/PNG reference archive and IMPLEMENTATION-SPEC.md are the visual baseline; issue #89 owns publishing and integrity checks. Do not substitute fabricated screenshots. Preserve originals, use live interactive components, compare real captures with pinned fonts/DPI/data and justified tolerances. Clearly label demo values. Central tokens, dark/light/high-contrast, RTL/LTR, keyboard/screen-reader navigation, high DPI, multi-monitor, docking, tabs, split panels, command palette, status bar and diagnostics.

Studio modules: shell, workspace, overview, visual model designer, graph/hypergraph explorer, DSL editor, constraint/objective editors, model inspector, solver configuration/monitor, execution visualizer, scenario and simulation, Gantt/production scheduling, resource allocation, evidence/explainability, benchmarks/profiler, integration mapping, administration/developer portal, productivity and settings. Interactive graph/chart primitives: zoom/pan, search/filter, source/impact tracing, timelines, heatmaps, Sankey, convergence, Pareto/sensitivity and before/after comparisons where applicable.

CLI: System.CommandLine + Spectre.Console (third party), .NET hosting/DI/logging/JSON. Local engine default available without server; remote optional. Command families init/workspace, model validate/compile/inspect/compare/export, solve (auto, incremental, limits, explain, dry-run where meaningful), graph, scenario/simulate, decision/evidence, benchmark/solver, doctor/version/config/logs/health. Structured JSON/CSV/plain output; stdout machine results, stderr progress/logs; no-color, quiet, verbose, noninteractive, cancellation, stable exit codes, completion, help. Real progress and results only. Test snapshots, contracts and install as global/local tool and standalone Windows/Linux executable; qualify container and desktop packages, portable/self-contained options.

## Enterprise and integrations

API-first versioned REST/OpenAPI, optional gRPC/MCP if justified, typed SDK, schema compatibility and provenance. CSV/JSON/REST/webhooks first, then optional WOW ERP and vendor adapters (SAP, Oracle, Dynamics, Odoo, ERPNext, custom ERP, SQL/Excel/events, CRM/MES/WMS/IoT). Explicit mappings, units, cursor, retries, dedupe, idempotency, freshness, reconciliation and scoped credential references. Never use customer data without authority. Read-only by default; gated external writes require verified revision, authorized human approval, separation of duties, idempotency and reconciliation. Distinguish workflow state from solver mathematical status.

Tenant isolation, OIDC/RBAC, audit integrity/retention, secure imports/exports, privacy, rate/resource limits, plugin/native worker isolation, secret management, backup/restore, disaster recovery, secure updates, SBOM/dependency scanning and AI prompt-injection defenses. No secrets or customer data in git/logs/metrics. OpenTelemetry and operations/recovery evidence.

## Delivery and acceptance

First end-to-end slice: manufacturing resources/materials/calendars/orders, baseline, EDG/UDIR, solve, independent check/evidence/explanation, CLI + Studio graph/Gantt, scenario outage, incremental versus full, measured CPU/RSS/time and both OSes. Next independently modeled workforce coverage fixture proves reuse without manufacturing dependencies. Future sector studies: inventory, supply chain, logistics, transportation, retail, healthcare, finance/cash flow, energy, construction, project management, agriculture, telecommunications, cloud and data centers; each has its own data, model, constraints, objective, risk, baseline and gate. Do not implement all verticals at once.

Separate research prototype, MVP, production, enterprise, multi-sector and scale release gates. No claim of completion on compilation alone. For each feature, require approved scope and out-of-scope, field/schema/API/UI contracts, source-linked diagrams, acceptance criteria, negative/edge tests and observed evidence. PRD/SRS/architecture, research/math/DML/API/SDK/UI/CLI/security/integration/deployment/testing specs, data dictionary and editable C4/class/sequence/activity/state/ERD/dataflow/dependency/deployment/trust-boundary/algorithm/UI/CLI diagrams with understandable Arabic/English explanations. No invented fields or silent semantic changes.

Quality: unit, integration, architecture, contract, property/differential, mathematical verification, fuzz, security, performance/load/stress, memory, desktop/visual/accessibility, CLI, end-to-end, installation and offline tests as applicable. Windows and Linux evidence must be distinguished. Record unavailable checks explicitly; no false CI or OS claims.

## Autonomous work allocation

Read [AGENTS.md](AGENTS.md) for selection and reservation. Use current GitHub issues and approved ledger, not hardcoded issue ranges or a historical chat. Workstreams:
- Research/algorithm: prior art, certified decomposition, reuse, incremental, numerical and comparative experiments.
- Model/compiler: EDG, UDIR, DML, registry and schema.
- Runtime/verification: adapters, planner, budgets, verifier and evidence.
- Studio/design: shared tokens, graph/chart components and desktop surfaces.
- CLI/local/SDK: commands, offline/embedded/headless delivery.
- Enterprise/integration: persistence, IAM, connectors, workflow, operational recovery.
- Quality/security/release: cross-platform and mathematical tests, evidence, deployment gates.
- Specification/traceability: requirements, diagrams, fields and backlog synchronization.

These are advisory ownership boundaries, not permanent assignments. Prioritize dependency-ready P0 work that unlocks the core and first vertical slice, then other ready work in the target release. Research tasks must have a falsifiable deliverable. A worker may switch streams only with stable contracts and non-overlapping claims. Do not close an epic as if it were a leaf deliverable. Continue to the next eligible task only after evidence and handoff are recorded; if blocked, record exact blockers and choose independent ready work.
