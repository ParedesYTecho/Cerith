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

TokenTracker's pinned reference advertises and implements a 42-product activity catalog. The catalog is evidence that broad coverage is practical, but it is **not** 42 homogeneous local adapters and it is not a claim of Windows runtime acceptance in Cerith. It mixes passive readers, managed hooks, local databases, remote queries and shared parser families:

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

This is a **coverage target and research catalog**, not a claim that Cerith already supports all 42. Each collector enters only after its source, fields, platform behavior, privacy boundary, deduplication and semantics are verified.

Reference-specific caveats that matter before task decomposition:

| Reference item | Acquisition/platform caveat |
| --- | --- |
| Cursor | Activity includes a remote usage query authenticated from local state; it is not purely passive local-log telemetry. |
| TRAE Work CN | Uses a remote internal API path and is opt-in in the reference; network/auth/privacy are part of acceptance. |
| Claude Science | Reference Windows support goes through WSL for its inspected local database path. |
| Devin CLI | Reference Windows activity acquisition also relies on WSL for the inspected path. |
| Dots | Reuses the `pi` acquisition/parser family rather than defining an independent installation/parser. |
| GitHub Copilot | Combines multiple sources (current SQLite usage, legacy baseline and OTEL paths); overlap/dedup is part of the contract. |
| Qoder CN | Regional variant with separate paths/endpoints; do not collapse it into international Qoder. |
| Shared parser families | Codex-family, Claude-family, pi/OMP-family and OpenCode-family implementations reduce repeated mechanics but do not erase product-specific semantics. |

### Usage limits: 17-provider reference catalog

The current TokenTracker limits registry covers:

1. Claude
2. Codex
3. Cursor
4. Gemini CLI / Code Assist
5. Kimi
6. Kiro
7. Grok Build
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

Notable acquisition distinctions in the pinned reference:

| Usage-limit integration | Reference acquisition note |
| --- | --- |
| OpenCode Go | API key + official usage API is the preferred route at this pin; dashboard scraping is a compatibility fallback and local-cost estimation is explicit opt-in, not equivalent quota truth. |
| Gemini CLI / Code Assist | OAuth-backed Code Assist quota; do not relabel this as generic Gemini consumer/API quota. |
| Grok Build | Billing/credit-period semantics; keep included quota, on-demand/prepaid balance and ordinary activity separate. |
| Qoder / Qoder CN | Separate regional/product paths and auth; treat them as distinct integrations. |
| Ark Coding Plan / Agent Plan | Separate plan surfaces and units; do not merge because branding is shared. |

## Acquisition principles

Prefer, in order appropriate to the specific tool:

- passive structured local files/databases;
- provider-supported structured events/hooks;
- existing official local sessions/credential stores;
- official CLI/RPC output;
- documented or first-party-observed provider endpoints;
- one-time explicit setup when no safe automatic route exists.

There is no universal acquisition method. The fact that 42 tools can be presented in one dashboard is evidence for a common product model, not evidence for one common parser/auth framework.

For M3.B, prefer existing passive/local activity over generating model requests. Do not change credentials or create activity solely to validate a collector.

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

### First bounded activity slice — OpenCode

CER-M3-005 implements one OpenCode local-activity source. The collector reads the standard `opencode.db` under the XDG data root (`XDG_DATA_HOME/opencode/opencode.db`, or `~/.local/share/opencode/opencode.db`) or an explicit `OPENCODE_DB` override (absolute as given, relative under the OpenCode data directory). OpenCode can use channel-specific database names; this slice does not guess or scan them. If the service cannot resolve the active database, it reports unavailable.

The source evidence is pinned to OpenCode v1.17.9: first-party [assistant message schema](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/session/message.ts), [SQLite schema](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/session/sql.ts), [message updater](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/session/message-updater.ts), [session projector](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/session/projector.ts), [session creation](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/session.ts), [database path selection](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/database/database.ts) and [XDG data path](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/global.ts) define the fields and persistence behavior. Session creation assigns `InstallationVersion` to `Session.version`; the projector stores it in `session.version`. Each `session_message.session_id` references that parent session, and the projector writes assistant updates to the message row associated with the same session. Cerith joins the selected assistant row to that parent within its read transaction and reads only the version scalar for qualification; it does not expose the session ID or version as telemetry. Only `1.17.9` is supported. A known other version yields valueless `Unavailable/source_version_unsupported`, because its internal semantics are outside the validated contract. A missing parent/version or unusable version yields valueless `Error/source_version_missing` or `Error/source_version_invalid`, because it violates the persisted source contract. Version qualification happens before the guarded message-data projection; there is no fallback to an older assistant. TokenTracker's [pinned v1.1.3 README](https://github.com/xiufengsun/TokenTracker/blob/a8520617ece3f15a07c342e2c7909eb311234522/README.md) corroborates OpenCode SQLite as an existing passive-reader pattern, not as Cerith's authority. This is an internal application schema, not a stable public telemetry contract; revalidate it when the host version or schema changes. The selected source is local/read-only and requires no network, credentials, model request or application-state write. Its JSON record also contains text, reasoning and tool payloads, so the native reader returns only fixed, type-checked scalar projections and never materializes or publishes the raw JSON.

The collector considers only the newest 64 message metadata rows ordered by `time_created DESC, rowid DESC`, then uses the newest-created assistant in that window. It does not fall back to an older assistant if the selected record is incomplete, failed or invalid. A valid record requires explicit nonnegative integral values for all five fields OpenCode reports: `tokens.input`, `tokens.output`, `tokens.reasoning`, `tokens.cache.read` and `tokens.cache.write`. Those are source-reported categories; Cerith does not infer that they are disjoint or additive. Model ID, model provider ID and `time.completed` are also direct source fields. V2 does not define `tokens.total`; no total, cost, session/project identity or source row ID is published.

The eight source-qualified MetricIds are listed in `METRICS.md`. All snapshots from one poll share `ObservedAt` and state. `occurred_at` preserves the source completion timestamp; `Fresh` means the read succeeded, not that the activity is recent. Polling repeats the latest snapshot and is not a delta or event history. MetricBus remains per-ID and cannot publish a tuple atomically, so a consumer must group by `ObservedAt` and accept only complete groups. No values are cached across unavailable/error results.

The reader reopens the database on each serial five-second poll, examines at most 64 metadata rows and one selected JSON record, limits one SQLite value/row to 64 KiB, uses a 100 ms busy timeout and a 250 ms SQL progress deadline with cancellation interruption. This bounds the query work and selected record size; it is not a hard wall-clock guarantee for native open or an individual JSON parse. Missing source/window or incomplete activity produces valueless `Unavailable`; malformed data/schema/read produces valueless `Error` with a sanitized code. A sanitized host qualification probe on 2026-10-04 selected no assistant in the bounded window, so there was no parent `session.version` to compare; host version qualification is unverified on this run. The production collector remains `LIVE_NOT_AVAILABLE_ON_THIS_HOST / activity_absent`; no message content was read.

## Product surfaces and analytics ownership

This document owns acquisition semantics, not duplicate UI requirements.

- Dashboard/history product scope is canonical in `ROADMAP.md` (M7) and `PRODUCT_SPEC.md`.
- Compact floating/taskbar/tray/detail behavior is canonical in `UI_UX.md` and `ROADMAP.md` (M4).
- This acquisition layer only guarantees truthful normalized MetricIds and safe attribution inputs for those later surfaces.

TokenTracker, Gengchou and AI Limits remain the reference products recorded in `REFERENCE_PROJECTS.md`; their UI/runtime stacks are not imported.

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

## Milestone ownership

The canonical milestone split lives in `ROADMAP.md`: M3 owns acquisition/setup, M4 owns generic compact surfaces, and M7 owns persistence/analytics. Keep this document focused on what collectors must acquire and normalize.
