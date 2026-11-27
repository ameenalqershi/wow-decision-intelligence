# Reproduction and scope

Edit diagrams.tsv (nodes, relationships, requirement IDs, scope keys and exact
source anchors). Run the following from repository root with .NET 10,
Graphviz and Chromium installed. Generated DOT files are editable graph
views; the sequence SVG uses a fixed-column sequence renderer from the
same TSV. Graphviz 2.43.0 and DejaVu Sans were used for committed SVGs.
Different Graphviz/font versions may change geometry, not relationships.

```sh
dotnet run --project tools/ArchitectureDiagrams -c Release -- "$PWD" "$PWD/docs/design/diagrams/sp03"
node tests/ArchitectureDiagrams/browser-check.mjs /path/to/chromium docs/design/diagrams/sp03 /tmp/diagram-browser.json
dotnet run --project tools/Traceability -c Release
```

Actual local evidence: 16 diagrams, 172 nodes, 198 source-linked edges,
all 7 SP03 requirements, 27 allowed lifecycle transitions, all 23
Studio modules. Actual Chromium geometry checks passed 16/16, with no
text overlaps or out-of-viewBox text. Manual review identified
misaligned sequence lifelines and oversized navigation/CLI diagrams;
fixed-column sequence and left-to-right navigation layouts replace
those initial renders. These are architecture diagrams, not the
official Studio screenshot baseline or deployment evidence.

All links are specification/issue links. The UDIR classes are actual
merged C# records; the factory examples are observed synthetic results.
The other diagrams are logical design views, not claims of runtime
availability. The source anchors verify the presence of supporting
text; the semantic relationship was reviewed against that text, not
inferred by the generator. The entire catalog is version-controlled.

The updated catalog adds the actual merged compiler frontend interfaces
(M05/M06/M07/M08/M09/M10/M12/S04), corrects UDIR collection cardinality
and includes TimeBasis. SafePresolve and ExactLowerer consume normalized
models independently; the existing lowering interface does not consume
PresolvedModel. DML operational features rejected by DML-CAP-001 are not
silently removed. A compiled model is not a verified solution.

Acceptance: TC-SP03-A is covered by the checked editable TSV and DOT,
all 198 edge anchors and issue links, rendered edge IDs, 27 lifecycle
transitions, 23 Studio views, 16 actual Chromium geometry checks and
bilingual explanations. TC-SP03-B is covered by explicit target-design
labels, source-linked managed interfaces and observed synthetic evidence
only. This is specification acceptance, not production functionality.
