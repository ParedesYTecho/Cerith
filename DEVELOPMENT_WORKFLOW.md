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

Model routing is operational and may change.

Treat model family, reasoning effort and execution route as separate dimensions. `Web` is a route, not a separate model family.

Current preference:

```text
default root/write worker and ordinary subagents
→ GPT-6 Luna Max, native Codex

selective higher-quality bounded worker
→ GPT-5.6 Sol, High effort, Web route

extra native reasoning only when genuinely needed
→ GPT-6.1 Sol, Medium effort, native Codex

independent non-overlapping second worker / review
→ Antigravity
```

Luna Max is the normal worker even for long tasks and ordinary subagents.

GPT-5.6 Sol Web High is **not** the default reviewer for every task. It is slower and has less usable context than the preferred native worker path, so use it selectively for short, tightly bounded work where stronger reasoning than Luna is useful: a focused hostile review, one semantic/API question, a bounded architecture/security/concurrency issue, upstream interpretation, a difficult bug hypothesis, or a second opinion. This route is especially useful when it can avoid spending GPT-6.1 Sol native.

Use GPT-6.1 Sol Medium native only when an extra native reasoning tier is justified: context too large for Web, native harness/tool context matters, Web latency is unsuitable, material disagreement remains, or a genuinely difficult ABI/security/concurrency/architecture question is unresolved.

Task duration, token volume, native code or repeated iterations do not justify escalation by themselves. After the difficult reasoning point is settled, implementation returns to Luna unless another concrete reason exists.

When Luna owns the task, specialist/reviewer subagents are read-only by default unless write ownership is explicitly reassigned.

Do not automatically escalate to GPT-6.1 Sol High/xHigh/Max, GPT-6 Astra or another premium tier; require an explicit per-task reason.

Judge routes and models by observed reliability, context fit, latency and quota consumption, not brand loyalty.

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
