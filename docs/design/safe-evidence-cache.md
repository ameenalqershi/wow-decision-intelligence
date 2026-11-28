# X04 — exact evidence replay cache

Scope #41 / REQ-X04 / TC-X04-A; predecessors #40/#30, consumer #42. Integrates the already merged #35 package/replay and #24 immutable SQLite registry. The cache is a host-local, bounded LRU of existing evidence packages. It neither issues a new certificate nor grants execution/publication/authorization. Its key is the trusted durable tenant/model/revision/run binding, exact attempt invocation (including verifier build, budget and timestamps), EvidenceId and package SHA-256. These values must come from an authorized durable attempt/evidence ledger, not an HTTP caller or a cached artifact.

## Admission and replay

1. Resolve the actual immutable revision from the registry under the trusted tenant. Validate current X03 fingerprints against all original input bytes, source provenance and current configuration. This cache additionally requires catalog and selected facts to be registered original inputs, a stricter profile than general S10.
2. Verify the exact package SHA-256 and manifest EvidenceId; run S10 replay, which recomputes S05 and S04 against the original document/facts/catalog. Compare the full recomputed assessment bytes to the stored assessment, not just hashes. Require exact binding, invocation, solver identity, profile, capability/settings bytes and declared configuration. The compiler build SHA-256 is trusted host metadata, not attested by S10; changes invalidate the cache.
3. Reject previously rejected evidence and unsupported X03 profiles. Snapshot the package and exact fingerprint bytes before storing. A matching retry must have the same package bytes and canonical identities; conflicts reject. Maximum 1–8 entries (default 4), bounded S10 package and X03 input profiles; eviction is least-recently-used.
4. On every read, require exact key, current registry revision and full X03 canonical/provenance byte comparison. Replay S10/S05 again and require the same trusted identity/configuration. Any mismatch is a miss with no record. No solver attestation is elevated to independent proof; even an identical fingerprint from a new run/attempt cannot transfer a bound or evidence. No independent certificate beyond existing S05 is claimed.
5. Cache index and eviction are synchronized; expensive validation occurs outside the lock. A read whose entry was evicted during replay misses. Tenant invalidation removes tenant entries and changes an admission epoch to reject in-flight insertions across invalidation. The host must perform current authorization and durable fence checks at each access/publication, and invalidate on revocation or retention. A returned object does not grant permission and cannot be revoked from memory after return. The cache is not a durable retention ledger or cross-process lease.

This is a conservative safe replay baseline, not a latency optimization claim: replay reruns mathematical verification. Entry count and each encoded artifact are bounded, but no global process memory quota is established here. The cache does not execute a solver, skip current verification or implement cross-revision bound transport; #42 handles current-model hints and repair and may fall back to full solving. Unknown, LP, stochastic, multiobjective and native qualification remain outside this acceptance profile.

## Evidence

Five Linux locked Release tests with real SQLite files and S10 packages: exact proof and solver-attestation replay without strengthening; changed unit scale, objective direction, data, adapter/compiler/verifier/settings and stale run/attempt; forged package and rejected mathematical witness; cross-tenant denial, concurrent exact admission and reads, eviction and tenant invalidation. Tests assert exact assessment bytes and separate primal versus bound strengths. No prior cache existed; no before-performance claim.

```sh
dotnet restore tests/Optimization.Cache --locked-mode
dotnet test tests/Optimization.Cache --no-restore -c Release
```
