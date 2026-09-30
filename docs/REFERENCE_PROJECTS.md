# External Reference Projects and Research Provenance

Status: Canonical research provenance.
Purpose: preserve which external projects materially influenced Cerith and why, without making any of them Cerith's architectural base or automatic source of truth.

## Core rule

Cerith learns from external projects but keeps its own architecture.

A project listed here may contribute product ideas, observed edge cases, provider-source clues, UX patterns or implementation comparisons. That does **not** mean:

- its architecture should be copied;
- its code should be imported;
- its telemetry semantics are automatically correct;
- its API behavior is authoritative;
- its license permits copying code into Cerith.

When implementation-specific behavior matters, revalidate it against official sources and current upstream code. Record the upstream revision/commit when the exact behavior becomes a durable dependency or design input.

## Product and telemetry references

### LibreHardwareMonitor

Repository: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor

Role in Cerith:
- primary initial hardware acquisition backend for M1;
- source for CPU/GPU/RAM/VRAM/temperature/fan/etc. sensor observations where supported.

Boundary:
- LibreHardwareMonitor belongs in TelemetryService/provider code, not Core or generic widgets;
- Cerith normalizes readings into its own MetricId/MetricSnapshot contracts;
- sensor names/mappings must be verified rather than assumed universal.

### PresentMon

Repository: https://github.com/GameTechDev/PresentMon

Role in Cerith:
- official Service/API source for CER-M2-001 displayed FPS and displayed frame time and the CER-M2-002 displayed FPS P1 statistic;
- pinned integration contract: [v2.6.0 release](https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0), [Service/SDK guidance](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/README-Service.md) and tagged [PresentMonAPI.h](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonAPI2/PresentMonAPI.h).
- P1 semantic provenance: tagged [`MetricsCalculatorDisplay.cpp`](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/CommonUtilities/mc/MetricsCalculatorDisplay.cpp) emits zero intervals for non-displayed frames; [`DynamicMetric.h`](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/DynamicMetric.h) retains finite zeros in percentile input; tagged [`DynamicStat.cpp`](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/DynamicStat.cpp) reverses rank for reciprocal metrics, interpolates the selected raw interval percentile, and reciprocates. Tagged [`Middleware.cpp`](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/Middleware.cpp) sets a 4096-frame-metric per-swap-chain ring capacity. The API rejects `PM_STAT_COUNT` for dynamic queries. Cerith's controlled high-drop validation used a separate `PM_METRIC_DROPPED_FRAMES` measurement to reconstruct the zero-sample population absent from `pmConsumeFrames` rows before comparing raw-oracle P99/P1.

Boundary:
- generic Cerith widgets bind to MetricId, never PresentMon-specific types;
- use the official Service/API and loader contract; do not ship the service middleware DLL;
- the API header defines the source metrics, average statistic, PID tracking, dynamic and frame queries; local runtime behavior is recorded separately in CER-M2-001 evidence;
- derived gaming claims require measured definitions and validation; this slice does not establish anti-cheat compatibility or universal capture coverage.

### MSI Afterburner / RTSS / HWiNFO / Rainmeter

Role in Cerith:
- product/capability/UX references;
- examples of monitoring, overlays, layouts, sensor surfaces and customization.

Boundary:
- they are not Cerith's codebase or architectural template;
- their existence does not justify provider-specific widgets or closed metric contracts.

## AI quota research references

The original Cerith design explicitly studied AI quota monitors. These repositories are research/corroboration sources, not authorities. Official provider behavior remains higher priority.

### Gengchou

Repository: https://github.com/ynjmxn/gengchou

Pinned reference revision: `4d02b9d1f85e806c92c4defc8e8c4c822467b6f3`.

Primary Cerith role: **Windows compact-surface and shell-integration reference**, plus provider-acquisition corroboration.

High-value lessons:

- taskbar widget, compact floating monitor, provider tray icons and detail popup expose the same quota state at different density levels;
- compact surfaces select a stable headline window but allow an urgent/high-usage window to take over, keeping detail available on hover/click;
- semantic placement survives Explorer/taskbar rebuilds, monitor changes and RDP/lock transitions better than raw saved screen coordinates;
- exact-DPI/theme rendering and deterministic dump modes make visual regressions reviewable instead of relying on hand screenshots;
- provider detection spans Windows files, Credential Manager, existing Desktop/CLI sessions and already-running WSL distributions without waking WSL just to poll;
- stale/auth/rate-limit states keep last-known values visible without pretending they are fresh;
- `compact_view.rs` is a useful example of keeping compact product semantics independent of HWND/DPI/drawing code.

Important negative lesson: `docs/ADDING_A_PROVIDER.md` documents that provider identity is spread across many files and adding a provider can become a tree-wide diff. Cerith should not repeat that coupling; provider acquisition must remain isolated from surface rendering and WidgetHost must remain MetricId-driven.

License: MIT at the pinned revision. Code adaptation is possible only after explicit provenance/attribution review; the project is still not Cerith's architectural base.

### AI Limits

Repository: https://github.com/napxlexn/ailimits

Pinned reference revision: `a42bbd043070538ff5c4161b7eda952720173345`.

Primary Cerith role: **compact native visual-system and lightweight runtime reference**, plus corroboration through an independently implemented adapter. Shared-upstream limitations must be recorded; a second client hitting the same backend is not an independent quota oracle.

High-value lessons:

- Compact / Medium / Expanded detail levels preserve hierarchy while changing density live;
- horizontal/vertical bars, width tiers, provider-count growth, palette, opacity and brightness show how one semantic view can adapt without becoming multiple unrelated widgets;
- the taskbar panel and two-ring tray indicator keep useful state visible when the main floating widget is hidden;
- one renderer, one layout system and one theme source reduce visual drift;
- provider fetches run independently, last-known data survives transient failure, and stale age is visible;
- monotonic freshness logic is separated from wall-clock reset/countdown presentation;
- performance claims are backed by a reproducible audit rather than intuition.

Boundary: AI Limits is GPL-3.0-or-later. Treat implementation code as research unless a future license decision explicitly permits reuse. Visual/interaction ideas may be reinterpreted originally in Cerith's native component system; do not transplant code or branding.

### TokenTracker

Repository: https://github.com/xiufengsun/TokenTracker

Pinned current reference revision: `a8520617ece3f15a07c342e2c7909eb311234522` (v1.1.3 release commit). CER-M3-001 quota research used the earlier `daa6c55099d34458ff773f6c1edd777f16fb7693`; re-check current code before implementation-significant reuse.

Primary Cerith role: **broad AI acquisition, zero-config onboarding, local analytics and dashboard reference**.

High-value lessons:

- 42 AI tools are discovered through provider-appropriate passive readers, structured local databases/files, official hooks or provider APIs instead of forcing one universal acquisition technique;
- 17 real-time usage-limit integrations coexist with broader token/activity tracking while remaining a separate product concept;
- local-first/zero-config behavior auto-installs narrowly scoped hooks where appropriate and otherwise reads existing local state;
- the Windows/macOS/Linux product surfaces share normalized usage data across dashboard, desktop widgets and native shells;
- the dashboard demonstrates useful information architecture for trends, model breakdown, cost, activity heatmaps and project attribution;
- per-provider parsers have dedicated tests and TokenTracker's own lessons document several real normalization failures, reinforcing that token/cost semantics must be verified rather than copied blindly;
- last-good caching and provider-specific setup hints keep partial provider availability from making the whole product unusable.

Boundary: TokenTracker's broad Node/dashboard architecture is not Cerith's runtime architecture, cloud/leaderboard/skills/pet features are not automatically Cerith requirements, and activity/cost telemetry is not quota. Its license is MIT at the pinned revision, so focused code adaptation may be considered after explicit license/provenance review and semantic validation; wholesale copying is neither necessary nor desired.

### Additional quota references

CodexBar and OpenUsage remain useful independent research implementations where current and relevant.

Canonical repositories for the CER-M3-001 comparison: [CodexBar](https://github.com/steipete/CodexBar) and [OpenUsage](https://github.com/robinebers/openusage). Other repositories named OpenUsage are distinct projects and were not substituted. Exact audited revisions, file links, source classifications and disagreements for all five monitors are maintained only in [AI_QUOTA_REFERENCE_MATRIX.md](AI_QUOTA_REFERENCE_MATRIX.md). No source code was copied or dependency added; future reuse still requires a separate license review.

CER-M3-001 also uses first-party [openai/codex](https://github.com/openai/codex) for protocol/backend/auth-lifecycle evidence, official [Claude Code statusline documentation](https://code.claude.com/docs/en/statusline) for event-fed quota semantics, and official Antigravity product documentation for published UI/plan behavior. First-party source observations do not make undocumented backend endpoints supported APIs. Concrete endpoint provenance and local-probe limits belong to the matrix; normalized Cerith behavior belongs to `PROVIDERS_AI_QUOTAS.md`.

The original master brief also named **Claude Code Usage Monitor** as a research reference. The historical material available to Cerith does not preserve a sufficiently certain canonical repository URL for that name, so this document deliberately records the reference without inventing a link. If its exact provenance is re-established later, add the verified repository and revision here.

These references do not outrank official provider sources merely because they are listed in Cerith docs.

## AI quota validation hierarchy

Research provenance and validation authority are different concepts.

For a concrete provider, prefer:

1. official documented API/UI/CLI behavior where available;
2. official local application/CLI structured data;
3. stable authenticated provider behavior that can be independently verified;
4. multiple independent current implementations, including Gengchou, AI Limits, TokenTracker, CodexBar/OpenUsage as applicable;
5. Cerith's sanitized observations and golden fixtures.

A mismatch is investigated, never averaged away.

## Legacy WidgetOSD

WidgetOSD is historical evidence only.

Preserve knowledge, not implementation.

Potentially reusable only after independent validation:
- sanitized response shapes/fixtures;
- sensor mappings;
- edge cases and failure modes;
- DPI/multimonitor lessons;
- QA/security lessons.

Do not port its architecture, AI quota calculations, fallback logic, cache semantics or quota UI semantics.

## UI/UX research

The detailed design-reference workflow belongs in `docs/UI_UX.md`.

External web/component sources are interaction and visual references only. Production Cerith remains native unless a future measured, recorded decision changes that.

## Provenance rule for future research

When an external project materially shapes a durable Cerith decision, record at minimum:

- project/repository URL;
- area of Cerith it informed;
- concrete lesson/evidence;
- what remains untrusted or must be revalidated;
- exact upstream revision when implementation details matter;
- relevant license/provenance constraints if code reuse is contemplated.

Do not store private chat transcripts, raw account data or generated context dumps as project memory.
