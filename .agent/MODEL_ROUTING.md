# Model Routing

Operational preference only. This may change frequently.

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

Use Luna Max as the normal sole repository write owner for:

- implementation after the semantics are understood;
- deterministic tests;
- routine debugging and evidence-driven retry loops;
- runtime probes and validation;
- CI/rebase/fix loops;
- straightforward refactors;
- documentation and repository maintenance;
- broad-context and long-running bounded execution.

Luna can own a task for hours. Long duration does not imply stronger reasoning is needed.

Ordinary low-risk subagents may also use Luna.

## Strong reasoning work

Historically Cerith deliberately uses Sol for work where stronger reasoning materially reduces technical risk. This is not merely a quota preference.

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

Do not downgrade one of these gates to Luna only to save quota.

## Bounded strong worker: GPT-5.6 Sol Web High

Use:

```text
model  = GPT-5.6 Sol
effort = High
route  = Web
```

when the work needs stronger reasoning than a Luna subagent **and** is short, tightly scoped and fits comfortably in the smaller/slower Web route.

Good examples:

- inspect one risky function or compact diff;
- answer one subtle API/semantic question;
- investigate one bug hypothesis;
- review one bounded concurrency/security concern;
- interpret a small set of upstream sources;
- provide an independent second opinion.

Sol Web High is a selective high-quality worker, not the default route for every review.

Prefer a compact evidence packet:

```text
exact question
+ relevant diff
+ minimum required contracts/source
+ runtime/test evidence
```

Do not force broad or context-heavy work through Web merely to save native quota.

When another agent owns the repository task, Sol Web is read-only by default.

## Strong native reasoning: GPT-6.1 Sol Medium

Use:

```text
model  = GPT-6.1 Sol
effort = Medium
route  = native Codex
```

when the work warrants a strong reasoning model and the Web route is not the right fit, including:

- substantial architecture or contract work;
- high-impact concurrency/security/IPC reasoning;
- native interop/ABI review across meaningful context;
- difficult provider integration or semantic analysis;
- context-heavy hostile review;
- debugging where multiple components/evidence sources must be reconciled;
- a critical gate whose quality bar should not be constrained by Web context/latency;
- cases where the native harness/tool context materially matters.

This tier does **not** require trying Sol Web first. Web and native are alternative routes chosen from task shape; they are not a mandatory ladder.

After the difficult reasoning point is resolved, implementation normally returns to Luna Max.

## Exceptional native escalation: GPT-6.1 Sol High

Use GPT-6.1 Sol High only for a concrete unresolved high-risk question after Medium-level reasoning is insufficient, for example:

- subtle ABI/interoperability ambiguity with conflicting evidence;
- high-impact concurrency or security reasoning that remains unresolved;
- contradictory runtime/upstream evidence that Medium cannot reconcile;
- a durable architectural decision with major consequences and unresolved trade-offs;
- conflicting strong reviews that cannot be reconciled from the available evidence.

Escalate the hard question, not the whole mission.

## Frontier escalation

GPT-6 Astra or another frontier/premium tier is outside the normal Cerith routing.

Use it only after an explicit per-task decision for an exceptional unresolved problem where the lower tiers are demonstrably insufficient.

## Preferred pattern

There is no single mandatory linear ladder.

Typical execution:

```text
Luna Max native executes
→ strong reasoning gate chosen by task shape:
   - GPT-5.6 Sol Web High for short bounded high-quality work, or
   - GPT-6.1 Sol Medium native for substantial/context-heavy strong reasoning
→ GPT-6.1 Sol High native only for a concrete unresolved critical issue
→ Luna Max implements/fixes/finishes
```

## Ownership

One repository write task has one write owner at a time.

Independent specialists/reviewers are read-only by default and must not silently mutate the implementation they are reviewing.

Antigravity may take independent non-overlapping work or a second-pass review when useful.

## Runtime continuity and review ownership

Before editing on each turn, verify the latest authoritative runtime `turn_context` model and effort; prompt or spawn text is not proof, and implementation must stop if the assigned route cannot be verified. CER-M3-004 implementation owners require GPT-6 Luna Max. Resume only when the runtime explicitly preserves Luna Max and the resumed turn verifies it; otherwise continue on the same worktree and branch with a fresh Luna Max worker. MID and FINAL reviews are external, read-only GPT-6.1 Sol Medium gates and never change the implementation owner's runtime. Follow the two-gate cadence and common checklist in `.agent/TASKS.md`.

## Quota and quality rule

Optimize quality per scarce native quota **without weakening high-risk gates**.

- Use Luna Max for execution and ordinary work.
- Use Sol Web High when a bounded problem benefits from stronger reasoning and Web's slower/smaller context is acceptable.
- Use GPT-6.1 Sol Medium native when strong reasoning, larger context or the native harness is materially useful.
- Use GPT-6.1 Sol High only when Medium leaves a critical issue unresolved.

Quota optimization chooses between suitable routes; it does not redefine what level of reasoning the technical risk requires.
