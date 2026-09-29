# Model Routing

Operational preference only. This may change frequently.

## Default

Use the cheapest/fastest model that reliably solves the bounded task.

Task duration, token volume or the number of iterations are not reasons by themselves to escalate to a stronger reasoning setting.

### Default write/execution owner

Current preference:

```text
GPT-6 Luna Max
```

for:

- bounded implementation;
- tests with explicit acceptance;
- normal debugging and probe loops;
- DTOs/contracts;
- straightforward refactors;
- simple adapters;
- mechanical migrations;
- CI/rebase/fix loops;
- documentation and repository maintenance.

Luna Max may own a long-running task for as long as needed. Prefer finishing the bounded goal over escalating because the task is slow.

### Strong specialist / reviewer

Current default reasoning specialist:

```text
GPT-5.6 Sol Medium
```

Use Sol Medium as a read-only specialist or independent reviewer for:

- semantic/API interpretation;
- bounded architecture questions;
- concurrency/IPC review;
- security review;
- credential-handling review;
- plugin isolation;
- public contracts;
- difficult provider integrations;
- hostile code review;
- resolving ambiguity before implementation.

A normal strong-review gate should start at Sol Medium, not Sol High.

### Escalation to Sol High

Use Sol High only when there is a concrete technical reason that Medium is not enough, such as:

- unresolved architectural or semantic ambiguity with durable consequences;
- subtle native ABI/interoperability behavior;
- concurrency or security reasoning where failure impact is high and the question remains genuinely difficult;
- contradictory upstream/runtime evidence;
- persistent difficult debugging after evidence-driven attempts;
- a durable architectural decision whose trade-offs remain unclear;
- conflicting independent reviews that cannot be reconciled from available evidence.

Do not escalate to High merely because:

- the task has taken hours;
- the context is large;
- the implementation is native;
- the first probe failed;
- CI failed;
- another iteration is needed.

Escalate the specific hard question, not the whole mission. Once Sol High resolves the difficult reasoning point, return implementation/execution to Luna Max unless another concrete reason requires High.

### Ownership and subagents

One repository write task still has one write owner.

Sol Medium/High subagents should be read-only by default when Luna Max owns the task. Independent reviewers must not silently mutate the implementation they are reviewing.

Antigravity can take independent non-overlapping tasks or perform second-pass review.

## Rule

Preferred pattern:

```text
Luna Max executes
→ Sol Medium thinks/reviews when useful
→ Sol High only for justified hard cases
→ Luna Max implements/fixes
```

Premium reasoning should not be spent on mechanical work.

Cheap throughput should not be trusted blindly on high-risk architecture.
