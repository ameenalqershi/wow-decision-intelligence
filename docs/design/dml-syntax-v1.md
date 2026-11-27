# M05 · DML 0.1 syntax contract

The syntax library `WowDi.Dml.Syntax` implements a bounded UTF-8 lexer and parser for the declarative grammar in [language.md](language.md). It produces immutable, source-linked AST nodes. It does not resolve symbols, read connectors, execute source text, evaluate arithmetic, choose a solver or generate UDIR. Binding is M06 and normalization is M07.

## Grammar clarification

The original grammar is retained, including its case-sensitive keywords, left-associative arithmetic, unary minus, explicit units, source aliases, temporal windows, dependencies, policies, scenarios and triggers. Two syntax clarifications support existing UDIR domains without a new wire contract:

- `decision flag : Bool;` denotes an implicit Boolean domain. Explicit Boolean bound syntax can be parsed but must be rejected during semantic admission.
- `constraint row : a <= b soft penalty 1.25;` denotes a soft constraint with an explicit exact penalty. The absence of the suffix denotes a hard row. A nonpositive penalty is a semantic error. Neither policy nor parser may silently soften a hard constraint.

```ebnf
decision = "decision", ID, ":", type, [ "in", "[", expr, ",", expr, "]" ], ";" ;
constraint = "constraint", ID, ":", expr, relation, expr, [ "soft", "penalty", NUMBER ], ";" ;
```

The parser requires an interval for non-Boolean decisions. Every identifier token and node has a half-open UTF-16 source span; numbers retain their exact decimal spelling. JSON string escapes are decoded as data, never evaluated. Source identity carries a SHA-256 of the original UTF-8 bytes. The grammar version is explicitly supplied to the entry point (default `dml/0.1`), independent of UDIR wire and semantic versions.

## Stable diagnostics

| Code | Meaning |
|---|---|
| DML-VERSION-001 | Unsupported grammar version |
| DML-LEX-001 | Invalid UTF-8 |
| DML-LEX-002 | Malformed decimal |
| DML-LEX-003 | Unterminated or invalid JSON string |
| DML-LEX-004 | Unexpected character |
| DML-SYN-001 | Expected token, including EOF |
| DML-SYN-002 | Expected relation |
| DML-SYN-003 | Expected unsigned integer objective priority |
| DML-SYN-004 | Expected declaration |
| DML-SYN-005 | Expected scalar type |
| DML-SYN-006 | Expected expression atom |
| DML-LIMIT-001..007 | Byte, token, identifier, numeric, string, unit and expression-depth limit respectively |

Diagnostics are deterministic first errors with a source span and one-based line and UTF-16 column. CRLF counts as one newline. Failure never returns a partially accepted AST; cancellation throws OperationCanceledException. Messages contain no source contents. Studio can display these diagnostics, but must not bind or compile a rejected result.

Limits: 1 MiB original bytes, 65,536 non-EOF tokens, 128 ASCII identifier characters, 256 numeric digits total with at most 255 fractional digits, 4,096 decoded UTF-16 string characters, 256 unit characters, expression recursion and resulting tree height at most 64. A source exceeding a bound is rejected, never truncated. The bounds are syntax limits rather than mathematical feasibility guarantees. No reflection, function calls, member access, interpolated strings, arbitrary expressions in trigger bodies, file access or network access are provided by the grammar. A source schema string is inert data; M06 must validate it against authorized aliases before any later use.

## Scenario and trigger extension policy

Version 0.1 scenarios contain only named assignments; their parent/stage/probability and nonanticipativity semantics require an explicitly reviewed versioned extension before introducing multistage syntax. Triggers are only `on change <source alias> reoptimize;`; they do not execute actions in the parser. Additional trigger kinds or statements, calendar forms, global constraints, capability declarations or uncertainty distributions require a new grammar version with an explicit parser profile, AST types, diagnostics and migration/round-trip cases. Unknown syntax and versions fail closed. An unknown mandatory semantic extension must not be ignored to make parsing or lowering succeed. No executable plugin extension point exists. Existing valid 0.1 syntax keeps its meaning.

## Evidence

```sh
dotnet restore tests/Dml.Syntax/WowDi.Dml.Syntax.Tests.csproj --locked-mode
dotnet test tests/Dml.Syntax -c Release --no-restore
sudo unshare -n sh -c 'ip link set lo up && dotnet test tests/Dml.Syntax -c Release --no-build --no-restore'
dotnet run --project tools/Traceability -c Release
```

TC-M05-A: fixtures in tests/Dml.Syntax/Fixtures include the published Batch grammar example and a workforce model. Tests cover exact token spelling, source hashes and UTF-16 spans, precedence, syntax variants, stable diagnostics, resource limits, cancellation, rejection of executable constructs, and 2,000 deterministic malformed/random inputs parsed twice. Syntax acceptance is not model admission. Unknown symbols, cycles, types, calendars and authorization are M06/M07 gates.
