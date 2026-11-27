# Command host and output contract

The executable name is wowdi. Applications assemble commands with System.CommandLine, Microsoft.Extensions.Hosting and dependency injection. Only a selected handler may open a workspace or launch a worker. Spectre.Console provides optional terminal rendering. Help, version and syntax validation never require network access, a solver or an enterprise account.

| Command family | Application port or boundary | Result oracle |
|---|---|---|
| init, workspace create/list/inspect/open/export | API-WORKSPACE | Reopen in a second process and compare frozen revision IDs. |
| model create/validate/compile/inspect/compare/export | API-MODEL | Compare DML and SDK normalization, diagnostics and source spans. |
| solve | API-SOLVE, API-RUN, API-EVIDENCE | Expose actual milestones, real measurements and the verification result. Dry run stops after planning. |
| graph inspect/dependencies/impact/export | API-GRAPH | Match the incidence and cross-revision reference traversal. |
| scenario create/compare, simulate | API-SCENARIO | Keep a frozen baseline and record seeds and sample counts. |
| decision verify/explain, evidence inspect/export | API-EVIDENCE | Report independent verification, preserve unknown bounds. |
| benchmark run/compare/report, solver list/capabilities/profile | Adapter manifest and paired benchmark results | Report unavailable adapters and retain unsuccessful trials. |
| doctor, version, config, logs, health | API-COMMAND | No secrets in either stream. Version must match the installed build. |

CLI01 boundary: `wowdi --help`, `wowdi --version` and `wowdi version`. Resolving the registered version provider twice in one process returns the build's informational version. Unknown commands return a nonzero usage status. Command handlers receive configuration and cancellation via DI. Check user-level config without silently supplying a remote endpoint. Composite solve, profiling and installation belong to the independent command issues.

For structured output, stdout contains exactly one versioned JSON result or valid CSV with escaped cells. Logs, errors and progress use stderr. Non-interactive mode never prompts. Respect NO_COLOR on both streams. Use exit 0 for successful processing, 2 for usage/invalid input, 3 for unsupported capability, 4 for failed verification, 5 for infrastructure failure and 130 for user cancellation. Termination and mathematical status are separate fields. A completed query reporting infeasibility with valid evidence is a successfully processed query.

The dark CLI reference supplies typography, label order and colors. A native terminal controls window chrome. Show indeterminate progress when the engine has no progress measure. Estimated savings require a compatible baseline. CI fixtures label sample data, exercise redirected output separately and pin columns and ANSI capability.
