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
- primary planned research/integration direction for M2 gaming/frame telemetry;
- FPS, frame time, presentation metrics and related session evidence.

Boundary:
- generic Cerith widgets bind to MetricId, never PresentMon-specific types;
- derived gaming claims require measured definitions and validation.

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

Why it matters:
- native Windows quota monitoring for Claude, Codex, Antigravity and Grok;
- useful evidence around local credential/session discovery, reset windows, stale/auth/refresh states, WSL behavior, privacy and taskbar/tray surfaces;
- useful comparison for truthful handling of provider-reported windows instead of fabricated percentages.

Do not assume its provider endpoints, credential precedence, cache rules or semantics are correct for Cerith without independent validation.

### AI Limits

Repository: https://github.com/napxlexn/ailimits

Why it matters:
- native Windows quota overlay covering Claude, Codex, Copilot and Antigravity;
- useful evidence around read-only local auth sources, provider-specific usage retrieval, stale values, reset handling, low-overhead runtime and compact surfaces;
- another independent implementation against which provider behavior can be compared.

Its source is GPL-3.0-or-later. Treat it as research unless any future code reuse is explicitly license-reviewed; do not copy implementation casually.

### TokenTracker

Repository: https://github.com/xiufengsun/TokenTracker

Why it matters:
- broad AI usage/token/cost tracking;
- useful independent implementation for local tool discovery, parsing, accounting and usage-limit behavior;
- valuable cross-check when the narrower quota monitors disagree.

Its broader token/cost model is not automatically equivalent to provider rate-limit or subscription-quota semantics.

### Additional quota references

CodexBar and OpenUsage remain useful independent research implementations where current and relevant.

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
