# M10 · Versioned JSON model import

The import boundary is the separate .NET library WowDi.ModelImport. It consumes the authoritative M03 UDIR model and F05 compatibility reader without changing either. It exports the embedded [JSON Schema 2020-12](../../src/ModelImport/Schemas/udir-v1.schema.json), with local references and no dynamic schema selection from input. The trusted schema is compiled once in its own registry (no global schema registration collision). JsonSchema.Net 9.7.0 is pinned in the package and locked restore.

```csharp
using WowDi.ModelImport;

var result = VersionedModelImport.Read(utf8Model);
if (!result.Accepted)
{
    // Present the stable stage and code, not untrusted input or exception text.
    Report(result.Diagnostic!.Stage, result.Diagnostic.Code);
    return;
}
var document = result.Document!;
// Next: trusted tenant admission, original source digest validation and
// immutable registry publication by M11; dimensional admission by M04.
```

| Stage | Validation and failure |
|---|---|
| Compatibility | F05 verifies bounded UTF-8 JSON, duplicate names at any depth, contract identity, supported major, version range and required extension support. Unknown mandatory extensions fail; optional metadata survives. |
| Schema | JSON Schema independently checks required properties, closed object shapes, supported semantics profile, scalar types/enum spellings, exact numeric string grammar and bounded integer/source/hash representation. No network, no source evaluation, no domain claim. |
| Domain | Existing UdirDocument.Read performs authoritative M03 checks (canonical rational reduction, ordered bounds, integer domains, reference integrity, expression arity/cycles, source spans and scenario semantics). |

ValidateSchema runs only compatibility and schema checks, returning a redacted diagnostic or null. Read adds domain checks, returning an immutable UdirDocument only on complete success. Both use a bounded defensive copy of caller memory. Failures never publish a revision. The exact original input remains caller-owned; canonical output preserves supported minor version, payload semantics and opaque optional extensions. Unknown required engine capabilities are preserved for the planner to reject when unsupported, not interpreted as approved.

The shape schema intentionally does not reimplement M03 cross-field semantics, M04 dimensional algebra, backend capability admission, independent verification or migration to an unsupported wire major. JSON Schema's mathematical integer definition can admit a lexical numeric form rejected by the typed .NET reader; both stages must pass. The schema is a validation artifact, not a wire contract change. Downstream I02 (#56) and CLI04 (#116) should use this library before registry publication. M09 SDK and compiler may consume the same authoritative domain types.

## Acceptance (TC-M10-A)

```sh
dotnet restore tests/ModelImport/WowDi.ModelImport.Tests.csproj --locked-mode
dotnet test tests/ModelImport/WowDi.ModelImport.Tests.csproj -c Release --no-restore
sudo unshare -n sh -c 'ip link set lo up && ip -brief address && ip route && dotnet test tests/ModelImport/WowDi.ModelImport.Tests.csproj -c Release --no-build --no-restore'
```

24 tests use the merged factory fixture and controlled negative variants. They separately reject schema errors, malformed/reversed/fractional bounds, duplicate names, unknown mandatory semantics, unsupported versions, dangling references, duplicate symbol IDs, bad spans, over-size/depth input; preserve optional metadata and opaque required capability tokens; and compare concurrent canonical reads. The synthetic variants are validation inputs, not solver evidence. No UDIR, registry, compiler or unit-semantics source was edited.
