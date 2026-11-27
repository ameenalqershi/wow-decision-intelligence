# E02 — Enterprise PostgreSQL persistence

Scope: #76, REQ-E02 and TC-E02-A. Implements the enterprise registry and durable job admission on EF Core 10 / Npgsql 10.0.4 / PostgreSQL 16. The local M11 SQLite registry remains unchanged; the enterprise repository uses the same public UDIR and EDG model types and exact source/input digest rules. No REST route, common wire field or mathematical meaning is changed.

## Schema and identity

Initial migration creates schema wowdi, revisions, content, inputs and jobs, plus EF migration history. All identity text columns have explicit ordinal C collation; all links include the tenant namespace. Primary and foreign keys:

| Table | Key and relationships | Persisted content |
|---|---|---|
| revisions | tenant, model_id, revision_id | UDIR envelope bytes, EDG bytes, independent SHA-256 hashes |
| content | tenant, hash | Original source/input bytes, deduplicated only within the tenant |
| inputs | tenant, model_id, revision_id, hash | FK to full revision and full content key, both restrictive |
| jobs | tenant, job_id | Full FK to revision, tenant-scoped unique idempotency key, frozen request bytes and hash, trusted issuer/subject, UTC creation time, initial Queued state |

The repository accepts model/revision identity through the public registry domain contract, checks authorization via E01 TenantAccess (Write for saves, View for reads, Propose via BindJob for admission) before connecting, and applies exact tenant/model predicates and keys to every query. Caller-supplied tenant cannot authorize a read/write. Job lookup additionally requires the model scope. No unauthenticated API for raw repository access. Configure scoped EnterpriseRepository with DbContextOptions configured through EnterpriseDb.Configure, TenantAccess and TimeProvider. Never inject raw EnterpriseDb into untrusted endpoints. The database must use protected connection strings, TLS in production, restricted network access, separate migration/backup/runtime roles, and a least-privilege runtime role with only SELECT/INSERT on these tables for this delivery (no UPDATE/DELETE of immutable revision/content/input records). Future job state updates require explicitly scoped grants. Tenant isolation here uses application authorization, predicates and composite referential keys, not PostgreSQL row-level security. A shared runtime database credential is privileged across tenants; RLS and role hardening require separate deployment qualification. Do not enable sensitive parameter logging.

## Transactions and integrity

SaveRevisionAsync checks graph/revision identity, exact source and input digest sets, SHA-256 of each input and source UTF-8/UTF-16 length, before opening the transaction. INSERT ON CONFLICT DO NOTHING serializes competing writes for the same revision; a committed identical retry returns the existing revision, while any different model, graph or input content rejects with registry.revisionConflict. The winning insert, tenant-local content and all links commit atomically. Blob insertion follows ordinal digest order to avoid lock-order inversion. Foreign key restrictions preserve references. An interrupted write rolls back all three tables. Find uses a repeatable-read snapshot, checks model/graph hashes and all linked inputs, and reconstructs the original domain documents. Missing or denied keys yield absent; corruption yields diagnostic, never silently repairs or returns unverified bytes. The same logical source may be stored once per tenant. UDIR Write is the frozen serialized envelope; the actual original source files are retained verbatim. This matches M11 rather than promising byte identity with whitespace in arbitrary incoming UDIR JSON.

Job admission validates the authenticated binding, requires a registered revision, freezes at most 1 MiB of validated request bytes, and uses an atomic insert under the unique tenant/idempotency key. Identical retries by the same actor and for the same revision return the same job; a different body, actor, model or revision returns registry.idempotencyConflict. The host must validate the request's solver/capability/budget contract before admission and never include credentials. Job ID is server-generated; state begins Queued. The request's digest is rechecked on read. This is durable admission, not a scheduler: S12/E03 own leases, fencing, cancellation, workflow transitions, retry policy, outbox and result publication. They must not bypass the tenant and revision binding or mutate frozen request bytes. No shared queue or Redis dependency.

Conservative identity bounds: nonblank ordinal keys up to 256 characters, no controls or surrounding whitespace; job request 1 MiB. Host must enforce upload, graph/input byte quotas, connection pooling, query timeouts and request budgets; the repository does not claim to be a resource governor. SQL operations use parameterized EF Core APIs. No external actions performed.

## Migration and operational recovery

Use reviewed EF migration scripts or an explicit migration job, not uncoordinated migration from every API process. InitialEnterprise is generated from the committed model and snapshot; tests assert no pending model changes and repeat migration. Down drops data tables, so do not use it as a production rollback; take an encrypted backup and rehearse forward repair or restore into a separate database. PostgreSQL roles/grants, encryption, storage locations and scheduled backups are deployment concerns. A restore must re-establish privileges/ownership and requalify service readiness before any traffic. Production RPO/RTO, continuous backup, audit/evidence retention, reconciliation and disaster recovery remain E05/SEC03. SEC01's IEvidenceMovementStore is not implemented by this registry; evidence records, audit and reference semantics require S10/E05 integration and a separate migration. No other worktree is changed.

## SQL Server portability

Use a separate EF provider, migration assembly and conformance suite before claiming SQL Server support. PostgreSQL bytea corresponds to varbinary(max), timestamp with time zone to datetimeoffset with UTC normalization; PostgreSQL C collation differs from a SQL Server binary/ordinal collation and SQL Server may treat trailing spaces differently. Explicitly test ordinal and case behavior and key lengths (SQL Server index byte limits are different). PostgreSQL INSERT ON CONFLICT is not portable: implement a provider-specific transaction with unique constraint arbitration and safe retry/locking, not an unchecked read-then-insert or naive MERGE. PostgreSQL repeatable-read snapshot semantics differ from SQL Server SNAPSHOT/RCSI settings. PostgreSQL pg_dump/pg_restore is not SQL Server BACKUP/RESTORE; role/permission and recovery procedures also differ. Test all behaviors against both providers before dual support; SQL Server is not implemented or qualified here.

## Evidence and downstream consumers

Run locked restore and Release test suite under WOWDI_PG pointing to a disposable PostgreSQL 16 server with CREATEDB and major-16 pg_dump/pg_restore clients available (match the server major; newer clients can emit unsupported settings such as transaction_timeout). Each test creates a uniquely named database and drops only that database in teardown; no fallback to SQLite or in-memory provider. Missing environment configuration fails tests. Database migrations, SQL constraints, concurrent writes, idempotency, backup and restore are real. Authentication is synthetic only at E01's already-tested seam. Tests use public synthetic M11 factory fixture, and verify source bytes, graph incidence/capacity, tenant keys, same-key races, rollback after trigger failure, DB FK violation, tampered content/job, and restore into fresh database with restored migrations and jobs. No production database or user data is accessed.

#77 E03: use the durable job identity, implement transactional outbox and fencing.
#79 E05: persist protected audit and telemetry with tenant-safe retention.
#80 E06: supply migration/backup roles, deployment configuration and runtime least privilege.
#130 RT02: integrate actual server token admission and conformance with worker lease and cancellation.
#150 SEC03: qualify encrypted backups, RPO/RTO and restore with role/audit/queue reconciliation.
#148 SEC01: integrate its evidence store after S10/E05; do not mistake reference tests for production retention.
#37 S12: validate frozen run request and wire scoped repository without changing this revision contract.

No shared schema or planning files changed. All schema fields here are internal persistence fields; SP01/S12 remain authoritative for public field/API contracts.
