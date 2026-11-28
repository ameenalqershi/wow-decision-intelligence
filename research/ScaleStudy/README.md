# Scalable exact model representation: research prototype (proposed X12; registration requested on #182)

**Readiness: RESEARCH.** This prototype does not replace or modify UDIR, which remains the semantic reference. Any contract change goes through a reviewed contract PR with the model team.

## Design

UDIR stores every coefficient, product and sum as its own typed expression node inside a JSON envelope. That costs about 608 bytes per nonzero, so the 1 MiB envelope stops at roughly 1,200 nonzeros. The sparse prototype keeps exactness but changes the layout:

| Aspect | Design |
|---|---|
| Rows | Compressed sparse row (CSR): `lo ≤ a·x ≤ hi` per row; ranged and equality rows in one form |
| Coefficients | Indices into a deduplicated pool of exact rationals (`Q`, bounded bit length). Industrial models reuse few distinct values (123 values for 9.9 M nonzeros in the lot-sizing family), so the structure costs 8 B per nonzero plus a tiny pool |
| Column view | CSC by counting sort (O(nnz)) when column access is needed |
| Encoding | Canonical little-endian binary (`WDSM` v1) with the value pool sorted by value. A streaming SHA-256 is identical for identical models regardless of builder insertion order. Decoding re-validates every index, ordering, canonical rational and size limit |
| Verification | Exact (`SparseVerifier`). A checked Int128 fast path handles integer rows with integer assignments; anything fractional or overflowing goes to the exact rational path. Doubles convert to their exact dyadic value, never rounded. Rows are processed in fixed blocks merged in order, so results are deterministic for any parallelism. Tolerances are an explicit, separate policy and never applied silently |
| Limits | `SparseLimits` (columns, rows, nonzeros, distinct values) are explicit guard rails and exceeding one is a diagnostic. UDIR limits are untouched |

**Equivalence.** `tests/SparseTests.SparsePathsAgreeWithUdirOriginalVerifier` compares three independent paths on randomized models and random assignments:
1. DML → UDIR → `OriginalVerifier` (the reference)
2. DML → `ExactLowerer` → `UdirBridge` → `SparseVerifier`
3. generator arrays → `SparseBuilder` → `SparseVerifier`

All three must agree on domain violations, row feasibility and the exact objective.

## Run

~~~sh
dotnet test research/ScaleStudy/Tests -c Release          # env: WOWDI_DIFF_MODELS, WOWDI_BOUND_CASES, ... scale the case counts
dotnet build research/ScaleStudy/Bench -c Release
systemd-run --wait --pipe --collect --nice=10 --property=MemoryMax=3G --working-directory="$PWD" \
  --setenv=DOTNET="$(readlink -f "$(command -v dotnet)")" /bin/bash research/ScaleStudy/run-scale.sh scale.jsonl
systemd-run ... /bin/bash research/ScaleStudy/run-bench.sh <dir with instances + refs.tsv> bench.jsonl 30
~~~

The evidence and summary are in `evidence/`.
