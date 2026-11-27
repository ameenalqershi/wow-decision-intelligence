# Market and engineering comparison

Research snapshot • 27 November 2026. The cited documents establish public interfaces and vendor-described capabilities. An integration name establishes a target, not measured interoperability. No common workload, contract quotation, customer data, capacity limit or independent performance audit was available. “?” means unknown from the cited materials. Tail-column implications are engineering judgements. Procurement must request edition-specific deployment, license and support terms.

## Enterprise planning products

| Product / source | Operational focus and public architecture | Modeling, programmability and integration | Scenarios, uncertainty and updates | Evidence, scale and commercial model | Implication for WOW DI |
|---|---|---|---|---|---|
| [Makkook DIOS][dios] | Operational constraints and actions across an enterprise | Configured objectives and constraints, ERP/MES/WMS connections claimed | Live model and response to changes claimed; uncertainty formalism ? | Vendor-described reasons and execution; solver, guarantees, benchmarks and prices ? Commercial offering | Reproduce our own evidence trail. Vendor speed claims supply no baseline. |
| [o9 Digital Brain][o9] | Integrated planning on Enterprise Knowledge Graph | Shared model across supply, demand and finance; extension coverage ? | What-if and synchronized planning; exact incremental algorithm ? | Cross-functional context; reproducible solver evidence and comparable scale ? Enterprise platform | A graph alone cannot differentiate a product. Export revisioned constraint-level evidence. |
| [Kinaxis Maestro][kin] | Concurrent supply planning linked to operational KPIs | Planners change demand, capacity, inventory and rules | Reconciles plans and compares scenarios; uncertainty guarantees ? | KPI trade-offs; vendor timing claims depend on scope. Commercial | Benchmark the cost of producing a verified revised plan after one outage. |
| [Blue Yonder IBP][blue] | Demand, supply and financial planning | Configured planning processes; generic programming interface not established here | Scenario-based alignment across functions; algorithmic reuse ? | Business objectives visible; throughput, proof format and terms ? Commercial | Offer a bounded planning workflow with transparent assumptions. |
| [SAP IBP][sap] | Supply-chain planning with what-if versions | Key figures and planning objects in the IBP environment | Alternative plans for demand conditions; simulation differs from stochastic optimization | Planner comparison; solver bound and public scale limit ? Commercial SAP offering | Treat changes to scenario data independently from acceptance and write-back. |
| [Siemens Opcenter APS][siemens] | Finite-capacity MPS and detailed shop scheduling | Labor, machines, materials, tools and priorities; shop-floor focus | Schedule changes natural to APS; probability model and exact incremental guarantees ? | Strong manufacturing semantics; portable solver evidence ? Commercial | Closest functional pilot comparator. Compare on identical calendars and due dates. |
| [Oracle Fusion SCM][oracle] | SaaS supply planning with in-plan edits and simulation sets | Modify a material plan or input data and rerun | Simulation sets and edits test demand or impediments; underlying reuse ? | Compare planning outcomes; reproducible external proof and limits ? Cloud subscription/quote | Expose snapshot provenance and replay a historical input independently. |
| [Palantir Foundry][pal] / [AIP][aip] | Ontology, actions and operational workflows | Developer tooling, functions and data integrations | Isolated edits in scenarios; a generic optimization guarantee is not established | Governance and operational actions; scale and commercial terms contract-dependent | Preserve proposal/approval boundary and avoid requiring an AI model. |

## Modeling systems, planning engines and optimizers

| Product / source | Layer / supported models | Public architecture, programming and integration | Incremental, uncertainty and scenario semantics | Evidence, scaling and licensing | Design implication / limitation |
|---|---|---|---|---|---|
| [AIMMS][aimms] | Optimization application platform | Model development, UI, embedded components and deployment environment | Model-defined or solver-specific; exact reuse ? | Developer and deployment licenses, commercial/academic options; instance evidence depends on solver | Compare authoring effort separately from solve speed. |
| [Gurobi][guro] | Mathematical optimizer, LP/MIP and other documented classes | Model API, starts, restricted multi-scenario changes | Select objectives, bounds and RHS changes per scenario; not arbitrary scenario-tree nonanticipativity | Bounds, gaps, IIS and continuous duals subject to model/termination; commercial with specific license programs | Reference for evidence semantics. Commercial adapter optional. |
| [FICO Xpress][fico] | Mathematical optimization suite | Native solver, Mosel, .NET and other APIs, solution pool | Model and API specific; pool enumeration has different semantics from an uncertain future | Bounds where solver supports them; [community license][ficolic] plus commercial terms | Qualify edition and model limits before a comparison. |
| [IBM CPLEX][ibm] | Mathematical programming and separate CP technology in suite | Optimizer APIs, starts and conflict refiner | Reuse a MIP start, refine a conflicting start; scenario structure is model-dependent | Diagnostic conflicts and MIP bounds; commercial editions; conflict set need not have minimum cardinality | Represent diagnostic strength explicitly. |
| [Google OR-Tools][ort] | Algorithm toolkit including CP-SAT, routing, flow and linear wrappers | C++ engines and [.NET bindings][ortrepo]; CP-SAT uses integral constraint coefficients | Hints and repeated solves do not establish preserved search state. Uncertainty modeled externally | Native statuses and objective bounds as supported; Apache-2.0 core, bundled notices separate. Scale instance-dependent | Integer scheduling adapter. Reject unsafe scaling, validate independently. |
| [Timefold][timefold] | Constraint-based planning / local search | JVM domain objects and [incremental scoring][time2] | Score deltas and replanning; not a generic bound or stochastic guarantee | Score explanations, open Apache-2.0 and commercial enterprise edition; planning APIs available separately | Compare dispatch heuristics and edit latency; do not equate score with proof. |
| [Hexaly][hex] | Model-and-run optimization, including scheduling and routing classes | Expressions and C# modeling API | Per-model initial solutions/scenarios need separate qualification; uncertainty support ? | Commercial optimizer. Quality and bound capabilities vary by problem | Include where licensed; no required runtime dependency. |
| [Pyomo][py] | Algebraic modeling in Python | External solver interfaces; separate solver installation | Repeated solves and extensions depend on solver and formulation | [BSD-3-Clause][pylicense] for modeling layer; third-party backend terms independent | Prior art for separating model from backend; reference only. |
| [JuMP][jump] | Julia modeling language for mathematical optimization | MathOptInterface connects optimizer implementations, wrappers and reformulations | Data changes and warm starts depend on backend support; stochastic models can be expressed | Backend diagnostics/scale; open ecosystem, check each package and solver license | Capability contracts must account for bridges and dual reconstruction. |
| [MiniZinc][mzn] | Solver-independent constraint modeling language | Compiler flattens model and data into FlatZinc for a solver | Reflattening and result verification are distinct; uncertainty encoded by model | Inspectable flattened model; [MPL-2.0 default for compiler][mznlicense], solver-specific licenses | UDIR and flattening alone provide no novelty. Retain source locations. |
| [OptaPlanner / Apache KIE][op] | JVM planning framework; Timefold is a related fork | Domain-specific constraints and planning objects | Continuous planning, pinning and replanning; no implied global optimum | [Apache-2.0][oplicense]. Operational deployment and measured load depend on application | Evaluate pinning separately from exact separability and measure disruption. |

## What to verify in a competitive pilot

Give every candidate the same machine windows, material arrivals, horizon and hard policies. Export the assignments and independently score them. Measure the time to first feasible plan, total cost at fixed budget, audited change lead time, planner effort and cost of integration. Record missing features as unknown until demonstrated. Separate managed service price, model development, native license, data preparation, maintenance and implementation support. Commercial differentiation is a hypothesis until the same buyer chooses or pays for this workflow.

[dios]: https://makkook.ai/dios
[o9]: https://o9solutions.com/digital-brain
[kin]: https://www.kinaxis.com/sites/default/files/resources/Kinaxis-eBook-Navigating-the-deep-waters-of-supply-planning.pdf
[blue]: https://blueyonder.com/solutions/supply-chain-planning/integrated-business-planning
[sap]: https://help.sap.com/docs/SAP_INTEGRATED_BUSINESS_PLANNING/252c83bbf8184915a56d4cdd0917ef06/aa7171c1f43b4060aeaf57183c648282.html
[oracle]: https://docs.oracle.com/en/cloud/saas/supply-chain-and-manufacturing/26c/fausp/simulations-in-supply-planning.html
[siemens]: https://viewer.aps.sws.siemens.com/login.html
[pal]: https://www.palantir.com/docs/foundry/ontology/overview-ontology-scenario
[aip]: https://www.palantir.com/docs/foundry/aip
[aimms]: https://www.aimms.com/support/licensing/?show-element=academic-terms-and-conditions
[guro]: https://docs.gurobi.com/projects/optimizer/en/current/features/multiscenario.html
[fico]: https://community.fico.com/blog/fico-xpress-optimization-blog/fico-xpress-10-0---october-2026/9396
[ficolic]: https://community.fico.com/blog/fico-xpress-optimization-blog/fico-xpress-community-license-xpress-9-9-and-insight-5-17-2----may-2026/9324
[ibm]: https://www.ibm.com/docs/en/icos/22.1.1?topic=conflicts-refining-conflict-in-mip-start
[ort]: https://developers.google.com/optimization/cp/cp_solver
[ortrepo]: https://github.com/google/or-tools/blob/stable/ortools/dotnet/README.md
[timefold]: https://docs.timefold.ai/timefold-solver/latest/frequently-asked-questions
[time2]: https://docs.timefold.ai/timefold-solver/latest/constraints-and-score/overview
[hex]: https://www.hexaly.com/docs/last/csharpapi/optimizer/index.html
[py]: https://www.pyomo.org/installation
[pylicense]: https://github.com/Pyomo/pyomo/blob/main/pyproject.toml
[jump]: https://jump.dev/pages/code/
[mzn]: https://docs.minizinc.dev/en/latest/flattening.html
[mznlicense]: https://github.com/MiniZinc/libminizinc/blob/develop/LICENSE.txt
[op]: https://kie.apache.org/components/optaplanner/
[oplicense]: https://github.com/apache/incubator-kie-optaplanner/blob/main/LICENSE
