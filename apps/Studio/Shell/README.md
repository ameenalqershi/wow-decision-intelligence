# UI01 — Studio application shell

Implementation of the UI-owned shell specified by SP02 (#160), not an engine or a
native desktop host. The canonical UI01 reference and final disposition are
under docs/design/studio/review. Uses the existing DesignSystem tokens and
components; the reference SVG remains unchanged.

## Boundaries and decisions

- The registry exposes the 23 approved module identifiers in the ten navigation
  groups. A renderer must explicitly register its available views. An absent
  renderer or workspace provider never supplies invented data.
- The runtime supplies authorized workspace and frozen revision identities;
  the shell does not infer permissions or connect to databases. A rejected
  switch retains the previous context. A successful switch resets tabs to the
  new workspace; saved tabs are restored only for the exact authorized context.
- Version-one local presentation JSON holds tab keys, active key, split share
  (25–75%), inspector visibility/dock and optional logical display placement.
  InspectorDock is presentation state only, not an API or persistence-domain
  field. Atomic replacement, strict member validation and explicit retry protect
  existing bytes. Layout identities have a bounded size for local storage.
- The host adapter (U02/DS03) owns monitor discovery, DPI and placement recovery;
  the shell verifies that a proposed recovery lies within a current work area
  before applying it. The unit fixture checks this boundary, not real displays.
- The gallery's three available view renderers and workspace contexts are
  synthetic fixtures. Production providers are owned by RT01 and individual
  feature issues. The engine-status display is explicitly unavailable.
- Responsive chrome preserves DS01 colors, switches to horizontal navigation
  and stacked panels at small widths, and provides Arabic RTL, English LTR,
  keyboard tab operations, resizing, docking and focus restoration.

## Acceptance evidence

Run `dotnet test tests/Studio/Shell/Unit -c Release` and the DesignSystem
regression suite. The interactive gallery is published and run with
WOWDI_SHELL_LAYOUT pointing to an isolated temporary JSON file. Execute
`node tests/Studio/Shell/browser-check.mjs <chrome> <url> <png>`.
The browser JSON and PNG in tests/Studio/Shell/evidence are actual local
Chromium output, not a mockup. The studio-shell workflow repeats the tests on
Ubuntu and Windows. Native host and physical multi-monitor tests remain
conditional integration gates under #71/#127; runtime conformance under RT01.

The reviewed Unsaved state also supports explicit discard: restore the last
accepted presentation snapshot, re-resolve any former workspace revision
through the authoritative provider, and reject discard if access was revoked.
Do not write any file during discard. Recovery buttons remain visible when
the inspector is hidden. Discard affects layout only, not a feature document.
