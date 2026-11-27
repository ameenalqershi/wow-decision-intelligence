# DS04 — graph and convergence primitives

Uses the M01 immutable DecisionGraphSnapshot as an input only; no graph engine
or shared contracts are changed. GraphProjection retains tenant/model/revision,
typed entities, relationship identity, owner and the full target incidence.
A hyperedge is rendered as a relationship vertex and incidence lines, not as
fabricated independent pairwise relationships. The generic deterministic
incidence layout is a primitive, not the final reference Studio screen.
Consumers own workspace selection and revision changes; reset selection when
switching graph revision or run. Do not mix tenant/revision data.

DecisionGraphView and ConvergenceView expose controlled selection events.
Native SVG foreignObject buttons and equivalent semantic tables select the same
relationship/event ID. Use DS01 tokens and visualizations.css in the host.
Full graph editor layout, pan/zoom, and large-graph performance qualification
are deferred to the respective UI feature and QA issues.

MetricSeries consumes supplied event IDs, strictly unique nonnegative sequence
numbers and nullable decimal values. The consumer owns authorization, actual
event retrieval and run identity. Missing observations (explicit null or
nonconsecutive sequence) break the polyline; zero is a legitimate observation.
No synthetic events, interpolation, savings, bounds or optimality claims.
The gallery reads a local stored JSON fixture, explicitly illustrative, not
a production execution trace. The native execution event repository is not
yet a DS04 dependency; this primitive cannot attest a real run.

## Verification

    dotnet test tests/Studio/Visualizations/Unit -c Release
    dotnet publish tests/Studio/Visualizations/Gallery -c Release -o artifacts/ds04
    cd artifacts/ds04
    dotnet WowDi.Studio.VisualizationGallery.dll --urls http://127.0.0.1:48264/

From the root in another shell, run:
    node tests/Studio/Visualizations/browser-check.mjs /path/to/chromium http://127.0.0.1:48264/ gallery.png

Observed local: six C# tests, including extreme decimal values, pass.
Three Chromium scenarios verify keyboard selection, AX tree, synchronized
hyperedge incidence, stored event identities, missing point/segment break,
and zero. Evidence is under tests/Studio/Visualizations/evidence.
CI runs the same qualification on Windows/Linux. This does not represent
full native desktop or screen-reader speech qualification.
