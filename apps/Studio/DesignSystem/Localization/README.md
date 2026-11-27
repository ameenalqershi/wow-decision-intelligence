# DS03 — localization and responsive layout primitives

Use LanguageScope to switch the language and direction without rebuilding
the component tree. Consumer strings must be localized; scope does not
translate arbitrary text or silently change process-wide culture.
IdentityText and LocalizedNumber keep immutable IDs and numeric/unit labels
isolated within Arabic text. Numeric formatting is explicit per language.
ResponsiveSplit uses logical layout order: the primary pane starts on the
left in English, on the right in Arabic, and stacks first on narrow screens.
DOM order remains stable for keyboard access. Use localization.css with
DS01/DS02 styles.

DisplayRecovery is a deterministic pure placement policy using host-provided
logical work areas and scale factors. It preserves valid saved placement,
clamps offscreen bounds, and centers a fallback on an available display.
The host owns physical display enumeration, conversion to a consistent
logical coordinate space, actual window movement and OS DPI events.
This policy alone is not proof of multi-monitor support.

## Evidence

- 5/5 C# tests passed: language mapping, valid placement, invalid topology,
  missing monitor fallback and secondary monitor scale/clamping.
- Actual Chromium tests passed: runtime en/ar switch, logical focus order,
  mixed-direction identity and number labels, and responsive layout with
  emulated DPR 1 and 2. The recorded screenshot is a labeled fixture.
- Windows/Linux browser qualification CI runs the same tests.
- Native monitor transfer with distinct real display scale factors is still
  unverified. Do not close DS03 until that acceptance is observed.
- Full resource translation and application shell persistence remain
  feature-level integration; the gallery is not the final Studio design.

Commands from root:

    dotnet test tests/Studio/Localization/Unit -c Release
    dotnet publish tests/Studio/Localization/Gallery -c Release -o artifacts/ds03
    cd artifacts/ds03
    dotnet WowDi.Studio.LocalizationGallery.dll --urls http://127.0.0.1:48265/

From another shell at the root:

    node tests/Studio/Localization/browser-check.mjs /path/to/chromium http://127.0.0.1:48265/ gallery.png
