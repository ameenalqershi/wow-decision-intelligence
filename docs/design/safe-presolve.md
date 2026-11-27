# M12 · Conservative exact presolve
Consumes the M07 admitted normalized graph without modifying the original UDIR or its revision identity. The projection exposes retained variables, rows, original objectives via Original, source-linked rewrite entries and contradiction records. No solver or independent proof implementation is duplicated.

- Remove a hard row only when its normalized predicate is the literal true, or it has the same canonical predicate as an already retained hard row. M07 compares full canonical node keys before interning. No soft row is removed; each penalty retains its own identity. A false hard predicate is retained and reported with its source span. Absence of a contradiction is not proof of feasibility.
- Remove a non-temporal variable only if no original expression references it, and no scenario or unrecognized mandatory feature is present. Its reconstruction value is an admitted lower endpoint, else upper endpoint, else zero; Boolean uses false. M03 already rejects reversed or invalid exact bounds. No domain tightening or rounding. Temporal variables are conservatively retained.
- The projection and lift require exact ordinal variable identity; the lift supplies only the recorded eliminated values. Neither operation substitutes for original-model checking. Every original feasible assignment projects to a feasible reduced assignment with identical objective and soft penalties; every feasible reduced assignment lifts to an original feasible assignment with the same observations. Original expressions and all source records remain available. Unsupported profiles pass through unchanged.
- This is a standalone transformation interface, not yet an execution path: adapters must explicitly consume its retained rows/columns and reconstruct against the original model before using S04. No claim of native runtime acceptance.

TC-M12-A: exhaustive 125 original assignments and 25 reduced assignments across two bounded decision variables and an unused variable; compare projected feasible sets, lift witnesses, objective values and soft-row violations. Additional tests retain hard contradictions, unknown capability and scenario profiles, exact rational and Boolean witnesses, and reject empty domains at UDIR admission. Cancellation publishes no result.

Run:
```sh
dotnet restore tests/Dml.Presolve --locked-mode
dotnet test tests/Dml.Presolve -c Release --no-restore
sudo unshare -n sh -c 'ip link set lo up && dotnet test tests/Dml.Presolve -c Release --no-build --no-restore'
```
