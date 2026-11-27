# Local runtime — RT01 workspace foundation

The caller supplies an explicit directory and a trusted local tenant on creation. No network endpoint, credentials, server listener or native worker starts when building the host. DI resolves the workspace lazily. A workspace contains the M11 registry and separate S10 evidence SQLite databases inside the selected directory; creation stages in the same parent and publishes by directory rename without overwrite. Existing non-workspace directories are rejected. Workspace metadata is preflighted read-only before the registry's migration logic executes. The existing ModelRegistry owns revision/source/graph integrity and schema migration.

## Storage boundaries

- A workspace pins a tenant and workspace UUID. All revision operations use that tenant. IDs are ordinal and immutable; no mutable revision alias is inferred.
- Frozen-input export is a local version 1.0 archive containing UDIR bytes, graph entities and relationships, and content-addressed input bytes, sorted deterministically. Replay validates SHA-256, envelope version, scope, graph and original source bytes, then uses the registry transaction. The digest is a content identity, not authentication or verification. The current shared-envelope size limit is 1 MiB; larger archive support requires an explicit compatible streaming format.
- UnverifiedRunArtifact records opaque run bytes under tenant/model/revision/run and SHA-256. It is immutable, retry-safe, transactionally linked to a frozen revision, and checked on read. It is intentionally **not** verified decision evidence, proof, approval or a solver result. No code path upgrades it to verified status. Maximum artifact bytes: 16 MiB.
- ActivitySource emits local workspace operation spans without tenant content or remote exporters; integration with the repository's approved observability setup remains downstream.
- No schema or API changes to UDIR, ModelRegistry, Solver.Contracts, Studio or CLI.

## Evidence and remaining gates

Observed Linux .NET 10 tests: eight passed normally and again within an isolated network namespace, with only loopback up and no routes. Run:

```sh
dotnet restore tests/Hosting/WowDi.Hosting.Tests.csproj --locked-mode
dotnet test tests/Hosting/WowDi.Hosting.Tests.csproj -c Release --no-restore
sudo unshare -n sh -c 'ip link set lo up && ip -brief address && ip route && dotnet test tests/Hosting/WowDi.Hosting.Tests.csproj -c Release --no-build --no-restore'
dotnet run --project tools/Traceability -c Release
```

Loopback is required by the test runner's local IPC; the first attempt with loopback disabled could not complete the runner handshake. This is a storage/workspace conformance test, not solver qualification. Windows automated tests are provided in local-hosting.yml.

The ledger lists F05 and M11 as RT01 dependencies. The design also requires actual isolated local solver invocation, independent feasibility and cost verification, and authoritative decision/evidence publication. These need S02/S03 (#27/#28), S04 (#29), S06/S07 (#31/#32), and S10 (#35), as applicable to the selected supported solver. The existing production Solver.Contracts types alone do not implement those features. RT01-04 and verified decision evidence are not complete; this branch must not be treated as closing RT01 until the actual integration and acceptance evidence exist. Coordinate any dependency-ledger correction with its owner.

## Local settings and recovery (API-SETTINGS, local provider)

The local workspace metadata schema is now v2; v1 upgrades transactionally to a singleton settings row at revision 0 with Information diagnostic level. Registry application/schema identifiers are preflighted before migration. The local provider has explicit workspace scope; LocalSettingsSnapshot and LocalSettingsChange carry WorkspaceId, config revision/expected revision and typed DiagnosticLevel (Error, Warning, Information, Debug, None). Compare-and-swap changes commit atomically; stale, invalid and foreign workspace requests fail without replacing the prior configuration. LocalDiagnostics reads the effective level, checks actual SQLite integrity and emits only fixed redacted log messages at the allowed severity. Solver and remote health remain not assessed. No credentials, arbitrary config dictionary, enterprise route or theme/language settings enter this provider. Solver-specific budget/options remain with the planner/worker contract.

WorkspaceBackup.RestoreCopy uses SQLite's consistent online backup API to a sibling staging directory. It checks integrity, foreign keys, every tenant-scoped frozen revision and artifact digest, plus settings, before publishing by no-overwrite directory rename. The restored workspace has its own identity; revision, run and artifact identities and settings remain preserved. Cancellation is checked before and after the synchronous backup and throughout validation; no partially restored directory is published. This does not claim authoritative verified evidence export until S10 integration. Filesystem access is governed by OS permissions, not a remote authorization boundary.

Validation: 13 tests cover settings CAS, concurrent writers, v1 migration, failed write rollback, redacted actual health, workspace restore and digest preservation, cancellation and corrupt artifact rejection. Actual solver, admission/cancellation/time-limit enforcement and verified evidence still depend on #27/#28, #29, #31/#32, #35. #89/#190 are merged; no shared contracts are duplicated.

Additional workspace isolation: local files are single-tenant even though the shared registry supports multiple tenants. Workspace preflight rejects foreign rows in revisions, content, inputs or artifacts; a contaminated snapshot cannot be published at restore. Newly created and restored Unix directories have owner-only 0700 permissions. Windows inherits the selected parent's ACL; this is not an encryption guarantee. Two additional tests pass locally (15 total; Unix permission test applies only on Unix).

## S10 integration and workspace schema v3

LocalEvidence composes the merged #35 library. The local host stages both SQLite files on creation. Legacy v1/v2 workspaces create or validate S10 before advancing metadata to v3; an interrupted migration is retryable. Missing evidence.db in an established v3 workspace fails closed. Both files are checked for tenant scope.

Capture is a trusted host boundary, not a user-supplied result endpoint. It requires the exact S10 measurement snapshot bytes already frozen in revision inputs. The SDK-private catalog representation is not automatically the S10 snapshot. Replay requires trusted package identity and hashes and does not publish supplied bytes. Unknown/rejected observations remain audit records. No opaque unverified artifact is promoted.

Cooperative local writers and backup hold a persistent exclusive file lease with a bounded 15-second wait and cancellation where exposed. Never delete the lease file during use. Both database snapshots are copied while writers are quiesced; staging checks revisions, artifacts, settings and recomputed S10 evidence before publication. This is not a distributed transaction or a security boundary against privileged direct database edits.

Observed: 22/22 Hosting tests normal and isolated Linux; upstream S10 8/8. Seven new tests cover capture/retry, lineage, portable replay after restoration, rejected observations, missing database, migration, frozen catalog, corruption, isolation, concurrent capture and cancellation while the lease is held. Contention testing uses multiple workspace handles in one process; independent-process and crash qualification remain pending. Fixtures are enumerated test witnesses, not native solver outcomes. Windows CI is configured.

Remaining: qualified native adapter execution (#7/#27/#28), actual planner and resource-governed worker invocation (#31/#32), durable run recovery and full offline acceptance. Do not close #129 on storage tests alone.
