# DS02 shared Studio components

The components reuse DS01 semantic tokens. Reference-derived styling and native
HTML semantics are kept separate from engine/application contracts. The
`StudioAction` wrapper uses the pinned Microsoft Fluent UI Blazor package;
the library's other primitives use native HTML where that preserves the
reference appearance and accessibility semantics.

- StudioField associates labels, hints and error messages with a caller-owned
  input ID. Validation remains the caller's responsibility.
- StudioEditableTable uses a table caption, scoped row and column headers,
  stable row IDs and named editable controls. The consumer handles edit events.
- StudioCard, StudioNavigation and StudioToolbar expose named structural
  regions; the navigation consumer owns routes and active selection.
- StudioDialog uses native modal semantics, Escape, and explicit restoration
  of the opening control. It uses a small local DOM interop module.
- StudioAnnouncements separates polite status and alert channels.
  StudioStatusIndicator includes text, so color is never the only state cue.
- StudioStateView renders distinct loading, empty, error and ready content;
  retry is a consumer-owned action rather than a fabricated success.

Host integration: register `AddFluentUIComponents()`; load the DS01 tokens,
components.css and the Fluent UI static web assets. A real interactive Razor
rendering context is required for event callbacks and dialog interop.

## Qualification

```sh
dotnet test tests/Studio/DesignSystem -c Release
dotnet publish tests/Studio/ComponentGallery -c Release -o artifacts/ds02
cd artifacts/ds02
dotnet WowDi.Studio.ComponentGallery.dll --urls http://127.0.0.1:48263/
# In another shell, from repository root:
node tests/Studio/ComponentGallery/browser-check.mjs /path/to/chromium http://127.0.0.1:48263/ gallery.png
```

The fixture deliberately uses local presentation data, not a fabricated
engine result. Evidence under tests/Studio/ComponentGallery/evidence captures
the actual local Chromium run. The browser script traverses controls using
Tab/Enter/Escape and verifies accessible names through the browser AX tree,
focus restoration, actual Fluent UI activation, row edits, separate state
trees/live regions, and theme change. Native OS screen-reader speech and
full application/desktop integration have not been qualified by this test.
Those require actual assistive technology and later native integration tests.
The dedicated CI workflow exercises the published fixture on both OSes.
