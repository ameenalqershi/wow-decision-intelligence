# I01 — Connector SDK and versioned mapping manifest

Scope: #55 / REQ-I01 / TC-I01-A. This is the optional Integration Hub contract, not an enabled connector, a vendor qualification, or executable mathematical logic. It references only F05 contract validation and the .NET framework. No core project references it.

## Wire format and identity

The F05 envelope contract family is wowdi.integration.connector-manifest, major 1. The payload has explicit required fields (nullable fields are still required); unknown payload fields, duplicate JSON properties, invalid versions, unknown required extensions and oversized or over-deep envelopes fail closed. Optional extensions are preserved for compatible relaying, not interpreted as authorization or executable configuration. Untrusted extension metadata must not be logged or forwarded without the applicable data policy.

| Field | Meaning |
|---|---|
| connectorKind | Stable ordinal registry ID; parsing does not authorize installation or activation. |
| sourceSchema / sourceSchemaVersion | External schema identity and exact vendor/source revision, without binding to any ERP. |
| targetSchema / targetSchemaVersion | Target import schema identity and revision; target field IDs must be reviewed against SP01/M10 before admission. |
| mappingVersion | Canonical major.minor; immutable mapping revision. Any change of source, target, transform or policy creates a new mapping revision. |
| fields | One or more bounded typed mappings, unique target field IDs. |
| provenance | Ordered nonempty sourceKeyFields, exact sourceTimestampField. |
| cursor | Mode and maximum batch rows. |
| retry | Bounded attempts including the first attempt, initial and maximum delay in milliseconds. |
| reconciliation | none or idempotency-and-lookup; capability declaration only. |
| timePolicy / timeZoneId | Explicit time interpretation, no ambient local timezone. |
| currencyPolicy | none, explicit-currency, rate-snapshot. |
| credentialReference | null or a nonsecret opaque registry reference such as cred:warehouse-read; not a credential or URL. |

## Field mappings

Each entry declares sourceField and targetField, sourceType and targetType, sourceUnit and targetUnit, required and allowNull, and transformId and transformVersion. Source field is a literal source-schema identifier (not code or a filesystem path); target field is an ordinal stable ID. A required field must be present; allowNull independently governs explicit null. Absence is never zero. The allowed mapping transport types are boolean, integer, decimal, text, instant, duration, quantity, money, rate. This vocabulary is an ingestion seam, not a change to UDIR/DML types. Exact source number parsing, decimal representation and domain validation belong to I02/M10. Duration/quantity/money/rate require explicit units; other types require null units. Identity transform version 1.0 cannot change type or unit. Other transform IDs are declarative registry references, not arbitrary code; activation must resolve an allowlisted, versioned transform and validate dimensions/units against the approved domain contract. A money mapping requires a currency policy; cross-currency conversion requires an explicit rate snapshot and timestamp. Never infer conversions.

Time policy utc-only accepts UTC source timestamps; explicit-offset requires explicit offset; named-zone requires an available zone identifier and rejects ambiguous/nonexistent local times. The reader validates declarations; adapter implementations normalize row timestamps to UTC and must preserve original timestamp provenance before publication.

## SDK and synchronization

IConnectorSource exposes read-only ReadAsync with cancellation. The host supplies a trusted ConnectorStreamIdentity (tenant, connector instance, mapping version, stream/partition). It does not obtain tenant identity from input payloads. Source schema/version and manifest digest are bound in the host's registry to the instance and mapping version. Rows carry ordered source key components, normalized UTC source timestamp and a source JSON object. Structural checks reject mismatched mapping versions, oversized batches, invalid keys/positions or non-object rows; field-level payload validation and duplicate event reconciliation remain with I02/I03/I04.

| Cursor mode | Contract |
|---|---|
| snapshot | Stage immutable snapshot pages. Incomplete page advances an ephemeral opaque continuation; final page has null Next. Do not publish a partial revision or treat the staging token as a durable checkpoint. |
| opaque | Cursor is an opaque bounded token. A nonempty or incomplete page must return an advancing token. No assumptions about sorting or decoding. |
| offset | Nonnegative partition-specific integer offset; no regressions, strictly advances on nonempty or incomplete pages. |

Complete indicates end of the current scan, not permanent end of the stream. In incremental modes an empty final page may omit Next, meaning retain the committed position. Cursor contents may be confidential and must not appear in telemetry. Checkpoint publication is an I04 transaction with validated input receipts and revision writes; stream identity and mapping revision are part of the checkpoint key. A change to mapping/schema requires an explicit replay or migration, not cursor reuse. Host must enforce bounded scan duration, batch bytes, retries and cancellation beyond these structural limits.

Retry attempts are 1..10; delays 1..3,600,000 ms, maximum >= initial. A bounded backoff policy may retry eligible transient reads only. Validation/authorization errors quarantine or fail without retry. Initial contract bounds: 4096 mappings, 32 source key fields, 10,000 rows per batch, 4096-character opaque cursor. These are conservative contract limits for review, not performance measurements. I04 owns persisted retry state, jitter/backoff, checkpoint transaction and recovery.

## Credential and execution boundary

No credential value field, endpoint secret, bearer token or arbitrary script is accepted by the manifest payload. A syntactically valid reference still requires server-side tenant and connector-instance authorization; secret resolution occurs in the isolated connector scope only. Never log manifests/opaque extensions/cursors indiscriminately. External network destinations, egress, source permissions and plugin activation remain host-controlled. A parsed manifest never permits an external write. Even idempotency-and-lookup is only a capability declaration. Any future write must use E04/S11 authorization, verified immutable revision, approval, target-bound idempotency and reconciliation; unknown side effect remains suspended, no blind retry. No vendor adapter is qualified by this work.

## Verification and downstream ownership

Run locked restore and release tests in tests/Integration.Connectors. The tests use synthetic factory fields, malformed contracts, credential/transform negative cases, provenance, time/currency and cursor constraints, and do not contact any external system. I02 implements snapshot CSV/JSON ingestion and semantic field checks; I03 authenticated REST/webhook ingestion; I04 transactional sync/recovery; I05-I09 vendor qualification; SEC01 secure export/retention; U06/UI20/UI21 render authorized connector metadata only. SP01/M10 remain authoritative for target field schema. These consumers should review the manifest version before wiring their implementations. No planning, engine or common wire envelope changes are introduced.
