# Model routing

Route by capability tier, not by model name. Model names change; the tiers do not.

## Roles

| Tier | Role | Use for |
| --- | --- | --- |
| **Light** | helper | Searching and reading code, extracting context, summarising docs or logs, inventories, mechanical edits with an exact spec. |
| **Standard** | **day-to-day lead** | Runs the roadmap autonomously: implementation, tests, debugging, CI, reviews of ordinary PRs, merging, documentation, keeping `STATE.md` and `TASKS.md` accurate. |
| **Strong** | escalation | A task that turned out hard, a design with lasting consequences inside a milestone, concurrency/IPC/persistence/security reasoning, the independent review of a high-risk change. Escalate early: use it when it will take fewer attempts, not after repeated failures. |
| **Strategic** | planner, scarce | Milestone organisation, cross-cutting architecture, resequencing the roadmap, reviewing the project's foundations. Not for implementation. |

Choose the tier from the difficulty and risk of the task. Duration and token volume are not reasons to escalate.

## Current mapping

Update this table when the available models change. Nothing else in the repository depends on it.

| Tier | Claude Code | Other harnesses |
| --- | --- | --- |
| Light | Haiku | smallest capable model |
| Standard | Sonnet 5.5 | default coding model |
| Strong | Opus 5.5 | strongest reasoning model |
| Strategic | Fable 5.2 | none; leave strategic items pending |

## Strategic work is reserved

Strategic items are marked **Strategic** in `TASKS.md`. If the strategic model has no quota, leave those items pending with their state recorded and carry on with independent work. Do not switch models silently to finish them. Everything else must work with a Standard-tier lead; if a task is too big for it, split the task or escalate that one piece to Strong.

## Subagents

Subagents exist to reduce total cost and protect the lead's context, never just to go faster.

- Delegate when a cheaper tier can do the work and the result comes back small (a search, a summary, a bounded edit).
- Do not delegate when explaining the task costs more than doing it, or when you already hold the context.
- Do not parallelise by default. Sequential work that reuses context is often cheaper.
- A delegated brief names the files, the exact question or change, and what done looks like. Ask for a short answer.
- One writer per task. Reviewers and researchers are read-only.
- Verify a subagent's result before relying on it: read the diff and run it.

## Sessions

- Keep a session while its context is still useful and cheap. Start a new one when the context has grown large or a block of work has merged.
- The checkpoint is the repository: `STATE.md`, `TASKS.md`, `DECISIONS.md` and the merged PRs. Keep them current as work merges.
- Write a specific handoff note only when work or context could be lost: before quota runs out, when changing agent mid-task, or when interrupting unfinished work. Put it in the task's entry in `TASKS.md` (branch name, what is done, what is next) and push the branch. No routine handoffs.
- Never redo merged work.

## Harness

Nothing beyond `git`, `gh` and the .NET SDK is required. No hook, skill, plugin or MCP server is needed to work on Cerith, and none is configured by the repository.

- Worth having: a code-review skill for the independent review of high-risk PRs.
- Not useful here, and they cost context on every session: web/frontend, database, deployment, finance and office plugins; process frameworks that add planning or brainstorming ceremony; a code graph (the codebase is small).
- Output-compression modes are fine for chat; commits, PRs and docs are always written normally.
