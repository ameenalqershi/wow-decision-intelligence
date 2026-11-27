# DML 0.1 design draft

Three front ends (DML, visual editor, typed C# builder) produce the same source-linked semantic tree. JSON uses a schema-versioned declarative representation. These are language specifications for implementation issues M03–M10. No host-language code, reflection, network access, recursive calls or string interpolation inside expressions. Source and trigger references bind to registered, authorized connector aliases at ingestion.

~~~ebnf
model       = "problem", ID, "{", { decl }, "}" ;
decl        = resource | parameter | decision | constraint | objective | temporal
            | source | trigger | scenario | dependency | policy ;
resource    = "resource", ID, ":", type, "=", expr, ";" ;
parameter   = "param", ID, ":", type, "=", expr, ";" ;
decision    = "decision", ID, ":", type, "in", "[", expr, ",", expr, "]", ";" ;
constraint  = "constraint", ID, ":", expr, relation, expr, ";" ;
objective   = ("minimize" | "maximize"), ID, "priority", NAT, ":", expr, ";" ;
temporal    = "window", ID, "from", STRING, "until", STRING, "calendar", ID, ";" ;
source      = "source", ID, "schema", STRING, ";" ;
trigger     = "on", "change", ID, "reoptimize", ";" ;
scenario    = "scenario", ID, "{", { "set", ID, "=", expr, ";" }, "}" ;
dependency  = "depends", ID, "on", ID, ";" ;
policy      = "policy", ID, "hard", ID, ";" ;
type        = ("Int" | "Rational"), [ "[", ID, { ("/" | "*"), ID }, "]" ] | "Bool" ;
relation    = "<=" | ">=" | "==" ;
expr        = term, { ("+" | "-"), term } ;
term        = unary, { ("*" | "/"), unary } ;
unary       = [ "-" ], primary ;
primary     = NUMBER, [ "[", ID, { ("/" | "*"), ID }, "]" ] | ID
            | "true" | "false" | "(", expr, ")" ;
~~~

ID is an ASCII letter followed by letters, digits or underscores; keywords are reserved. NAT is an unsigned integer; NUMBER is an unsigned integer or finite decimal with digits on both sides of the decimal point. STRING is double-quoted UTF-8 with JSON string escapes. Whitespace separates tokens, punctuation may abut a token. Units/calendars are registry identifiers. Parse exact decimals, enforce size limits, and reject out-of-range exponents and numeric overflow. Closed decision intervals use the variable unit and finite bounds in the pilot. Boolean decisions denote domains {false,true} only. Formal grammar permits syntactic combinations subsequently rejected by semantic checks.

~~~text
problem Batch {
  source orders schema "orders.v1";
  resource stock : Int[kg] = 10[kg];
  param consumption : Int[kg/batch] = 2[kg/batch];
  param margin : Int[USD/batch] = 4[USD/batch];
  decision qty : Int[batch] in [0[batch], 8[batch]];
  constraint available : qty * consumption <= stock;
  maximize profit priority 1 : qty * margin;
  policy material hard available;
  on change orders reoptimize;
  scenario shortage { set stock = 4[kg]; }
}
~~~

Expected optimum: five batches, USD 20. Under shortage: two batches, USD 8. AST: Problem → Source/Resource/Parameter/Variable/Comparison/Objective/Policy/Trigger/Scenario. Each declaration owns a span and each symbol reference owns its resolution. Binding constructs an acyclic derived-expression DAG; operational precedence cycles are diagnosed separately. Objectives are lexicographic in ascending priority with unique priorities in v0. Hard policies can only refer to existing hard constraints. Disallow division by a possibly zero expression and variable products unsupported by the chosen adapter. Missing timezone offset, invalid calendar or nonpositive window is an error. Reject a scenario override of a decision, foreign source alias or parameter of a different unit.

| Fixture | Required behavior |
|---|---|
| Change kg to USD in stock | DML-UNIT-001 reports both source spans and expected quantity |
| Use unknown identifier stok | DML-BIND-001 reports nearest in-scope alternatives |
| Replace margin unit with EUR/batch and add a USD term | DML-FX-001 requires an explicit versioned conversion; v0 import must normalize first |
| Add second goal with priority 1 | DML-OBJ-002 rejects ambiguous priority |
| Multiply two integer decisions for a linear-only backend | DML-CAP-001 records the unsupported operation and source span |
| Visually edit the shortage and export JSON | Same normalized model fingerprint as edited DSL/SDK |
| Submit deeply nested input or executable constructs | Bounded diagnostic. No evaluation or connector invocation during parsing |

JSON Schema validates structure, enums and required properties. A semantic pass still checks symbols, compatible units, unique priorities, references and horizon. Keep extension namespaces distinct; an unknown must-understand property is an error. Suggested API: Build → Validate → Compile(profile) → Run(snapshot,budget) → Verify(originalModel, candidate). Future interval/cumulative constructs, multi-stage uncertainty and time types require versioned grammar and equivalent round-trip fixtures.
