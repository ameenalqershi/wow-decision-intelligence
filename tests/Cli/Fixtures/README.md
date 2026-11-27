# CLI02 observed terminal captures

Linux, .NET 10, Spectre.Console 0.54.0, xterm-256color. The doctor command is an actual local operation, not the sample solve in the design reference.

- doctor-tty-36.txt / doctor-tty-100.txt: actual PTY, NO_COLOR=1, width set with stty, CRLF normalized to LF; only the .NET Unix terminal's initial keypad-mode control sequence was removed. No ANSI styling remains.
- doctor-redirected.txt: real redirected stdout, no ANSI/control sequences except newline.
- Colored PTY output was inspected; the actual progress and success RGB codes match the reference. The host terminal owns background and chrome.
- RUNNING configuration: before CreateHost; OK configuration: after configuration loaded. RUNNING workspace: before directory enumeration; OK workspace: after successful MoveNext. The progress lines indicate unknown totals; there is no mathematical solver work.
- Reproduction from repo root after Release build: `TERM=xterm-256color NO_COLOR=1 script -qec "stty cols 36 rows 40; dotnet apps/Cli/bin/Release/net10.0/wowdi.dll doctor --workspace /tmp" /dev/null` (repeat at 100 columns). For redirected output run the same dotnet command with stdout redirected.
- Windows/Linux automated tests check width, safe markup, measured/indeterminate progress, machine output and capabilities; Windows actual PTY evidence remains for QA03. Solve-screen visual qualification is downstream CLI05/QA03.
- A JSON invocation in a PTY with stdout and stderr redirected separately parsed successfully with jq. The .NET Unix runtime emits a keypad initialization sequence directly to the controlling terminal, not to the redirected result stream. An unredirected terminal transcript therefore includes that runtime sequence; the machine-output contract applies to the captured stdout stream.
- Snapshot normalization also strips trailing spaces inserted by wrapping; no visible characters are changed.
