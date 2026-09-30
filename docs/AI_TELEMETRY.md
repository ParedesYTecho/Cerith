# AI Telemetry

Status: Canonical product/acquisition direction for AI observability beyond the quota-only contract.

## Purpose

Cerith's AI capability is broader than subscription quota, but it must keep different semantics separate.

```text
AI telemetry
├─ quota / usage limits
├─ local token/model activity
├─ derived cost
├─ project/session attribution
└─ service status (future/optional)
```

Quota normalization and auth/failure rules live in `PROVIDERS_AI_QUOTAS.md`. This document owns the broader local AI-tool catalog, acquisition principles and cross-milestone product direction.

## Reference snapshots

Current reference revisions used for the product direction:

- TokenTracker `a8520617ece3f15a07c342e2c7909eb311234522` (v1.1.3 release commit);
- Gengchou `4d02b9d1f85e806c92c4defc8e8c4c822467b6f3`;
- AI Limits `a42bbd043070538ff5c4161b7eda952720173345`.

Exact provider semantics can decay independently of these revisions; revalidate implementation-significant behavior.

## Current breadth targets

### Local AI activity: 42-tool reference catalog

TokenTracker's current reference catalog demonstrates automatic/local acquisition for:

1. Claude Code
2. Codex CLI
3. AStudio
4. Cursor
5. Gemini CLI
6. Antigravity
7. Kiro
8. OpenCode
9. OpenClaw
10. Every Code
11. Hermes Agent
12. GitHub Copilot
13. Kimi Code
14. CodeBuddy
15. WorkBuddy
16. Grok Build
17. oh-my-pi
18. OmO
19. pi
20. Dots
21. Prime Agent
22. Craft Agents
23. Reasonix
24. Kilo CLI
25. Kilo Code
26. Roo Code
27. Zed Agent
28. Goose
29. Droid
30. Mimo Code
31. ZCode
32. Qoder
33. AnythingLLM Desktop
34. Claude Science
35. DeepSeek Harness
36. TRAE Work CN
37. LM Studio
38. Unsloth Studio
39. Devin CLI
40. Cline
41. MiniMax Code
42. Command Code

This is a **coverage target and research catalog**, not a claim that Cerith already supports all 42. Each collector enters only after its source, fields, privacy boundary, deduplication and semantics are verified.

### Usage limits: 17-provider reference catalog

The current TokenTracker limits registry covers:

1. Claude
2. Codex
3. Cursor
4. Gemini / Code Assist scope to be named precisely during implementation
5. Kimi
6. Kiro
7. Grok / Grok Build scope to be named precisely during implementation
8. GitHub Copilot
9. Antigravity
10. ZCode
11. OpenCode Go
12. Command Code
13. Qoder
14. Qoder CN
15. Ark Coding Plan
16. Ark Agent Plan
17. Devin

Codex is already implemented in Cerith. The remaining providers are targets for evidence-driven expansion, not permission to import TokenTracker's labels or normalization unchanged.

## Acquisition principles

Prefer, in order appropriate to the specific tool:

- passive structured local files/databases;
- provider-supported structured events/hooks;
- existing official local sessions/credential stores;
- official CLI/RPC output;
- documented or first-party-observed provider endpoints;
- one-time explicit setup when no safe automatic route exists.

There is no universal acquisition method. The fact that 42 tools can be presented in one dashboard is evidence for a common product model, not evidence for one common parser/auth framework.

### Zero-config target

A normal first run should detect supported tools and useful existing state automatically.

A hook may be installed automatically only when its behavior is narrow, reversible, attributable to Cerith and does not overwrite unmanaged user configuration. Passive readers are preferred when they provide equivalent freshness.

If quota requires explicit authorization, setup happens once. Successful configuration persists. Repeated provider setup on ordinary Cerith startup is a defect.

## Privacy boundary

Collect only the minimum structured telemetry required for the feature.

Allowed when sourced/verified:

- timestamps;
- model/provider/tool identity;
- scalar token counters;
- cache/reasoning counters when semantics are known;
- bounded local session/activity identity;
- project attribution that does not require storing private content;
- quota/reset/credit fields under the quota contract.

Do not retain prompts, completions, source code, attachments, raw credentials or arbitrary transcript bodies.

A parser may need to open a structured record containing private content to reach usage metadata; if so, it must extract only the required scalar metadata and discard content immediately. Prefer sources that avoid reading content entirely.

## Token and cost semantics

Do not assume providers agree on token fields.

At minimum verify:

- whether input includes cached input;
- whether reasoning is included in output or separate;
- whether counters are per-request, snapshots or cumulative;
- whether mirrored/imported sessions duplicate another tool's activity;
- whether model identifiers map to a published price.

Derived cost must retain pricing provenance and must not become provider-reported billing. Unknown pricing yields unknown/partial cost, not fabricated zero-cost meaning.

## Product analytics target

Once history exists, ControlCenter should be able to compose a local dashboard with:

- usage trends;
- model/provider breakdown;
- cost analysis with provenance;
- activity heatmap;
- safe project attribution;
- quota/usage-limit status.

TokenTracker demonstrates the value of this information architecture. Cerith should implement it through its own native/history contracts rather than importing TokenTracker's dashboard runtime.

## Compact surfaces target

Gengchou and AI Limits demonstrate that the same provider state can remain useful without opening a dashboard.

Cerith's widget/surface engine should support:

- desktop/floating metric views;
- taskbar compact views;
- tray indicators;
- expanded/detail inspection;
- adaptive density and truthful stale/error states.

These surfaces consume MetricIds/history; they do not know provider auth or acquisition details.

## Parallel implementation boundary

Provider-specific acquisition should be physically isolated where practical, conceptually:

```text
src/Cerith.TelemetryService/Providers/AI/<Provider>/
tests/Cerith.Telemetry.Tests/Providers/AI/<Provider>/
tests/Cerith.Telemetry.Tests/Fixtures/<Provider>/
```

Do not create empty folders merely to match this sketch. Create a provider path only when work begins.

One write owner per provider boundary. Shared files, dependencies, auth infrastructure and registration are owned by a single integration task. A provider worker that discovers a shared requirement stops and escalates instead of editing another owner's path.

## Reference lessons, not architecture inheritance

Use:

- TokenTracker for acquisition breadth, zero-config patterns and analytics ideas;
- Gengchou for compact Windows surfaces and shell resilience;
- AI Limits for adaptive compact layout and lightweight rendering discipline.

Do not inherit:

- provider-specific UI coupling;
- a web/Node runtime as Cerith's native runtime;
- cloud sync/leaderboards/skills/pets as accidental requirements;
- GPL implementation code without an explicit license decision;
- external normalization assumptions without verification.

## Milestone mapping

- **M3**: AI acquisition, quota expansion, local activity collectors and setup/auth semantics.
- **M4**: generic widget engine plus compact floating/taskbar/tray/detail surfaces.
- **M7**: local history and analytics dashboard.

This mapping keeps acquisition, rendering and historical analysis separate while still delivering the reference-product capabilities through Cerith's universal metric architecture.
