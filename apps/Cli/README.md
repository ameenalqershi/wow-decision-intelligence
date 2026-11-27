# WOW DI CLI automation contract

Build with .NET 10. Use `dotnet run --project apps/Cli -- version --format json` from the repository, or `wowdi version --format json` after installation (tracked separately in CLI12).

- `version` defaults to plain text; `--version` remains the standalone build identity flag.
- `--format text|json|csv` selects a single result on stdout. JSON is a CLI presentation envelope (contract `wowdi.cli.result`, version `1.0`, payload containing command and fields). Domain payload schemas remain the responsibility of their owning contracts. CSV uses a header and CRLF records with doubled quotes and quoted delimiters/newlines.
- `--non-interactive` disallows prompting; redirected input or output, quiet and machine formats also disallow prompts. `--no-color` and NO_COLOR disable ANSI styling; machine formats and redirected streams never use it. Current version commands do not prompt or use ANSI.
- `--quiet` suppresses progress and informational diagnostics, not failures. `--verbose` enables additional diagnostics on stderr; the two flags are mutually exclusive.
- Usage errors: 2; unsupported capability: 3; verification failure: 4; infrastructure failure: 5; user cancellation: 130; successful processing: 0. Help and completion emit human/protocol text rather than a result envelope.
- System.CommandLine's `[suggest]` protocol returns registered command/option candidates, e.g. `wowdi '[suggest]' ver`. It is compatible with the optional dotnet-suggest shell integration; install/activate it separately for your shell. It does not require a solver.
- CLI-owned child processes can use OwnedProcess.WaitAsync to cancel and terminate the process tree. The solver runtime owns worker admission, isolation and evidence; the actual optimization command's cancellation must be qualified in CLI05/RT03. No progress or results are invented.
