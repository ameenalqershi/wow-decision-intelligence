# M09 · Typed C# modeling SDK

The managed library `WowDi.ModelingSdk` constructs the existing `WowDi.Udir.Model` and freezes it using `UdirDocument.Create`, `ValidateMeasurements` and the F05 bounded writer. It neither compiles DML nor evaluates expressions, searches for a solution, selects a solver, or asserts feasibility. No existing domain, solver or shared wire contract is changed.

## Public API and boundaries

| Type | Responsibility |
|---|---|
| `IQuantity`, `QuantityType<T>`, `QuantitySymbol<T>`, `QuantityExpression<T>` | Declare marker types for physical quantities/currencies; operands of addition, subtraction, comparison and equality share a marker. One marker has one UDIR type in a builder; products/quotients name a typed result handle, whose dimension and scale are independently checked by M04. |
| `ExactValue` | Construct exact reduced rational inputs from integers, BigInteger pairs or explicit decimal conversion. No double/float overload or implicit rounding. UDIR checks canonical wire limits and integer domains. |
| `ModelSource`, `FrozenInput`, `FrozenMeasurementCatalog` | Defensive source snapshots, exact UTF-8 bytes, SHA-256 identities, UTF-16 spans. Catalog definitions are sorted/frozen and captured as an input digest; host must separately pin timezone rules and calendar data used in replay. Explicit FX conversion stores its versioned receipt. |
| `ModelBuilder` | Single-writer builder with typed variable, resource, parameter, expression, constraint, objective, temporal and scenario operations. Foreign-builder handles are rejected before insertion. Builds run existing structural and measurement admission, and enforce the bounded wire envelope. A successful build freezes the builder; repeat Build returns the same immutable snapshot. |
| `BuiltModel` | Immutable UDIR document plus frozen bytes keyed by digest for downstream registry publication; no mutable alias, database or network access. |
| `ModelBuilder.Import` | Delegates compatibility, schema and structural validation to M10 and then validates measurements with M04. Preserves compatible optional metadata and required capabilities. Does not imply input-byte verification, tenant authorization or solver admission. |

Use registry unit/currency symbols only at declaration; arithmetic is typed and does not accept unit expression strings. UDIR is the canonical persisted representation; the generic marker types are host-only and never appear in JSON. Callers choose meaningful source spans in actual supplied bytes; the library does not synthesize DML spans or source evidence. Exact conversion uses M04, not a second dimensional engine. Changing a frozen model requires another builder and an explicit new revision identity. The registry enforces immutable revision identity when publishing.

## Runnable examples

The sample contains production material allocation and workforce coverage models linked to frozen requirements files. It builds, validates, emits UDIR JSON to stdout and re-imports through M10/M04. These examples do not invoke an optimizer.

```sh
dotnet restore samples/ModelingSdk/WowDi.ModelingSdk.Sample.csproj --locked-mode
dotnet run --project samples/ModelingSdk -c Release --no-restore -- production
dotnet run --project samples/ModelingSdk -c Release --no-restore -- workforce
```

The production example defines marker types Batch, Kilogram, KgPerBatch, UsdPerBatch and Dollar. For example:

```csharp
var total = b.Multiply(b.Reference(qty, limitSpan),
    b.Reference(material, limitSpan), kg, limitSpan);
b.HardConstraint("available",
    b.AtMost(total, b.Reference(stock, limitSpan), limitSpan), limitSpan);
var revenue = b.Multiply(b.Reference(qty, profitSpan),
    b.Reference(price, profitSpan), usd, profitSpan);
b.Optimize("profit", revenue, Direction.Maximize, 1, profitSpan);
```

See the complete executable [ExampleModels.cs](../../samples/ModelingSdk/ExampleModels.cs). The opt-in compile-negative fixture attempts addition of mass and money. It must fail with CS0411; normal sample builds remain valid.

## Observed acceptance evidence and outstanding gate

- 21/21 tests pass on Linux in Release, including source integrity, deterministic freeze, exact rational and FX receipt, foreign handles, invalid type/measurement/domain cases, temporal operations, Boolean/scenario overrides and M10 compatibility.
- Same 21/21 pass with no external network routes (isolated Linux network namespace; loopback for test runner IPC). Both real sample processes also execute without any network interface up; outputs match normal execution byte-for-byte.
- Locked restore and negative compiler diagnostic CS0411 verified. Windows/Linux hosted validation is configured.
- On the observed Linux checkout the sample output SHA-256 values are production `3c15124448f77d3ffc326d068e6b39b076439638058b306fc4259caafef8166b`, workforce `ffc77b73c75d71d004b7211b5cd0d5e218bc18e9567d813c4a4e5c9bac9e2288`. These hash exact source bytes and canonical output, not semantic equivalence across arbitrary producers or checkout newline conversions.

TC-M09-A also requires actual normalized IR equivalence to DML. The real M05 grammar, M06 parser/binder and M07 normalizer (#18/#19/#20) must be available and qualified; they are not implemented in this SDK. Do not substitute a hand-authored UDIR or string fingerprint for the DML compiler. M09 remains open until equivalent production/workforce inputs run through both frontends and the approved common normalizer. RT04 (#132) requires its separate actual embedded solve and independent original-model verification; a successful package restore alone cannot close that issue.
