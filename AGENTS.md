# AGENTS.md — Cerith

## Mission

Build Cerith according to the canonical repository documentation.

Project memory belongs to the repository, not to private chat history.

## Start every work session

1. Read `.agent/STATE.md`.
2. Read `.agent/HANDOFF.md`.
3. Read `.agent/TASKS.md`.
4. Run `git status --short`.
5. Query Graphify / the configured code graph before broad source reads.
6. Read only task-relevant canonical docs.

Do not reconstruct the whole project from chat history.

## Canonical documentation

```text
product goal       docs/PRODUCT_SPEC.md
architecture       docs/ARCHITECTURE.md
roadmap            docs/ROADMAP.md
metrics            docs/METRICS.md
AI quotas          docs/PROVIDERS_AI_QUOTAS.md
research provenance docs/REFERENCE_PROJECTS.md
UI/UX              docs/UI_UX.md
plugins            docs/PLUGINS.md
testing            docs/TESTING_QA.md
security           docs/SECURITY_PRIVACY.md + SECURITY.md
performance        docs/PERFORMANCE.md
legacy policy      docs/LEGACY_POLICY.md
open source        docs/OPEN_SOURCE_STRATEGY.md
code quality       CODE_QUALITY.md
```

Do not duplicate canonical knowledge into multiple files.

## Context integrity rule

`.agent/STATE.md` and `.agent/HANDOFF.md` describe the current operational state; they do not replace product identity, roadmap or research provenance.

Before making a product, architecture, provider or milestone-shaping decision, read `docs/PRODUCT_SPEC.md`, `docs/ROADMAP.md`, `docs/REFERENCE_PROJECTS.md` and the relevant canonical domain document.

If external research or a conversation materially changes durable product requirements, architectural constraints, provider semantics or reference provenance, distill the durable conclusion into the appropriate canonical document before closing the session. Do not rely on chat-only context.

## Public-ready rule

Act as if every committed file will be visible publicly tomorrow.

Never commit:

- credentials;
- tokens;
- cookies;
- authorization headers;
- private screenshots;
- raw provider payloads containing personal information;
- user email addresses;
- machine-specific personal paths unless sanitized;
- local chat transcripts;
- agent private caches;
- generated context dumps;
- private legacy artifacts.

If a fixture contains real provider data, sanitize it before commit.

## Zero-AI-slop rule

Generated code is not accepted merely because it compiles.

Every change must be intentional, minimal, reviewed and useful.

### Every new file must justify its existence

Before adding a file, be able to answer:

1. What responsibility does this file own?
2. Why can that responsibility not live cleanly in an existing file?
3. Who imports/uses it now?
4. What maintenance cost does it introduce?

If those answers are weak, do not create the file.

### Every new abstraction must justify its existence

Do not add:

- interfaces with only one foreseeable implementation merely “for flexibility”;
- `Manager`, `Helper`, `Factory`, `Coordinator`, `Service`, `Repository` or `ProviderFactory` layers without a concrete need;
- generic `Common`, `Shared`, `Infrastructure`, `Foundation`, `Utils` dumping grounds;
- wrappers that only rename another API;
- speculative plugin/DI architecture before a real second implementation exists.

### Every dependency must justify its existence

Before adding a package:

1. explain what problem it solves;
2. compare with the standard library / Windows API / existing dependencies;
3. assess runtime size and security cost;
4. check license compatibility;
5. record the decision if significant.

Do not add a dependency to save a few straightforward lines.

## Code-quality rules

Do not write:

- comments that merely restate code;
- empty catch blocks;
- unused abstractions;
- dead code left commented out;
- fake placeholders presented as working behavior;
- giant catch-all classes;
- tests that only prove getters/setters;
- duplicated validation logic;
- unexplained magic values;
- broad refactors unrelated to the active task;
- marketing prose in code comments;
- “robust/scalable/enterprise” filler text;
- TODO comments without a tracked task or issue.

Prefer names and structure that explain the code without narration.

## Multi-agent ownership

One write task has one owner at a time.

Before editing:

- claim/confirm ownership in `.agent/TASKS.md`;
- respect scope and allowed paths;
- do not overwrite another agent's active work.

For true parallel writes use separate git worktrees.

Another agent may perform non-mutating review/research without taking ownership.

## Graph-first navigation

Before broad grep/search/read operations:

```text
Graphify/code graph
→ candidate symbols/files
→ selective reads
→ expand only when evidence requires it
```

After meaningful structural changes refresh the graph.

## Change policy

- preserve unrelated user/agent changes;
- make the smallest responsible change;
- do not reopen settled architecture without reading the relevant decision/ADR;
- never invent telemetry;
- keep semantic states distinct;
- measure performance claims;
- keep runtime dependencies minimal.

## AI quota rule

The old WidgetOSD quota implementation is not reusable product code.

All quota providers must follow `docs/PROVIDERS_AI_QUOTAS.md`.

## UI rule

External web UI sources are design references, not runtime architecture.

Do not copy external source code without checking license/provenance.

Production UI remains native unless a future measured decision explicitly changes that.

## Completion rule

Before handoff:

1. run relevant build/tests;
2. inspect final diff;
3. remove dead/debug/generated junk;
4. update task state;
5. append a concise worklog entry;
6. update global state only if necessary;
7. rewrite handoff;
8. refresh Graphify if structure changed;
9. run the repository preflight/security checks if configured.

Stop at a clean task boundary. Do not silently begin unrelated work.

## Context efficiency

Prefer:

```text
STATE + HANDOFF + TASK + graph result + relevant diff + relevant source
```

over:

```text
whole repo + whole history + every document
```
