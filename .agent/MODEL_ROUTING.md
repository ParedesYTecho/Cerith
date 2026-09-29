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

Route changes the surrounding harness, context surface, latency, tools and quota path; it does not by itself define a different model family.

Task duration, token volume or the number of iterations are not reasons by themselves to escalate.

## Default: Luna Max native

Current default for both the root worker and ordinary subagents:

```text
GPT-6 Luna Max
route = native Codex
```

Use Luna Max by default for:

- repository write ownership;
- implementation;
- deterministic tests;
- normal debugging and probe loops;
- runtime validation;
- CI/rebase/fix loops;
- straightforward refactors;
- documentation and repository maintenance;
- broad-context work;
- long-running bounded tasks;
- ordinary research/review subagents where Luna is sufficient.

A task may remain with Luna Max for hours if it is progressing correctly.

Do not escalate merely because the task is slow, native, large, or requires repeated iterations.

## Higher-quality bounded worker: GPT-5.6 Sol Web High

Use:

```text
GPT-5.6 Sol
effort = High
route = Web
```

as a **selective higher-quality worker/subagent**, not as the default reviewer for every task.

Choose Sol Web High when:

- the task is short or tightly bounded;
- stronger reasoning than a Luna subagent is useful;
- the required context fits comfortably in the smaller Web context surface;
- slower turnaround is acceptable;
- using native GPT-6.1 Sol would be unnecessary.

Good uses include:

- a focused hostile review of a risky diff;
- one semantic/API question;
- a bounded architecture trade-off;
- a specific concurrency/security question;
- upstream/source interpretation;
- one difficult bug hypothesis;
- an independent second opinion.

Do not route broad, long-running or context-heavy work to Sol Web High merely because it is a stronger reasoner. Its route is slower and has less usable context than the preferred native worker path.

Keep Web prompts narrow and evidence-driven:

```text
exact question
+ relevant diff
+ minimum contracts/files
+ runtime/test evidence
```

When Luna owns the repository task, Sol Web subagents are read-only by default unless ownership is explicitly reassigned under the one-writer rule.

The Web route is development tooling, not Cerith runtime architecture, and its availability or usage accounting is not a product guarantee.

## Extra native reasoning tier: GPT-6.1 Sol Medium

Use:

```text
GPT-6.1 Sol
effort = Medium
route = native Codex
```

only when the work genuinely needs the **extra native reasoning level**.

Typical reasons:

- the problem is too context-heavy for the Web route;
- latency/interaction characteristics make Web impractical;
- the native harness/tool context materially matters;
- Luna plus a bounded Sol Web pass did not resolve a material issue;
- reviewers disagree on a high-impact question;
- a durable architectural decision remains unresolved;
- subtle native ABI/interoperability behavior remains uncertain;
- high-impact concurrency/security reasoning is still unclear;
- runtime evidence contradicts source/API expectations.

Use 6.1 Sol Medium on the specific difficult question or gate, not automatically on the whole mission.

After that reasoning point is resolved, implementation normally returns to Luna Max.

## Higher tiers

Do not automatically escalate to GPT-6.1 Sol High/xHigh/Max, GPT-6 Astra or another premium tier.

Those require an explicit per-task reason.

Current preferred pattern:

```text
Luna Max native
→ stay on Luna for normal work and normal subagents
→ use GPT-5.6 Sol Web High selectively for short bounded work needing more reasoning
→ use GPT-6.1 Sol Medium native only when a genuinely extra native tier is needed
→ Luna Max implements/fixes
```

## Ownership

One repository write task has one write owner at a time.

A specialist/reviewer must not silently mutate the implementation it is reviewing.

Independent agents may research or review without taking write ownership.

Antigravity may take independent non-overlapping work or a second-pass review when useful.

## Quota and quality rule

Optimize **quality per scarce native quota**, not minimum tokens at any cost.

Prefer:

- Luna Max for default execution and ordinary subagents;
- Sol Web High when a short bounded task deserves more reasoning than Luna but does not justify GPT-6.1 Sol native;
- GPT-6.1 Sol Medium native for genuinely extra reasoning or when Web is the wrong route because of context, latency or harness needs.

Do not spend GPT-6.1 Sol native on a focused question that Sol Web High can answer well.

Do not force Sol Web High onto broad/context-heavy work where Luna is the better operational worker.
