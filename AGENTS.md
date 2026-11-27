# WOW DI — Start here for every contributor

This file is the repository entry point for autonomous development. No previous chat or external prompt is required for the workflow. Missing or conflicting specification content is a blocker for the affected feature, not permission to invent it.

## 1. Read before editing

1. [ENGINEERING-DIRECTIVE.md](ENGINEERING-DIRECTIVE.md): consolidated owner requirements and workstreams.
2. [README.md](README.md) and [CONTRIBUTING.md](CONTRIBUTING.md): setup, claims, ownership and handoff.
3. [Architecture](docs/design/architecture.md), [engine semantics](docs/design/engine.md), [language](docs/design/language.md), [lifecycle](docs/design/lifecycle.md), [production](docs/design/production.md) and the relevant research documents.
4. [Roadmap](planning/roadmap.md), [backlog](planning/backlog.json), [issue map](planning/issue-map.json), current GitHub issues and their comments. Read the current requirement matrix, field dictionary, diagrams and approved UI references if present. Do not implement a feature whose required artifacts are missing or inconsistent.

## 2. Discover eligible work

Fetch origin/main and inspect the current repository state before selecting an issue. Use authenticated GitHub CLI (or equivalent API) and a unique worktree/handle. The issue ledger is authoritative for status; the versioned planning files are required for dependency semantics. Never rely on an old issue count, local stale files or a previous session's list.

- List open issues, their labels, assignees, comments and upstream dependencies; inspect existing PRs and reservations.
- Run the planning validator from the current checkout (see CONTRIBUTING and roadmap for the available version). A passing structural check alone does not establish live issue reconciliation.
- Compare the live issue numbers with the issue map and backlog. A missing mapping, changed dependency list, missing requirement or unapproved shared contract blocks affected work. Do not bypass the claim tool; coordinate reconciliation with the owner of #89 or its successor. Independently ready, correctly mapped research or implementation work may proceed only if it does not overlap that reconciliation.
- Select a leaf issue, not an umbrella epic: all prerequisites closed with accepted evidence, no active label/assignee/claim, no conflicting PR or owned path, complete relevant specifications, executable acceptance criteria.
- Rank by release and explicit priority (P0 first), then impact on the critical path and ability to deliver the complete scope. Prefer core algorithm/model/verification and first vertical slice enablers over unrelated future expansion. Do not hardcode issue numbers as an assignment list.

Useful read-only commands (from repository root):
~~~sh
git fetch origin main
gh issue list --state open --limit 1000 --json number,title,labels,assignees
gh issue view ISSUE --comments
gh pr list --state open --limit 100
git ls-remote --tags origin 'refs/tags/work-claims/*'
git worktree list
~~~
Replace ISSUE with a real candidate. If an API listing reaches its limit, paginate; do not treat a truncated list as complete.

## 3. Claim before writing

Run `bash tools/claim.sh ISSUE unique-handle` as documented in CONTRIBUTING. Only the successful remote reservation owner may start. Confirm status:active, assignee, branch and worktree; document owned paths, contracts and verification plan in an issue comment. If metadata sync fails, use the documented recovery procedure. Never remove another reservation or take over unfinished work silently. If working on a shared contract, land a coordinated contract PR first.

## 4. Implement, verify, hand off

Keep domain, compiler, solver adapter and independent verifier separated. Preserve source and tenant identity. Every Optimal assertion needs declared proof or solver evidence level. An experiment must state baseline, data, budget and acceptance conditions. Recompute evidence when a semantic input changes. DML data must never execute arbitrary code. Do not introduce mandatory paid services, Python production components or dependencies on WOW ERP.

Implement the whole accepted scope against the approved diagrams, fields and contracts. Run relevant tests, including negative cases; record commands, OS, inputs, measured results and unavailable checks. Verify reverse dependencies and update documentation. Open a linked PR; never rewrite main or another contributor's work. Close only after accepted evidence and merge according to repository policy; remove active status and release only your own reservation as specified in CONTRIBUTING. A test not run is not a pass.

At a blocker, record the exact missing prerequisite or tool and work on an independent eligible issue. At session end leave the issue, branch, commit, tests, blockers and next step in its handoff. On continuation inspect comments, reservation, branches and dirty worktrees before doing anything. Continue with the next eligible issue after completion without waiting for a new prompt, within the current execution budget.
