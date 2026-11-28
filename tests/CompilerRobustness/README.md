# QA05 independent robustness evidence

Issue #147; baseline e29c62e36b3942a8e4f67dbec00cc1c30b2d3d5e. Coverage is a bounded integer-affine source profile with exact rational objective values whose denominator divides 3. Test-only C# references production components under test, not their internal arithmetic as oracle. For each fixed seed 0–63, exhaust x,y in [-2,2], evaluate the two original inequalities with integer operations, compare S04 original evaluation and lowered rows, cross-multiply the full scaled objective, and independently take the min/max numerator. Compare the S05 exact proof, feasible count, optimum and strength; a 24-point budget may not prove a 25-point domain. Test forged objective values, incorrect bound direction/order, unverified certificate, stale run/catalog binding and missing proof. A fixed four-case minimal negative source corpus checks explicit rejection without compilation. Seeds are identified in theory names; the TRX workflow preserves failing case identities. No automatically minimized counterexample is claimed (none encountered in the initial pass); retain any future counterexample as a fixed regression case before changing a generator.

Run with .NET SDK 10.0.115:
```
dotnet restore tests/CompilerRobustness/WowDi.CompilerRobustness.Tests.csproj --locked-mode
dotnet test tests/CompilerRobustness/WowDi.CompilerRobustness.Tests.csproj --no-restore -c Release
```

Initial Linux Release result: 68 tests passed, zero failed/skipped; 64 complete 25-point domains (1,600 assignments) and four negative source cases. No native solver, no universal proof, no LP certificate, no multiobjective or scenario semantics, no production performance or security qualification. Windows results pending hosted workflow. S04/S05 bounded verification is independently cross-checked within this profile; full audit of #29/#30 remains open.
