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

For product, architecture, provider or milestone-shaping work, also read `docs/PRODUCT_SPEC.md`, `docs/ROADMAP.md`, `docs/REFERENCE_PROJECTS.md` and the relevant canonical domain document before deciding.

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
→ context-integrity check
→ graph refresh if structural
```

The context-integrity check asks whether the session produced durable knowledge that a fresh session could not reconstruct from the repository. If yes, record the conclusion and provenance in the appropriate canonical document; never commit private chat transcripts or generated context dumps.

## Ownership

One write task has one owner at a time.

For true parallel writing use separate Git worktrees/branches.

No two agents should simultaneously mutate the same files.

## Ciclo de sesión

- Una sesión de escritura suele acompañar una task desde su claim hasta el merge y la reconciliación post-merge.
- Mantén la sesión durante implementación, debugging, correcciones de review y cambios sobre el mismo working tree.
- Tras mergear y reconciliar la memoria del repo, prefiere una sesión nueva para la siguiente task. La continuidad local o el debugging pueden justificar reutilizarla; no abras chats nuevos mecánicamente.
- Para reviews realmente independientes, prefiere otra sesión o modelo.
- Una sesión nueva reconstruye el contexto desde `AGENTS.md` → `STATE.md` → `HANDOFF.md` → `TASKS.md` → Git/Graphify, no desde el historial del chat anterior.

## Model routing

Model routing is operational and may change. The canonical policy is `.agent/MODEL_ROUTING.md`.

Keep **model**, **reasoning effort** and **route** separate. `Web` is a route, not a model family.

Current working policy:

```text
default execution / sole write owner
→ GPT-6 Luna Max, native Codex

short bounded work needing stronger reasoning than Luna
→ GPT-5.6 Sol High, Web route

substantial/context-heavy strong reasoning gate
→ GPT-6.1 Sol Medium, native Codex

concrete unresolved critical question
→ GPT-6.1 Sol High, native Codex
```

Luna remains the normal executor even for long-running work.

Sol is selected because the **reasoning risk** justifies it: architecture/contracts, concurrency/lifecycle, IPC/security, native ABI/interop, subtle provider semantics, difficult contradictory debugging or hostile review of high-risk changes. Do not weaken those gates merely to save quota.

The Web and native Sol routes are alternatives chosen from task shape, not a mandatory escalation chain. Sol Web High is useful for short, tightly bounded, low-context work where slower turnaround is acceptable. GPT-6.1 Sol Medium native is appropriate when stronger reasoning also needs substantial context, native tools/harness or a critical review surface.

Task duration, token volume, native code, retry count or a first failed probe are not escalation reasons by themselves. Once a difficult reasoning point is settled, implementation normally returns to Luna.

Independent reviewer/specialist agents are read-only by default when Luna owns the repository task.

Root model/effort/route are launcher choices. Do not assume that naming a different root model inside a task prompt changes the already selected root session model.

Judge routing by observed reliability, technical risk, context fit, latency and quota consumption.

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
