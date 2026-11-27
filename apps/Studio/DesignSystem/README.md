# Studio shared visual system — DS01

A host-neutral .NET 10 Razor component library. No solver, API, persistence,
CLI, JavaScript runtime or desktop-host dependency is introduced. DS02 owns
the Fluent UI component integration and accessible interactive controls;
U02 owns Photino and installation qualification.

## Consumer contract

Reference this project, include
`_content/WowDi.Studio.DesignSystem/tokens.css` in the host document, and wrap
the Studio presentation in `<ThemeScope Theme="..." Density="...">`.
Theme defaults to Dark and density to Comfortable. Invalid enum values
are rejected rather than silently falling back. Tokens are scoped to the
theme subtree, including nested scopes. Keep the stylesheet loaded once.

| Role | Central tokens |
|---|---|
| Surfaces | bg, sidebar, chrome, card, monitor, graph-bg |
| Content | text, muted, nav-text, monitor-text, success, warning, error |
| Selection | active-bg, active-text, active-marker, active-icon, focus |
| Graph | node-bg, node-border, optimizer-bg, optimizer-border, edge, graph-grid |
| Chart | chart, axis, progress, progress-track |
| Control | action-bg, action-text, control-border, hit-target |
| Geometry | reference-width/height, title-height, sidebar-width, status-height, kpi-width/height/gap, graph-width, monitor-width, panel-height/gap, node-width/height |
| Typography/density | font-family, font-caption/body/heading/metric, weight-regular/bold, space-1 through space-4, density-gap |
| Icons/borders | icon-size, icon-stroke, radius-control/card, border-width, focus-width/offset, shadow |

All token names have a `--wow-` prefix. Use logical CSS properties to support
RTL. Reference dimensions are not fixed window dimensions; consume as
proportions or layout hints. Reference text sizes remain mapped separately
from readable rem-based runtime sizes. The reference's flat surfaces have
no drop shadow. Icons inherit semantic foreground, with a decorative
`aria-hidden` icon or a meaningful accessible label supplied by the
consumer. Do not put icon-only controls below the minimum hit target.

The original English dark SVG is not changed. Light and high contrast are
derived palettes; they are not official additional reference images.
The SVG's decorative node border and chart axis have insufficient contrast
if they are the sole interactive cue. Preserve them for noninteractive
reference geometry; use `--wow-interactive-node-border`,
`--wow-interactive-axis` and the focus token for required visual information
and interactive states. The default reference palette and geometry remain
available unchanged. Colors never convey mathematical status on their own.

CLI02 can share these semantic role names, but must preserve its separate
terminal reference palette and terminal capability handling; it need not
take a dependency on the Razor assembly. DS04 owns data-driven graph/chart
geometry and accessibility equivalents. UI01 owns actual shell layouts.

## Verification

Run from repository root:
```sh
export WOWDI_GALLERY_OUTPUT=/tmp/wowdi-ds01
dotnet restore tests/Studio/DesignSystem --locked-mode
dotnet test tests/Studio/DesignSystem -c Release --no-restore
node tests/Studio/DesignSystem/browser-check.mjs /path/to/chromium "$WOWDI_GALLERY_OUTPUT/gallery.html" "$WOWDI_GALLERY_OUTPUT/gallery.png"
dotnet run --project tools/Traceability -c Release
```

The gallery renders the same labeled presentation fixtures in every theme
using the actual Razor theme component, not engine results or a flattened
image. The C# suite verifies the SVG palette and geometry, color contrast
(text >= 4.5:1 and necessary outlines >= 3:1), enum behavior and the CSS
style audit including negative fixtures. Chromium verifies computed
styles, six real Tab focus transitions and captures a PNG. The audit
targets CSS declarations in Studio (not arbitrary inline Razor or
third-party generated styles); future components must avoid inline
visual literals and extend the audit when introducing new CSS syntax.

The full Studio screenshot comparison, runtime accessibility and native
desktop performance belong to later UI/QA issues, not this token library.
The CI workflow executes the same test suite and browser checks on both
OSes, with screenshots and computed-style results as artifacts.
