# WOW Decision Intelligence — UI reference contract

## Instructions to the implementation agent
Implement the supplied visual references faithfully. The SVGs are the source of truth for geometry, colors, labels, proportions and visual hierarchy at their reference viewport sizes. PNGs are previews at 3× resolution. Do not redesign them, change the palette, replace the graph with a generic dashboard or add decorative elements without approval. Use real components and real application state, not a flattened screenshot or hard-coded simulation of the engine. All displayed numbers are example fixtures, not measured results.

## 1. Desktop: WOW Decision Intelligence Studio
- Technology: C# / .NET, Blazor Razor Components, Photino host subject to Windows/Linux proof of concept; custom CSS/design tokens and SVG for graph rendering. Fluent design principles, without default component styling that changes the reference.
- Reference canvas: 720 × 460 logical units. Treat dimensions as reference layout, not fixed window size; scale typography to readable desktop sizes and preserve layout proportions on larger windows.
- Title bar: 31 units, #1B2331. Sidebar: 144 units wide, #151D29. Main background: #10151E. Bottom status bar: 22 units, #1B2331.
- Sidebar order: Overview, Decision Models, Decision Graph, Optimization, Scenarios, Simulation, Integrations, Benchmarks, Evidence, Settings. Active item Decision Graph: #23463F background, #3ED5B3 accent line, #62E4C6 text.
- Header: Production Planning, with workspace breadcrumb underneath.
- KPI row: four equal cards, 130 × 58, radius 5, gap 9; background #1C2735, border #2C3949. Muted label #94A3B8, primary text #E2E8F0, positive text #5CE0B6.
- Main work area: graph 375 × 276 and monitor 167 × 276, gap 10. Graph background #121B27; grid #243040; nodes #20374D / border #4276A0; optimizer node #20594D / border #4CDDB4; edges #477D9E. Monitor #1B2533, progress fill #35C6A2, chart #4ADEB0, evidence button #16785F. Exact positions and curve geometry are in the SVG.
- Graph inputs: Demand, Materials, Machines; central Optimizer; outputs Schedule, Inventory, Costs; lower Constraints node. Support selecting nodes, pan, zoom, keyboard navigation, inspecting actual graph state, and rendering data-driven edges. Preserve the reference fixture for screenshot regression tests.
- Monitor: solver status, progress (only if determinable), convergence, feasibility, solver identity, memory, elapsed time, best bound, and evidence action. If progress or bound unavailable, show an honest indeterminate state. Never invent optimality, savings, memory or timing.
- Implement resize behavior, light/RTL variants separately; the reference is the English dark theme. Do not change the dark default design.

## 2. CLI: WOW DI Terminal
- Technology: C# / .NET, System.CommandLine, Spectre.Console where suitable. The reference's window chrome is illustrative; the real CLI runs inside the user's terminal and must not try to draw OS window controls.
- Command fixture: `wowdi solve production.dml --explain`.
- Header accent #58D6B3; background reference #0D1117; primary #C9D1D9; muted #9CA3AF; border #30363D; success #56D364 / #3FB950; progress #79C0FF and #238636.
- Stage order: load model, validate constraints, build dependency graph, compile IR, select solver, then report actual solving progress and result.
- Results table: Solver, Status, Execution time, Memory usage, Constraints satisfied, Optimality gap, Estimated savings (only if a valid baseline exists). Verification line only after genuine verification.
- Respect terminal width, ANSI capabilities, NO_COLOR, non-interactive mode, cancellation, exit codes and redirection. JSON output must be machine-readable and separate from logs/progress.
- Fixture values are illustrative. The status and independent verification must reflect actual results.

## 3. Acceptance criteria
- Build the reference screen as reusable components, not a static image.
- Run the same application use cases from Desktop and CLI; no duplicated mathematical/business logic.
- Compare screenshots to the supplied SVG/PNG at the reference proportions. Document any differences caused by font metrics, host chrome, DPI, or rendering engines.
- Test Windows and Linux, including startup, resize, graph interaction, cancellation, invalid input, rendering and accessibility. Test the CLI in narrow/wide and color/no-color terminals.
- Deliver implementation files, screenshots from actual execution, test results, and a documented list of visual deviations. Do not mark completion until the reference design and behavior are verified.
