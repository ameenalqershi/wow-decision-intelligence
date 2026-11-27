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

Actual local evidence: 15 diagrams, 157 nodes, 179 source-linked edges,
all 7 SP03 requirements, 27 allowed lifecycle transitions, all 23
Studio modules. Actual Chromium geometry checks passed 15/15, with no
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
