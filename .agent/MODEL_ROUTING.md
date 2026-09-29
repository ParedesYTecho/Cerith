# Model Routing

Operational preference only. This may change frequently.

## Core rule

Treat **model**, **reasoning effort** and **route** as separate decisions.

Do not use `Web` as if it were another model name.

Example:

```text
model  = GPT-5.6 Sol
effort = High
route  = Web
```

is distinct from:

```text
model  = GPT-6.1 Sol
effort = Medium
route  = native Codex
```

Route changes the surrounding harness, context surface, tools, quota path and reliability characteristics; it does not by itself define a different model family.

Task duration, token volume or the number of iterations are not reasons by themselves to escalate.

## Default write / execution owner

Current default:

```text
GPT-6 Luna Max
route = native Codex
```

Use Luna Max as the normal sole repository write owner for:

- implementation;
- deterministic tests;
- normal debugging and probe loops;
- runtime validation;
- CI/rebase/fix loops;
- straightforward refactors;
- documentation and repository maintenance;
- long-running bounded tasks.

A task may remain with Luna Max for hours if it is progressing correctly.

Do not escalate merely because the task is slow, native, large, or requires repeated iterations.

## Default specialist / reviewer

Current preferred specialist path:

```text
GPT-5.6 Sol
effort = High
route = Web
```

Use **GPT-5.6 Sol Web High** freely for specific, bounded, read-only specialist work such as:

- hostile code review;
- semantic/API interpretation;
- upstream/source review;
- bounded architecture questions;
- concurrency/IPC review;
- security review;
- native interop reasoning;
- provider-contract review;
- second opinions;
- pre-implementation design gates;
- focused post-implementation review.

Do not ration this route merely to preserve native Codex quota. The purpose of this routing preference is to obtain strong focused reasoning without spending the native worker path unnecessarily.

Keep Web specialist prompts narrow. Prefer a compact evidence packet:

```text
goal
+ exact question
+ relevant diff
+ relevant contracts/files
+ runtime/test evidence
```

rather than whole-repository or whole-history dumps.

Web specialists are **read-only by default** when Luna Max owns the repository task.

The Web route is development tooling, not Cerith runtime architecture, and its availability/usage accounting is not a product guarantee.

## Native extra reasoning tier

Use:

```text
GPT-6.1 Sol
effort = Medium
route = native Codex
```

only when the task genuinely needs an **extra native reasoning gate** beyond the normal Luna + Sol Web High workflow.

Typical reasons:

- Web reviewers disagree on a material issue;
- a durable architectural decision remains unresolved;
- subtle native ABI/interoperability behavior needs a stronger native pass;
- high-impact concurrency or security reasoning remains uncertain;
- runtime evidence contradicts source/API expectations;
- the native harness/context/tool boundary itself matters to the reasoning;
- a critical final review warrants an independent native model pass.

Use it on the specific difficult question or review gate, not automatically on the entire mission.

After the extra reasoning point is resolved, implementation normally returns to Luna Max.

## Higher tiers

Do not automatically escalate to GPT-6.1 Sol High/xHigh/Max, GPT-6 Astra or another premium tier.

Those require an explicit per-task decision because the normal routing is:

```text
Luna Max native
→ GPT-5.6 Sol Web High for focused specialist/review work
→ GPT-6.1 Sol Medium native only when an extra native tier is justified
→ Luna Max implements/fixes
```

If that hierarchy is insufficient, explain the concrete unresolved risk before choosing a higher tier.

## Ownership

One repository write task has one write owner at a time.

A specialist/reviewer must not silently mutate the implementation it is reviewing.

Independent agents may research or review without taking write ownership.

Antigravity may take independent non-overlapping work or a second-pass review when useful.

## Quota and quality rule

Optimize **quality per scarce native quota**, not minimum tokens at any cost.

Prefer:

- Luna Max for throughput and execution;
- Sol Web High for strong bounded thinking/review;
- GPT-6.1 Sol Medium native for genuinely extra reasoning.

Do not downgrade a high-risk review to a weak model merely to save quota.

Do not spend the native extra tier on mechanical work that Luna can perform reliably.
