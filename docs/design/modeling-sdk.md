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

## Shared semantic admission and frozen facts

`ModelBuilder.Build()` performs structural and measurement admission, not solver admission. Call `BuiltModel.Normalize(catalog)` to invoke the merged M07 normalizer, with the identical frozen catalog bytes and digest. Unknown/unsafe expressions fail through the shared diagnostic; the SDK does not implement an alternate normalizer.

Call `FreezeFacts(params FactBinding[])` explicitly to produce the S04 flat JSON string-valued baseline snapshot. Use typed `Fact(symbol, exactValue)` or Boolean overloads for ranged/Boolean non-variable facts. Fixed numeric domains supply their declared value; missing facts, variables, foreign handles, duplicates, fractional integer or out-of-domain facts reject before publication. Facts are ordered ordinally, added to the immutable input digest set, and exposed as BuiltModel.FactsDigest. Not requesting a snapshot is allowed for model authoring; it does not imply solve readiness. The independent verifier must still check the original model and supplied frozen bytes.

## TC-M09-A: actual frontend equivalence

Both production and workforce are expressed as typed C# in ExampleModels.cs, independently authored DML in production.dml/workforce.dml, and independent declarative UDIR JSON inputs under Json/. The JSON inputs are not generated by either frontend: only source offsets and digests are mechanically derived from their real requirements/fact/catalog bytes. Each frontend has its own revision and source identity. The test invokes DmlBinder + DmlCompiler on actual DML, ModelBuilder on C#, VersionedModelImport on JSON, and the common ExactNormalizer on the three results. It checks identical canonical mathematical bytes, exact frozen fact bytes and catalog, and distinct revision/source bytes. Production root explicitly fixes baseline stock to 10 kg, the shortage branch to 4 kg, and the admitted non-variable domain to [4,10]. No implicit probability or native solver result is invented.

For workforce the real independent S04 verifier checks all 13 assignments per frontend, 39 witnesses total, with the original model digest and frozen facts. These are enumerated acceptance witnesses, not claimed native solver outcomes. The current verifier does not support scenario execution; production equivalence is mathematical structure and facts only, not evidence of a scenario solve. The exact normalized projection is not a revision or evidence identity.

31/31 tests locally cover the original typed API, compilation-negative mixed units, explicit fact binding failures, unsafe division admission, catalog mismatch and the frontend comparison. The same fixture sources are pinned to stable bytes across checkout platforms. CI executes Windows/Linux tests and the sample executable twice. RT04 (#132) still requires independent local-feed package consumer execution and actual qualified native optimization and verification; M09 acceptance is not that runtime qualification.
