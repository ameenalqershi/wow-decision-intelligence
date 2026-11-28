# Exact bound certificates for LP and MILP relaxations: research (proposed X13; registration requested on #182)

**Readiness: RESEARCH.** This is not wired into S05/`EvidenceClassifier`. Solver attestation and certificate evidence stay distinct.

## Method

The method is the box-Lagrangian safe bound (Neumaier & Shcherbina, *Safe bounds in linear and mixed-integer linear programming*, Math. Programming 99, 2004), computed in exact rational arithmetic.

For `min c·x` subject to `lo ≤ Ax ≤ hi` and `l ≤ x ≤ u`, any multiplier vector `y` gives:

    c·x ≥ Σ_i y_i·b_i + Σ_j min_{x_j∈[l_j,u_j]} (c_j − (Aᵀy)_j)·x_j,   b_i = lo_i if y_i>0 else hi_i

- **Unused multipliers.** A multiplier whose required bound is infinite is set to 0.
- **No finite bound.** If a reduced cost needs an infinite column bound, the certificate reports that no finite bound exists. It never invents one.
- **Validity under any multipliers.** The inequality is weak duality, so it holds for **any** `y`. Wrong, noisy or adversarial multipliers can only weaken the bound. That is the property that lets us use floating solver duals without trusting them.
- **Cleaned candidates.** Floating duals carry about 1e-16 noise, which can flip the sign of a reduced cost on an unbounded column. `SafeBound.Cleaned` also tries both signs, dyadic roundings and continued-fraction approximations of the duals, and keeps the tightest finite bound. That choice affects only tightness, never validity.
- **Integral objectives.** When every costed column is integer with an integer cost and the offset is integer, the bound is rounded inward (ceiling for minimization) to the integer optimum's lattice.
- **Assessment.** An independently verified incumbent value `v` and a bound `L` give one of three outcomes:

  | Outcome | Condition |
  |---|---|
  | `ProvenOptimal` | gap is 0 |
  | `CertifiedGap` | an exact gap is reported |
  | `Contradiction` | `v` beats a valid bound, which means a defect in the model, data or verifier, raised as a critical error |

**Limits.**
- For a MILP this certifies only the LP-relaxation bound. Branch-and-bound proofs need VIPR-style certificates (exact SCIP), which is future work.
- Infeasibility certificates (Farkas rays) are not exposed by the HiGHS 1.16 managed API.

## Tests

`research/ScaleStudy/Tests` covers four properties:
- weak duality under arbitrary multipliers
- the bound never exceeds the enumerated integer optimum
- exact duals prove optimality
- contradictions are detected
