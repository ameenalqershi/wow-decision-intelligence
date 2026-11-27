# Local contract regression checkpoint

Reviewed merged source cf664c1; commands executed from isolated
work/160-studio-spec-review worktree using .NET 10 SDK on Linux.
The subsequent eee2e1c merge changes CLI presentation only.
The test process exited zero. No Windows run of this combined
checkpoint is claimed; #159 has independent accepted Windows evidence.

| Executed command (Release, nologo, quiet) | Passed | Failed | Skipped |
|---|---:|---:|---:|
| dotnet test tests/Contracts | 46 | 0 | 0 |
| dotnet test tests/Udir | 42 | 0 | 0 |
| dotnet test tests/Solver.Contracts | 21 | 0 | 0 |
| dotnet test tests/DecisionGraph | 22 | 0 | 0 |
| dotnet test tests/ModelRegistry | 18 | 0 | 0 |
| dotnet test tests/Integration.Connectors | 18 | 0 | 0 |
| dotnet test tests/Enterprise.Identity | 7 | 0 | 0 |
| Total | 174 | 0 | 0 |

Traceability: 184 issues, 634 requirements, 387 direct dependencies,
94 sub-issue relationships; 4 Fully Covered, 498 Partially Covered,
42 Research Hypothesis and 90 Deferred with Justification.

The contract regressions establish that the authoritative domain seams
used by the review are currently valid. They do not establish that
the unimplemented feature adapters, Studio UI controls or their
end-to-end acceptance cases pass. The proposed static designs were
separately rendered and checked in Chromium.
