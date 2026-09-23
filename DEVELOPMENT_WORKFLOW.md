# Development Workflow — Codex + Antigravity + Future Agents

This document describes development infrastructure, not Cerith runtime architecture.

## Principle

> Models are replaceable workers. Project memory belongs to the repository.

## Shared context model

```text
CODE + TESTS + CANONICAL DOCS
            ↓
   GRAPHIFY / CODE GRAPH
            ↓
STATE + TASKS + DECISIONS + WORKLOG
            ↓
          HANDOFF
            ↓
 Codex / Antigravity / future agent
```

Git explains code history.

The handoff system explains current intent, failed approaches, verification and next action.

## Start protocol

```text
AGENTS.md
→ .agent/STATE.md
→ .agent/HANDOFF.md
→ .agent/TASKS.md
→ git status --short
→ Graphify query
→ relevant diff
→ relevant source
```

Do not begin by reading the entire repository.

## End protocol

```text
build/tests
→ diff review
→ code-quality cleanup
→ task update
→ worklog
→ state update if global
→ handoff
→ graph refresh if structural
```

## Ownership

One write task has one owner at a time.

For true parallel writing use separate Git worktrees/branches.

No two agents should simultaneously mutate the same files.

## Model routing

Model routing is operational and may change.

Current default idea:

```text
high-throughput bounded implementation
→ GPT-6 Luna Max or equivalent

critical architecture / concurrency / security / review
→ strongest available reasoning model

independent second worker / review
→ Antigravity
```

Judge models by observed reliability and quota consumption, not brand loyalty.

## Graphify

Graphify/code graph is development-only infrastructure.

Use it for:

- callers/callees;
- symbol relationships;
- impact analysis;
- ranked relevant files.

Do not commit private graph caches unless a future explicit decision says otherwise.

## Token efficiency

- discover before reading;
- handoff before history;
- one canonical doc per fact;
- narrow tasks;
- explicit acceptance criteria;
- no repeated project re-explanations;
- compact worklogs;
- preserve durable decisions.

## Worktrees

For parallel tasks:

```text
worktrees/
  CER-101-codex/
  CER-205-antigravity/
```

Integrate through reviewable commits/PRs.
