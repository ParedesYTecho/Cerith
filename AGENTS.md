# AGENTS.md — Cerith

Cerith is a native Windows telemetry suite (hardware, gaming, AI quotas/activity) built on one
`MetricId` contract: providers → `MetricSnapshot` → `MetricBus` → Named Pipe → native WinUI surfaces.

These instructions apply to every agent and model. Project memory lives in this repository, never in chat history.

## Start of session

1. Read `.agent/STATE.md` and `.agent/TASKS.md` (both are short by design).
2. `git status --short` and `git fetch`.
3. Read only the canonical docs your task touches. For UI work that is the "Implemented native surface" section of `docs/UI_UX.md`.

Do not read the whole repository or old history to "get context". Git log and merged PRs are the history.

## Canonical docs (one fact, one place)

```text
product goal         docs/PRODUCT_SPEC.md
roadmap              docs/ROADMAP.md
architecture         docs/ARCHITECTURE.md
decisions (ADR log)  .agent/DECISIONS.md
UI/UX                docs/UI_UX.md
metrics contract     docs/METRICS.md
AI telemetry/quotas  docs/AI_TELEMETRY.md, docs/PROVIDERS_AI_QUOTAS.md
testing              docs/TESTING_QA.md
performance          docs/PERFORMANCE.md
security/privacy     SECURITY.md, docs/SECURITY_PRIVACY.md
references           docs/REFERENCE_PROJECTS.md
code quality         CODE_QUALITY.md
model routing        .agent/MODEL_ROUTING.md
```

Before a product, architecture or provider decision, read the spec, roadmap and the relevant domain doc.
Earlier decisions are context, not dogma: understand the reason, then change them openly in `DECISIONS.md` if you have a better one.

## Build and test

```powershell
dotnet build Cerith.sln -c Release
dotnet test Cerith.sln -c Release --no-build
```

Both must pass with zero warnings and zero failures before any push. Judge by exit code and counts: tool output follows the machine's language. CI runs the same on every PR.

## Workflow

- One task = one branch from `main` = one PR. Branch names: `feat/…`, `fix/…`, `docs/…`, `chore/…`.
- One writer per task. Parallel writers need separate git worktrees and disjoint files. Reviewers are read-only.
- Keep PRs small and based on `main`. Do not stack draft PRs on other draft PRs.
- Squash-merge when CI is green and the definition of done is met. Delete the branch and any worktree afterwards.
- `main` is protected by the **Protect Main** ruleset: changes arrive only by pull request, squash only, linear history, branch up to date with `main`, review threads resolved, and the `build` and `hygiene` checks green. Do not add other protection or try to bypass it.
- Never force-push or rewrite shared history.
- When a task offers several options, take the first, ship it as one small PR and continue. Ask the owner only for destructive or irreversible actions, spending, secrets, or a product decision that needs their judgement.

## Definition of done

Verification is proportional to risk. It is not a ceremony.

- **Always:** Release build clean, all tests pass, you re-read the final diff, CI green on the PR head.
- **Logic change:** a test that fails without the change.
- **UI change:** run the app, look at it (light and dark, default and minimum size), attach a screenshot to the PR. Avoid stealing focus from the user; prefer a secondary monitor when one exists.
- **High-risk change** (concurrency, IPC, persistence, security, provider semantics): one independent review by a different session or stronger model. One good review is enough.
- **Performance claim:** measure it and record the number in `docs/PERFORMANCE.md`.

If a check cannot be run (for example, hardware not present), state the limit in the PR and in the relevant doc and move on. An unverifiable gate must not keep finished work unmerged.

Evidence (test counts, run IDs, hashes, measurements of one run) belongs in the PR description. Docs hold only durable facts.

## End of session

1. Update `.agent/TASKS.md`: remove finished tasks, keep open ones accurate.
2. Rewrite `.agent/STATE.md` if the current picture changed. Replace text; do not append checkpoints.
3. Record durable decisions in `.agent/DECISIONS.md` and durable knowledge in the matching canonical doc.
4. Leave no uncommitted work, stray worktrees or orphan branches.

`STATE.md` stays under 60 lines and `TASKS.md` lists open work only. Write them in plain sentences that a new agent can act on.

## Engineering rules

- Smallest responsible change. No unrelated refactors.
- A new file, abstraction or dependency must have a concrete present need and a real consumer. No `Manager`/`Helper`/`Factory`/`Utils` layers, no interface with one implementation, no speculative plugin or DI architecture.
- No comments that restate code, empty catch blocks, dead or commented-out code, placeholder behaviour, TODOs without a task, or marketing prose.
- Tests must be able to fail for a real reason. No getter/setter tests.
- Dependencies: explain the problem, compare with the standard library and Windows APIs, check licence and size, and record significant ones in `DECISIONS.md`.

## Product rules

- **Truthful telemetry.** Never invent, infer or smooth a value the source did not report. Keep states distinct (Fresh, Stale, Unavailable, Error, Stopped, Unlimited) and never coerce a missing value to zero.
- **Native UI.** Production UI is native WinUI. Web sources are design references only; do not copy external code without checking licence and provenance.
- **One metric contract.** Every surface binds by `MetricId`. No provider-specific drawing logic.
- **AI quotas** follow `docs/PROVIDERS_AI_QUOTAS.md`. The old WidgetOSD quota code is not reusable.
- **Low overhead.** Idle CPU and memory are product features; measure before claiming.

## Public repository

Every committed file is public. Never commit credentials, tokens, cookies, auth headers, private screenshots, raw provider payloads, email addresses, personal machine paths, chat transcripts or agent caches. Sanitize fixtures built from real provider data.

## Tooling

- Code search: use your harness's search tools. A code graph (Graphify, `graphify-out/`, git-ignored) is optional and useful only for cross-project impact questions; the codebase is small enough that it is never required.
- GitHub: use the `gh` CLI.
- Agent-specific config (`.claude/`, `.codex/`, …) is git-ignored and must not be required to work on the project.
