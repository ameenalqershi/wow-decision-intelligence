# S06 · Capability-first planning

SolverPlanner accepts the admitted normalized model, a trusted deployment catalog of adapter profiles, the requested runtime, numerical requirements, interruption/bound requirements, and the existing S07 WorkerBudget. It uses the actual M08 lowering as a semantic admission gate; a matching feature label alone is insufficient. Catalog entries must be established by the deployment host from qualified artifacts and evidence (R07/S02/S03). A syntactically valid qualification digest is only an identity, not validation of qualification. No default or fallback backend exists.

The initial policy favors CP-SAT for all-integral affine inputs and HiGHS for models with continuous variables; it only ranks eligible profiles. Ordinal solver ID, version and runtime ID are deterministic tie-breaks, not an assertion that a lexical version is the newest or fastest. Every examined profile receives an explicit eligible/rejection code, and lowerer source spans survive rejection. A zero-candidate result means unsupported under the provided profile/budget, not mathematical infeasibility. Unknown capability requirements, nonlinear or scenario constructs and unrepresentable numerical coefficients are rejected by the managed lowering boundary.

ExactInput requires exact representation during lowering, not exact backend arithmetic. IntegerArithmetic excludes binary64 profiles. Native evidence and actual result tolerance remain independent. The planner checks requested runtime, capability, interruption, bound support and declared minimum thread/memory/deadline requirements. The chosen worker reservation is capped at the manifest's thread maximum; the original memory and deadline remain unchanged. This policy does not predict actual resource consumption or guarantee completion. S07 enforces admission, process isolation and actual deadline; S12 handles durable state. No algorithmic performance or native qualification is claimed.

The catalog is bounded to 64 distinct solver identities and validated before selection. Facts are copied before evaluating candidates and M08 checks their digest. Cancellation propagates; no solver is started by planning. The profile includes the precise qualified artifact identity to be checked again by the execution host. Only trusted host code may construct the catalog; never admit it from DML, CLI user-supplied strings or untrusted API payloads.

TC-S06-A tests exercise deterministic tie-break under reordered input, exact continuous admission, resource/runtime/evidence-feature rejection, real M08 nonlinear/non-dyadic rejection despite capability declarations, and empty/invalid catalogs. These are policy tests with explicitly test-only profile descriptors, not solver outcomes or native qualification.

```sh
dotnet restore tests/Solver.Planning --locked-mode
dotnet test tests/Solver.Planning -c Release --no-restore
sudo unshare -n sh -c 'ip link set lo up && dotnet test tests/Solver.Planning -c Release --no-build --no-restore'
```
