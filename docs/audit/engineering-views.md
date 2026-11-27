# Engineering views · الرسومات الهندسية

[Complete dependency network (SVG)](diagrams/dependencies.svg) · [Editable Graphviz source](diagrams/dependencies.dot) · [Traceability and baseline](engineering-audit.md)

Every node carries its scope key, GitHub number and a link to the live issue. An arrow runs from prerequisite to consumer. Epic membership is a different relationship in planning/parents.json. The complete diagram is intended for zoom and search; use the views below for release and product decisions. Regenerate the source with the .NET validator and render with Graphviz.

```sh
dotnet run --project tools/Traceability -- --write-dependency-graph
dot -Tsvg docs/audit/diagrams/dependencies.dot -o docs/audit/diagrams/dependencies.svg
```

## System context · سياق النظام

```mermaid
flowchart TD
  O["Planner / المخطّط"] --> S["Studio or CLI"]
  P["Enterprise system / المصدر"] --> I["Integration Hub"]
  D["Developer / المطوّر"] --> K["Embedded SDK"]
  S --> A["Shared application ports"]
  I --> A
  K --> A
  A --> E["UADIE and original-model verifier"]
  E --> V["Versioned decision evidence"]
  V --> O
```

Product boundaries are defined by RT01–RT05. A connector imports authorized, unit-normalized snapshots. Returning a decision to a planner does not authorize an external write. The approved application flow, storage/solver components and sequence are in the [runtime design](../design/operating-modes.md).

## Release dependencies · اعتماديات بوابات الإصدار

```mermaid
flowchart TD
  R["G01 · Research evidence"] --> REVIEW["Scientific promotion review"]
  F["SL01 · Factory replay"] --> M["G02 · MVP"]
  N["R07 · Native qualification"] --> M
  QA["Desktop and CLI conformance"] --> M
  M --> P["G03 · Production"]
  P --> E["G04 · Enterprise"]
  P --> W["G05 · Multi-sector"]
  S["SL02 · Workforce proof"] --> W
  E --> C["G06 · Scale"]
  L["E08 · Capacity and fairness"] --> C
```

Solid arrows summarize selected dependencies. G01 permits a bounded research conclusion and has its own decision. All exact release prerequisites, including security and recovery, are in the complete network. A green CI run for a research fixture cannot approve G02.

## Trust boundaries · حدود الثقة

```mermaid
flowchart TD
  U["Untrusted import / إدخال خارجي"] --> T["Schema, bounds, units and identity checks"]
  T --> H{"Tenant and policy authorized?"}
  H -->|no| R["Reject and audit"]
  H -->|yes| B["Freeze source revision"]
  B --> W["Isolated worker without connector credentials"]
  W --> V["Independent original-model verification"]
  V --> K{"Witness accepted?"}
  K -->|no| R
  K -->|yes| A["Evidence and human approval"]
  A --> O["Scoped outbox and reconciliation"]
```

Inputs cross the Integration Hub boundary (I01, SEC01). The worker boundary (E07, EXT01) separates native code and tenant credentials. The independent verifier (S04) receives the original snapshot, not merely the lowered model. Any future write must satisfy the lifecycle and approval policy (S11, E04).

## Incremental optimization · الحل الكامل وإعادة الاستخدام

```mermaid
flowchart TD
  I["Old and new frozen incidence"] --> C["Find changed constraint and objective scopes"]
  C --> B{"Certified independent blocks?"}
  B -->|no| F["Full solve on new revision"]
  B -->|yes| M{"Unchanged semantic identity?"}
  M -->|yes| R["Reuse eligible component evidence"]
  M -->|no| S["Solve changed blocks"]
  R --> J["Reconstruct candidate"]
  S --> J
  J --> V["Check original constraints and objective"]
  F --> V
  V --> Q{"Acceptable evidence?"}
  Q -->|yes| E["Publish qualified result"]
  Q -->|no| D["Discard candidate and record diagnostic"]
```

The conventional baseline rebuilds and solves the full new revision before the same independent check. The proposal tries to avoid re-solving unaffected blocks. A shared capacity or joint objective may merge all blocks and remove the benefit. H1's 391 finite oracle comparisons cover the research fixture, with no demonstrated general speedup. See [prior art and measured limits](../research/innovation.md).
