# M06 · Bounded declarative binding

`WowDi.Dml.Binding` consumes the accepted M05 syntax AST. The public entry point `DmlBinder.Bind(sourceId, utf8, context, grammarVersion, cancellationToken)` snapshots original bytes, invokes the bounded parser and returns a fully bound document or a diagnostic. No partial document is published. The bound document preserves the original immutable AST, original bytes, UDIR source identity, ordinal declaration table, reference edges with their exact use and declaration spans, and deterministic topological value order.

## Binding rules

- All named declarations share one case-sensitive ordinal namespace. Forward references are allowed. Duplicate IDs fail; no shadowing or implicit external names.
- Expression references resolve only to declared parameters, resources and decisions. Value initializers and decision bounds contribute to the value dependency graph. Kahn traversal uses ordinal tie-breaks and iterative residual-cycle detection; a cycle diagnostic identifies a real cycle edge. No arithmetic is evaluated at this stage.
- Source declarations must match an exact alias and schema supplied by the trusted host in `BindingContext`. The context carries no credentials, endpoint, path or executable. A trigger must refer to a declared authorized source; binding never reads or schedules it.
- Scenario assignments target parameters/resources, not decisions; duplicate overrides fail. Later semantic admission owns domains, types, uncertainty and constant evaluation.
- A hard policy must reference a hard constraint. Operational dependencies target resource, decision or window declarations. Their cycles are diagnosed separately from value-expression cycles.
- All original window, source, trigger, dependency, policy and scenario declarations remain in the bound AST. M07 must explicitly reject features it cannot represent or preserve them in the appropriate approved contract, not silently discard them.

## Diagnostics

| Code | Meaning |
|---|---|
| DML-BIND-001 | Unknown identifier, with up to three nearest in-scope suggestions |
| DML-BIND-002 | Duplicate declaration (related span identifies prior definition) |
| DML-BIND-003 | Wrong declaration kind |
| DML-BIND-004 | Cyclic value dependency |
| DML-BIND-005 | Cyclic operational dependency |
| DML-BIND-006 | Source alias or schema not authorized |
| DML-BIND-007 | Duplicate trigger, dependency or scenario override |
| DML-BIND-008 | Hard policy refers to soft constraint |

Syntax errors retain the original code/span/line/column. Binding uses stable first failure in source order; duplicate detection precedes reference resolution. Suggestions are scoped, deterministic, bounded to 512 ordinal candidates within length difference two; suggestions are data, not automatic corrections. They must only be displayed in the authorized source context. Resource bounds from M05 still apply. Traversal and sorting are bounded by the admitted tokens and declarations. Cancellation throws OperationCanceledException.

## Observed evidence

```sh
dotnet restore tests/Dml.Binding/WowDi.Dml.Binding.Tests.csproj --locked-mode
dotnet test tests/Dml.Binding -c Release --no-restore
sudo unshare -n sh -c 'ip link set lo up && dotnet test tests/Dml.Binding -c Release --no-build --no-restore'
dotnet run --project tools/Traceability -c Release
```

TC-M06-A covers the real M05 Batch/workforce examples, exact use/definition spans, unknown names and suggestions, duplicate and wrong-kind references, forward references, actual cycles, independent operational graph, authorized aliases, immutable input, syntax and resource limits, cancellation, and an acyclic chain of 4,000 definitions without recursive graph traversal. Binding is not a semantic or mathematical certificate; division, units, objective semantics and normalization belong to M07. No connector action or native solver invocation occurs.
