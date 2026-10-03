# Model Routing

Operational preference only. This may change frequently.

## Historical CER-M3-004 routing

The final ZCode correction on 2026-10-03 used GPT-6.1 Sol Medium. That task-specific route is historical and does not override the current policy below.

## Core rule

Treat **model**, **reasoning effort** and **route** as separate decisions.

`Web` is a route, not a model family.

Choose the reasoning tier from the **technical risk and difficulty of the work**. Then choose the route that best fits the task's context, latency, tool and quota needs.

Task duration, token volume, native code, repeated iterations or a first failed probe are not escalation reasons by themselves.

## Default execution / write owner

Current default:

```text
model  = GPT-6 Luna
effort = Max
route  = native Codex
```

Use GPT-6 Luna Max by default as the normal sole repository write owner for:

- normal implementation after the semantics are understood;
- deterministic tests;
- routine debugging and evidence-driven retry loops;
- bounded runtime validation;
- CI/rebase/fix loops;
- straightforward refactors;
- documentation and repository maintenance;
- well-scoped work, including long-running tasks.

GPT-6 Luna Max can own a task for hours. Duration or token volume alone does not imply stronger reasoning is needed.

Ordinary low-risk subagents may also use GPT-6 Luna Max.

## Strong reasoning work

Use GPT-6.1 Sol when stronger reasoning materially reduces technical risk. This is not merely a quota preference.

Examples include:

- architecture or durable contract decisions;
- concurrency, cancellation, backpressure and lifecycle reasoning;
- IPC and trust-boundary review;
- security/privacy review;
- native ABI/interoperability;
- provider semantics where the upstream contract is subtle;
- difficult debugging with contradictory evidence;
- hostile review of high-risk changes;
- milestone-closing review where a semantic defect could invalidate the result.

Do not downgrade one of these gates to GPT-6 Luna solely to save quota.

## Strong native reasoning: GPT-6.1 Sol Medium

Use:

```text
model  = GPT-6.1 Sol
effort = Medium
route  = native Codex
```

when the work warrants a strong reasoning model and native Codex context/tooling is appropriate, including:

- substantial architecture or contract work;
- high-impact concurrency/security/IPC reasoning;
- native interop/ABI review across meaningful context;
- difficult provider integration or semantic analysis;
- context-heavy hostile review;
- debugging where multiple components/evidence sources must be reconciled;
- a critical gate where the native harness or broader context materially helps.

After the difficult reasoning point is resolved, implementation normally returns to GPT-6 Luna Max.

## Exceptional native escalation: GPT-6.1 Sol High

Use GPT-6.1 Sol High only as an exceptional escalation for a concrete unresolved critical problem where Medium is insufficient, for example:

- subtle ABI/interoperability ambiguity with conflicting evidence;
- high-impact concurrency or security reasoning that remains unresolved;
- contradictory runtime/upstream evidence that Medium cannot reconcile;
- a durable architectural decision with major consequences and unresolved trade-offs;
- conflicting strong reviews that cannot be reconciled from the available evidence.

Escalate the hard question, not the whole mission.

## Astra routing

GPT-6 Astra is not part of current Cerith routing.

## Preferred pattern

There is no single mandatory linear ladder.

Typical execution:

```text
GPT-6 Luna Max native executes
→ GPT-6.1 Sol Medium native when stronger reasoning materially reduces risk
→ GPT-6.1 Sol High native only when a concrete critical issue remains unresolved after Medium
→ GPT-6 Luna Max native implements/fixes/finishes
```

## Ownership

One repository write task has one write owner at a time.

Independent specialists/reviewers are read-only by default and must not silently mutate the implementation they are reviewing.

Antigravity may take independent non-overlapping work or a second-pass review when useful.

## Runtime continuity

Use the calling harness's authoritative runtime metadata when available; do not invent execution receipts.

## Quota and quality rule

Optimize quality per scarce native quota **without weakening high-risk gates**.

- Use GPT-6 Luna Max for execution and ordinary work.
- Use GPT-6.1 Sol Medium native when strong reasoning, context or the native harness materially reduces risk.
- Use GPT-6.1 Sol High only when Medium leaves a concrete critical issue unresolved.

Quota optimization chooses between suitable routes; it does not redefine what level of reasoning the technical risk requires.
