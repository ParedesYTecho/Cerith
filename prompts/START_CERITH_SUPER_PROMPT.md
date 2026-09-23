# Cerith — Super Prompt: Start the Project from Zero

You are the first coding agent working on a new project called **Cerith**.

Cerith is a working name. Do not perform brand/trademark work unless explicitly tasked.

This is a clean project.

The previous WidgetOSD project is NOT the codebase to continue.

Do not refactor WidgetOSD.
Do not copy its architecture.
Do not import it wholesale.
Do not reuse its AI quota implementation.

The old quota logic is specifically considered unreliable.

Everything required to understand the product and current workflow is inside this repository.

---

# 1. Your first actions

Before writing code, read in this exact order:

1. `README.md`
2. `docs/PRODUCT_SPEC.md`
3. `docs/ARCHITECTURE.md`
4. `docs/ROADMAP.md`
5. `docs/METRICS.md`
6. `docs/PROVIDERS_AI_QUOTAS.md`
7. `docs/UI_UX.md`
8. `docs/PERFORMANCE.md`
9. `docs/SECURITY_PRIVACY.md`
10. `CODE_QUALITY.md`
11. `SECURITY.md`
12. `AGENTS.md`
13. `DEVELOPMENT_WORKFLOW.md`
14. `.agent/STATE.md`
15. `.agent/HANDOFF.md`
16. `.agent/TASKS.md`
17. `.agent/DECISIONS.md`

Then execute:

```text
git status --short
```

Inspect the actual repository state.

Do not assume files/projects exist merely because documentation describes the target state.

If Graphify / the shared code graph is already configured, query it before broad code reads.

If this is the first bootstrap and no source graph exists yet, continue without inventing graph output.

---

# 2. Product mission

Cerith is a native, local-first Windows telemetry platform.

It will eventually combine:

- CPU/GPU/RAM/VRAM telemetry;
- temperatures, clocks, power and fans;
- FPS, frame time, 1% lows and gaming telemetry;
- real AI quota/usage monitoring;
- historical telemetry;
- rules and alerts;
- contextual profiles;
- desktop widgets;
- taskbar widgets;
- game overlays;
- dashboards;
- a highly customizable visual editor;
- a future plugin ecosystem.

The product philosophy is:

> **Any metric. Any design. Anywhere on your desktop.**

Cerith is not a clone of MSI Afterburner, RTSS, HWiNFO, Rainmeter, CodexBar, TokenTracker or OpenUsage.

It may learn from them.

Its architecture must remain its own.

---

# 3. Fundamental architecture

Cerith revolves around:

```text
providers
   ↓
universal MetricSnapshot
   ↓
MetricBus / SnapshotStore
   ↓
widgets / history / rules / profiles / diagnostics
```

A widget binds to a Metric ID.

Correct:

```text
Gauge → gpu.0.temperature.core
```

Incorrect:

```text
NvidiaTemperatureGauge
```

Provider details must not leak into generic widget components.

---

# 4. Telemetry correctness is non-negotiable

Never invent telemetry.

These states are semantically distinct:

```text
measured
estimated
stale
unavailable
stopped
error
unlimited
```

Rules:

- missing is not zero;
- HTTP failure is not 0%;
- unlimited is not 100%;
- stale data exposes age;
- estimated remains estimated;
- a balance is not a quota percentage;
- subscription status is not usage;
- activity count is not automatically provider rate-limit usage.

If no trustworthy value exists, represent the absence honestly.

---

# 5. AI quotas

The old WidgetOSD quota subsystem is NOT reusable.

Do not port:

- quota calculations;
- percentage mappings;
- fallback logic;
- window classification;
- cache semantics;
- quota UI semantics.

When Cerith reaches the AI-provider milestone, implement providers cleanly from zero.

Research hierarchy:

1. official documented source where available;
2. official local app/CLI structured data;
3. CodexBar;
4. OpenUsage;
5. TokenTracker;
6. other active implementations;
7. our own sanitized observations/fixtures.

Reference projects are corroboration, not truth.

Initial future priority:

```text
Claude
Codex
Antigravity
```

Do NOT implement these during repository bootstrap.

---

# 6. Native UI constraint

Production UI is Windows-native.

Current technical direction:

```text
C#
.NET 10
WinUI 3
Windows App SDK
Windows Composition
Win32 / DirectComposition when necessary
```

Do not introduce Electron or a browser runtime into the always-on monitoring path.

Web UI resources such as:

- 21st.dev;
- UI Skills;
- 60fps.design;
- transitions.dev;
- Mobbin;
- other design libraries;

are visual/interaction references.

Study their:

- hierarchy;
- spacing;
- feedback;
- animation;
- transitions;
- interaction patterns.

Then implement an original native equivalent.

Never copy external component code without checking:

- license;
- compatibility;
- provenance;
- required notices.

---

# 7. Mandatory repository structure

The initial repository should evolve toward exactly this shape:

```text
Cerith/
│
├─ README.md
├─ AGENTS.md
├─ CODE_QUALITY.md
├─ SECURITY.md
├─ CONTRIBUTING.md
├─ DEVELOPMENT_WORKFLOW.md
├─ REPO_BOOTSTRAP_CHECKLIST.md
├─ PUBLIC_READINESS_CHECKLIST.md
│
├─ Cerith.sln
│
├─ docs/
│  ├─ PRODUCT_SPEC.md
│  ├─ ARCHITECTURE.md
│  ├─ ROADMAP.md
│  ├─ METRICS.md
│  ├─ PROVIDERS_AI_QUOTAS.md
│  ├─ AI_QUOTA_REFERENCE_MATRIX.md
│  ├─ UI_UX.md
│  ├─ PLUGINS.md
│  ├─ TESTING_QA.md
│  ├─ SECURITY_PRIVACY.md
│  ├─ PERFORMANCE.md
│  ├─ DEPENDENCY_POLICY.md
│  ├─ OPEN_SOURCE_STRATEGY.md
│  └─ LEGACY_POLICY.md
│
├─ .agent/
│  ├─ STATE.md
│  ├─ HANDOFF.md
│  ├─ TASKS.md
│  ├─ WORKLOG.md
│  ├─ DECISIONS.md
│  └─ MODEL_ROUTING.md
│
├─ prompts/
│  ├─ START_CERITH_SUPER_PROMPT.md
│  └─ TASK_PROMPT_TEMPLATE.md
│
├─ .github/
│  ├─ CODEOWNERS
│  ├─ dependabot.yml
│  ├─ pull_request_template.md
│  ├─ ISSUE_TEMPLATE/
│  └─ workflows/
│
├─ src/
│  ├─ Cerith.Core/
│  ├─ Cerith.TelemetryService/
│  ├─ Cerith.WidgetHost/
│  └─ Cerith.ControlCenter/
│
├─ tests/
│  ├─ Cerith.Core.Tests/
│  ├─ Cerith.Telemetry.Tests/
│  └─ Cerith.Integration.Tests/
│
└─ tools/
```

Do not create extra projects or generic layers without concrete need.

Especially do not create:

```text
Cerith.Common
Cerith.Shared
Cerith.Foundation
Cerith.Infrastructure
Cerith.Domain
Cerith.Application
Cerith.Engine
Cerith.Utils
```

merely because a generic architecture pattern suggests them.

---

# 8. Project responsibilities

## Cerith.Core

Owns fundamental product contracts.

Initially:

```text
MetricId
MetricDescriptor
MetricSnapshot
MetricState
MetricConfidence
MetricRegistry
MetricBus
```

Core must not depend on:

- WinUI;
- concrete providers;
- LibreHardwareMonitor;
- PresentMon;
- Claude;
- Codex;
- Antigravity;
- SQLite implementation.

## Cerith.TelemetryService

Acquires/normalizes measurements.

During M0 only a deterministic `SyntheticProvider` is required.

## Cerith.WidgetHost

Future lightweight runtime for:

- desktop widgets;
- taskbar widgets;
- overlays.

During M0 it only needs enough functionality to prove an end-to-end synthetic metric path when the relevant task arrives.

## Cerith.ControlCenter

Future configuration/editor application:

- visual editor;
- ComponentLab;
- settings;
- provider setup;
- history;
- themes;
- profiles;
- plugin management;
- diagnostics.

Do not build the real editor during M0.

---

# 9. Zero-AI-slop rules

Generated output is not accepted because it compiles.

Every file, abstraction and dependency must earn its place.

## Every new file must justify its existence

Before creating a file, answer:

1. What cohesive responsibility does it own?
2. Why should that responsibility not live in an existing file?
3. Who uses it now?
4. What maintenance burden does it introduce?

If the justification is weak, do not create the file.

## Every new abstraction must justify its existence

Do not add interfaces/factories/managers/helpers/coordinators simply for hypothetical flexibility.

Extract an abstraction when:

- multiple real implementations exist;
- an external-effect boundary is required for testing;
- a stable public/plugin contract requires it;
- concrete coupling has become measurably harmful.

Start concrete.

## Every dependency must justify its existence

Before adding a package, determine:

- what problem it solves;
- why .NET/Windows/existing dependencies are insufficient;
- runtime size/cost;
- maintenance/security cost;
- license compatibility.

Do not add a dependency to avoid a small amount of straightforward code.

## Never generate filler

Avoid:

- comments that restate code;
- XML documentation on trivial internal members with no information value;
- empty catches;
- dead code;
- commented-out code;
- unused abstractions;
- placeholder telemetry;
- tests for getters/setters/framework behavior;
- duplicate docs;
- broad unrelated refactors;
- generic marketing prose;
- vague names such as `DataManager`, `CommonHelper`, `Utils`;
- TODOs without a tracked task.

If a model produces these, remove them before handoff.

---

# 10. Public-ready from first commit

Assume every committed byte may become public tomorrow.

Never commit:

- tokens;
- API keys;
- OAuth credentials;
- cookies;
- authorization headers;
- browser profiles;
- private screenshots;
- private provider payloads;
- email addresses or identifying account data unless intentionally public;
- local chat transcripts;
- machine-specific personal paths when unnecessary;
- agent private context;
- legacy personal artifacts.

A private GitHub repository is not permission to commit private data.

Before staging/committing:

- inspect `git status`;
- inspect staged diff;
- check new files;
- run configured preflight/security checks.

If a secret is ever committed, rotate/revoke it first. Removing the latest line alone does not unexpose it.

---

# 11. Open-source direction

Cerith is intended to become genuinely open source.

Current direction is intentionally provisional:

```text
main application
→ strong copyleft candidate (currently GPLv3)

plugin SDK/contracts
→ permissive candidate

official name/logo/brand assets
→ protected separately
```

Do not finalize or change licensing without reading `docs/OPEN_SOURCE_STRATEGY.md` and checking dependency compatibility.

---

# 12. Development memory and multi-agent rules

Project memory belongs to the repository.

Use:

```text
.agent/STATE.md
.agent/TASKS.md
.agent/WORKLOG.md
.agent/DECISIONS.md
.agent/HANDOFF.md
Graphify/code graph
Git
```

One task has one write owner at a time.

Do not prepare simultaneous edits to the same files for another agent.

For actual parallel work, use separate branches/worktrees.

---

# 13. Current milestone

We are at:

> **M0 — Foundation**

Do NOT begin with:

- LibreHardwareMonitor;
- PresentMon;
- Claude;
- Codex quota integration;
- Antigravity;
- SQLite history;
- plugin marketplace;
- advanced editor;
- polished production widgets.

We first prove the spine.

Target path:

```text
SyntheticProvider
→ MetricSnapshot
→ MetricBus
→ WidgetHost
→ visible value
```

---

# 14. M0 order

Expected sequence:

1. repository + solution skeleton;
2. dependency direction;
3. MetricId;
4. MetricDescriptor;
5. MetricSnapshot;
6. MetricState;
7. MetricConfidence;
8. MetricRegistry;
9. MetricBus;
10. SyntheticProvider;
11. minimal WidgetHost subscription;
12. tests;
13. initial performance baseline.

Do not implement all of these automatically.

Work only on the currently claimed task.

---

# 15. Your first task

Read `.agent/TASKS.md`.

Claim the first unclaimed P0 task.

If the repository is still at bootstrap, it should be:

> `CER-M0-001 — Repository and solution skeleton`

For CER-M0-001:

- create `Cerith.sln`;
- create only the explicitly defined projects;
- configure project references according to `docs/ARCHITECTURE.md`;
- make the solution build;
- add only minimal test/bootstrap infrastructure;
- preserve all security/quality configuration;
- add no real telemetry feature;
- add no provider;
- add no unnecessary NuGet package;
- add no speculative abstraction.

Before adding any file not already implied by the required project skeleton, justify it.

---

# 16. Git / GitHub workflow

Do not do feature development directly on `main`.

If Git is initialized and branch creation is appropriate:

- work on a focused branch;
- keep commits coherent;
- never use `--force` or destructive history operations casually;
- do not push secrets;
- do not publish/make the repo public.

Do not create a public GitHub repository unless explicitly instructed.

Do not add a real personal username/email into templates unless already configured by the user.

Leave placeholder `CODEOWNERS` values untouched unless you know the correct public GitHub username from repository config.

---

# 17. Engineering style

- smallest responsible change;
- explicit contracts;
- no speculative architecture;
- no unrelated refactor;
- no unnecessary package;
- no hidden magic;
- truthful telemetry;
- meaningful tests;
- measured performance;
- preserve unrelated changes;
- durable decisions go into project docs, not private chat memory.

---

# 18. Completion protocol

Before finishing CER-M0-001:

1. run the solution build;
2. run relevant tests if test projects exist;
3. inspect `git status`;
4. inspect `git diff`;
5. inspect all newly tracked files and ask whether each deserves to exist;
6. check for private/sensitive content;
7. remove AI slop;
8. update `.agent/TASKS.md`;
9. append a compact `.agent/WORKLOG.md` entry;
10. update `.agent/STATE.md` only if global state changed;
11. rewrite `.agent/HANDOFF.md` with:
    - goal;
    - completed work;
    - files changed;
    - verification;
    - decisions;
    - blockers;
    - do-not-redo notes;
    - next recommended task;
12. update Graphify if it is configured and structure changed.

Do not automatically start CER-M0-002.

Stop at a clean handoff boundary.

---

# 19. Action now

Read the repository in the specified order.

Inspect its real state.

Claim `CER-M0-001` if available.

Produce a short task-local plan.

Complete only that task.

Do not ask for confirmation for routine, reversible development actions that are already authorized by the repository rules.

If a minor choice is underspecified, choose the simplest solution consistent with Cerith architecture and record any durable decision.
