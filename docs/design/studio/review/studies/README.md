# Reference-aligned proposed module studies

UI01 is in ../UI01/shell-study.svg. UI05 uses the immutable
../../../references/01-WOW-DI-Studio.svg. This directory contains the other
21 module studies. The shared chrome, navigation order, active indicator,
canvas/inspector extents and palette follow the archived reference. Each
study uses a module-specific work area and inspector, with explicitly
unavailable, empty or unvalidated fixture state rather than simulated
solver results. These are static design review artifacts, not functional
screens. Labels on proposed controls describe intended actions; the actual
enabled/disabled policy is defined by the per-module state review.

All 22 proposed studies were rendered in Chromium and checked for
common reference geometry, navigation order, text bounds and overlapping
text bounding boxes. These checks establish structural presentation
properties only. They do not establish visual approval, keyboard behavior,
high DPI rendering or completed feature contracts. The original SVG and
other teams' source specifications have not been edited.

Regenerate and verify from the repository root:

    node docs/design/studio/review/studies/build-studies.mjs
    node docs/design/studio/review/studies/check-layout.mjs /path/to/chromium docs/design/studio/review/studies/layout-evidence.json

Final dispositions require review against merged audit sources, including
specialized application ports and fields. Do not promote proposed studies
to official screenshot baselines without explicit approval.
