# Model routing

Route by capability tier, not by model name. Model names change; the tiers do not.

## Tiers

| Tier | Use for | Do not use for |
| --- | --- | --- |
| **Light** | Searching and reading code, extracting context, summarising docs or logs, inventories, mechanical edits with an exact spec. | Design decisions, anything where a wrong answer is costly. |
| **Standard** | Implementation with understood semantics, tests, debugging, CI fixes, refactors, analysis, ordinary reviews, documentation. | Open-ended architecture. |
| **Strong** | Complex architecture and durable contracts, concurrency/IPC/persistence/security reasoning, important visual design, especially demanding reviews. Use sparingly. | Routine work. |
| **Lead** | The main session: technical direction, coordination, strategy, final judgement, and any work that is cheaper to do in place than to explain. | — |

Choose the tier from the difficulty and risk of the task. Duration, token volume or one failed attempt are not reasons to escalate.

## Current mapping

Update this table when the available models change. Nothing else in the repository depends on it.

| Tier | Claude Code | Other harnesses |
| --- | --- | --- |
| Light | Haiku | smallest capable model |
| Standard | Sonnet 5.5 | default coding model |
| Strong | Opus 5.5 | strongest reasoning model |
| Lead | Fable 5.2 while available, otherwise Opus 5.5 or Sonnet 5.5 | strongest available |

The workflow must work with a Standard-tier lead. If no stronger model exists, split the task smaller instead of waiting.

## Subagents

Subagents exist to reduce total cost and protect the lead's context, never just to go faster.

- Delegate when a cheaper tier can do the work and the result comes back small (a search, a summary, a bounded edit).
- Do not delegate when explaining the task costs more than doing it, or when you already hold the context.
- Do not parallelise if it raises total consumption. Sequential work that reuses context is often cheaper.
- A delegated brief names the files, the exact question or change, and what done looks like. Ask for a short answer.
- One writer per task. Reviewers and researchers are read-only.
- Verify a subagent's result before relying on it.
