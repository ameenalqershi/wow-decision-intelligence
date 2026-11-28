# X03 — canonical model fingerprints and reuse permissions

Scope #40 / REQ-X03 / TC-X03-A. Predecessors #24/#20; consumer #41. This host-local managed component uses the original UDIR and exact immutable revision inputs, not solver outcomes or the compiler's normalized digest. It is not an authorization mechanism, a cache implementation, or a new public schema.

## Identity contract

Each part is domain-separated by profile version, artifact kind, tenant and model. Its SHA-256 is only an index. Equality requires both digest and exact canonical bytes; bytes are copied and retained privately, not exposed by the API. Canonical encoding is a versioned fixed-shape JSON convention, not RFC 8785. Ordinal sorting is applied to ID-keyed collections and input digests; operand order and objective priority retain their mathematical meaning. Stable IDs are required; semantic equivalence across arbitrary renaming, algebraic rewrites or reordered source text is not claimed. Raw original UDIR bytes are independently retained as provenance identity (including revision ID, source digests and source spans). No normalization of Unicode identifiers; reject ill-formed UTF-16 before serialization can alias identities. Unknown envelope extensions or minor versions are rejected.

| Part | Content |
|---|---|
| Structure | Typed symbol/expression identities, edges and operations, time basis, row/goal roots, scenario tree shape, required capabilities |
| Data | Exact bounds/literals, scenario overrides and probability, all registered input digests **and bytes**, selected catalog and fact digests |
| Constraints | Hard/soft identity, penalties and complete reachable original expressions |
| Objectives | Direction, priority, weight, tolerance and complete reachable original expressions |
| Configuration | Compiler/version/build, planner, verifier, solver/version/RID/package, numeric profile and exact capability/settings bytes |
| Provenance | Exact original UDIR, revision identity and exact source bytes |

Parts may overlap intentionally: a constraint literal can invalidate the data and constraint parts. No single part certifies model equivalence. Host selects an authorized M11 revision, materializes the exact original inputs and source bytes, and supplies approved nonsecret capability/settings bytes. All listed inputs and source digests must be present with exact verified content; extra inputs, mismatched hashes, incorrect UTF-8 source lengths and unregistered catalog/fact identities fail. Max 1024 inputs / 8 MiB total, capabilities 256 KiB and settings 64 KiB. Private canonical bytes can include confidential input content; do not expose them or use the digests as public cross-tenant lookup tokens. Host remains responsible for source/EDG completeness, authorized fact selection, measurement admission, actual artifact attestation and solver configuration validation. No EDG-only or hidden external resource equivalence is claimed.

## Conservative permissions

- Different tenant or model: no permissions.
- Unknown capability, scenario, soft or non-default objective profile: no permissions.
- Equal exact domain bytes: a prior assignment may only become a hint requiring full current-model independent verification, never a feasible/optimal assertion.
- Equal exact bytes for all five semantic/configuration parts **and** original provenance: a cached plan is only a candidate for host rebinding and current artifact validation. Reordered UDIR or changed revision may retain canonical inputs but invalidates plan provenance.
- No bound or evidence transfer is authorized, even at identical fingerprints. X04 owns actual cache admission, current verification and evidence-strength policy. No reuse based on a digest alone.

Equality of canonical admitted inputs is not a claim of support for every expression or native solver; downstream compiler, solver and verifier must independently admit the actual profile. The implementation intentionally allows safe false misses. For total serialized content L, sorting is O(K log K) for K keyed items, traversing both DAG slices O(N+E) before sorting, and hashing/byte comparisons O(L); no latency or memory benefit is asserted.

## Reproduce

```sh
dotnet restore tests/Optimization.Fingerprints --locked-mode
dotnet test tests/Optimization.Fingerprints --no-restore -c Release
```

Seven tests cover reorder, deletion, changed objective/literal, changed tenant/model/revision/compiler/solver/RID/settings, actual data/hash/domain/units, unsupported profiles, missing sources, snapshot mutation and malformed Unicode. Linux SDK 10.0.115: 7 passed, 0 failed, 0 skipped. Hosted Windows/Linux runs are independent qualification. X04 must retain bytes/identity and never promote a hint to evidence.
