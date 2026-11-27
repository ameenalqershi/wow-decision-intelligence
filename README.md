# WOW Decision Intelligence

Programmable decision models, mathematical optimization and independently checked decision evidence in C# and .NET. UADIE is the working name of the research engine. WOW DI runs independently of WOW ERP. No language model is required to solve or verify a decision.

[العربية](README.ar.md) · [Engineering blueprint](docs/design/architecture.md) · [GitHub issues](https://github.com/ameenalqershi/wow-decision-intelligence/issues) · [Join development](CONTRIBUTING.md)

## Run the research studies

With the .NET 10 SDK, from the repository root:

~~~sh
dotnet run --project research/ComponentStudy -c Release
dotnet restore research/ScheduleStudy --locked-mode
dotnet run --project research/ScheduleStudy -c Release --no-restore
dotnet restore research/CoverageStudy --locked-mode
dotnet run --project research/CoverageStudy -c Release --no-restore
node planning/validate.mjs
~~~

The first experiment compares cached component solutions against exhaustive enumeration. The second verifies a finite-capacity production schedule with material receipts and a machine outage using CP-SAT. The third checks a HiGHS resource allocation against an analytic lower bound. Native studies require NuGet access on first restore. These are engineering research executables; commercial availability and general performance are unvalidated.

## Read the design

| Question | Document |
|---|---|
| What is the service boundary and desktop delivery path? | [Architecture and technology decisions](docs/design/architecture.md) |
| Which mathematical claims can the engine make? | [EDG, UDIR, compiler, orchestration, reuse and evidence](docs/design/engine.md) |
| How will a developer describe a decision? | [DML grammar and type rules](docs/design/language.md) |
| What happens before execution? | [State and approval contracts](docs/design/lifecycle.md) |
| What does the first pilot optimize? | [Production model and second-sector fixture](docs/design/production.md) |
| Which products and research predate us? | [Market matrix](docs/research/market.md) · [Methods map](docs/research/methods.md) · [Innovation register](docs/research/innovation.md) |
| What would make the product viable? | [Pilot economics and release gates](docs/research/feasibility.md) |
| Which dependencies have actually run? | [Runtime qualification](docs/research/runtime.md) · [Measured evidence](research/evidence/) |
| Where can I contribute? | [Scope and issue map](planning/roadmap.md) · [Contributor workflow](CONTRIBUTING.md) |

Private development repository. The owner can grant access to named collaborators. Contributor workflow applies equally to humans and development agents. No customer data, connector credentials, proprietary ERP code or external write actions are needed for the sample runs.
