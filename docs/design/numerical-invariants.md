# NUM01 · Numerical invariants and reproducibility

This suite exercises the actual DML parser/binder/compiler, measurement registry, affine lowering, independent original-model verifier and evidence classifier. No production arithmetic or independent verification logic is duplicated. Test witnesses are explicitly enumerated assignments, not native solver outcomes.

## Managed acceptance evidence

- Equivalent kg/g models: all nine integer assignments in each original model have identical feasibility and physically equivalent objective values after explicit M04 conversion. Their semantic digests are intentionally distinct.
- Exact boundary residuals: just below, equal to, and just above 1/2. A nonzero solver-reported tolerance cannot hide a hard-row violation or convert an approximate objective into an exact observation. Non-finite and noncanonical witness strings fail with source-linked diagnostics.
- Integer endpoints, exact FX effective interval, incompatible currencies, nonintegral conversion and conservative binary64 limits are independently tested. The latter concern representation of inputs, not backend floating-point stability.
- Eight repetitions of the same exact model and bounded enumeration compare original bytes, normalized bytes, status, strength, optimum and witness. No seed or runtime claim is inferred from managed deterministic enumeration.

TC-NUM01-A and B are partially evidenced by this suite. The S04/S05 production profile currently rejects the explicit rational.linear capability; M08 admits exact binary64 input for that capability. Do not remove the capability to make a continuous solver result appear admissible. Coordinate the continuous-profile verifier/tolerance policy with the S04/S05 owner. Boundary tests using the currently admitted integer.linear profile and rational domains characterize existing arithmetic only, not qualification of a continuous solver. Native stability and versioned production solver seeds remain outstanding; R07/S02/S03 must establish the backend profile and repeated actual worker execution. No exact certificate is inferred from floating-point agreement.

Run:
```sh
dotnet restore tests/Numerical.Invariants --locked-mode
dotnet test tests/Numerical.Invariants -c Release --no-restore
sudo unshare -n sh -c 'ip link set lo up && dotnet test tests/Numerical.Invariants -c Release --no-build --no-restore'
dotnet run --project tools/Traceability -c Release
```
