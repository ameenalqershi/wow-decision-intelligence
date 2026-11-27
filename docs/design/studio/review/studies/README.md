# Reviewed Studio visual design studies

UI01 uses the shell study, UI05 the immutable official reference, and the
other 21 modules their independent dark studies. The reviewed primary
studies are static design specifications, not executed product screenshots.

- wireframes/ : 23 reviewed structural layouts derived from the actual
  module information architecture (original draft wireframes remain).
- states/ : five independent state messages and recovery treatments per
  module, plus the registered action surface for every requirement.
- rtl/ : one Arabic unavailable-state design per module with mirrored
  logical panes and isolated LTR revision ID. Runtime localization and
  keyboard/assistive technology acceptance remain feature requirements.
- state-manifest.json : 115 scoped state fixtures, no mutation enabled
  in rejected/unavailable/loading/empty/failed states.

The final disposition defines the scope of design approval. State
screens are not claims of a provider result; all fixture values are
illustrative. Primary designs use the actual DS01 palette and reference
geometry. Preserve scalable typography and logical CSS at runtime.

Regenerate:
```sh
node docs/design/studio/review/studies/build-studies.mjs
node docs/design/studio/review/studies/build-states.mjs
node docs/design/studio/review/studies/build-rtl.mjs
node docs/design/studio/review/studies/check-layout.mjs /path/to/chromium report.json
node docs/design/studio/review/studies/check-variants.mjs /path/to/chromium variants.json
```
