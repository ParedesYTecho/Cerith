# Cerith Product Specification

Version: 1.0
Status: Product source of truth
Name status: working name

## Vision

Cerith is a native Windows telemetry platform.

It combines the best ideas from:

- MSI Afterburner;
- RTSS;
- HWiNFO / LibreHardwareMonitor;
- Rainmeter;
- modern native widget systems;
- AI quota monitors such as Gengchou, AI Limits, TokenTracker, CodexBar and OpenUsage.

The goal is not to clone any of them.

The external projects that materially informed Cerith, and what is or is not trusted from each, are recorded in `REFERENCE_PROJECTS.md`. Reference provenance is distinct from implementation authority.

The goal is a universal system:

```text
ANY METRIC
    ↓
UNIVERSAL METRIC BUS
    ↓
ANY VISUALIZATION
    ↓
ANY SURFACE
```

## Core value

> **Any metric. Any design. Anywhere on your desktop.**

## Surfaces

- desktop;
- taskbar;
- game overlay;
- second monitor;
- tray;
- dashboard;
- future remote display.

## Capability groups

### Hardware

CPU, GPU, RAM, VRAM, storage, network, power, clocks, temperatures, fans and supported vendor sensors.

The long-term read-only hardware catalog should be broad, but each metric enters only after its source, units and semantics are verified. Priority fan telemetry includes GPU fan speed and a CPU fan only when the CPU_FAN identity can be established without guessing. Where available, Cerith may expose both observed RPM and the provider's fan control/duty percentage; these are different quantities and must not be presented as interchangeable. Ambiguous motherboard channels are not automatically classified as chassis, CPU or PSU fans.

Fan control and custom fan curves are not part of the current telemetry scope.

### Gaming

FPS, frame time, 1% lows, 0.1% lows, frame pacing, presentation/display metrics, active sessions, dropped/repeated-frame information, and frame-type/generated-frame information where the provider exposes a verifiable distinction.

Application FPS, Displayed FPS and Presented FPS are separate semantic metrics. Cerith must not collapse them into a generic “real FPS” number or infer generated frames from arithmetic between rates unless the underlying provider contract proves that interpretation.

### AI quotas

Authoritative or explicitly classified usage data for:

- Claude;
- Codex;
- Antigravity;
- Copilot;
- Cursor;
- Grok;
- future providers/plugins.

### Historical telemetry

Local history with efficient retention/downsampling.

### Diagnostic validation sessions

Cerith should eventually provide an explicit **Test / Diagnostic Session** mode for development, troubleshooting and validation. A user starts and stops a bounded session while a game or workload is running. The session records all Cerith-observable metric samples relevant to that target, plus sanitized runtime/provider diagnostics and timing metadata needed to reproduce and analyze discrepancies.

The resulting local bundle should be inspectable and exportable for offline analysis. Where useful, Cerith should support comparing a session against an independently captured trace from another hardware/game telemetry tool. External tools are comparison references, not product dependencies or automatic ground truth; differences in source, sampling window and semantics must remain visible.

“Capture everything” in this mode means all relevant Cerith telemetry and diagnostic state for the selected session, not unrestricted collection of secrets, personal data or unrelated machine activity.

### Rules and alerts

Conditions over any metric.

### Contextual profiles

Gaming, Coding, Desktop, Streaming, Battery and user-defined profiles.

### Visual editor

A design-tool-like editor:

```text
canvas
layers
selection
drag/drop
resize
snap
alignment
groups
property inspector
data binding
rules
preview
undo/redo
```

### Plugin ecosystem

Community integrations without modifying Core.

## UX goal

First-run:

```text
install
→ detect hardware/providers
→ choose preset
→ choose location
→ useful
```

UX target: when detection and preset selection succeed normally, a new user should reach a useful, polished first setup in about 30 seconds. This is a product-experience target, not a measured performance claim.

Advanced:

```text
open editor
→ add component
→ bind Metric ID
→ style
→ add rule/motion
→ publish to surface
```

## Personalization

Users should be able to customize:

- typography;
- spacing;
- radius;
- borders;
- shadow;
- material;
- opacity;
- icons;
- colors;
- number formatting;
- charts;
- animation;
- conditions;
- layouts;
- bindings;
- themes;
- profiles.

## Expressions

Examples:

```text
{gpu.0.temperature.core}°C
{ram.used_gb:0.0} / {ram.total_gb:0.0} GB
Claude {ai.claude.session.remaining}% · {ai.claude.session.reset | countdown}
```

## Product quality bar

Cerith should feel:

- native;
- fast;
- polished;
- trustworthy;
- coherent;
- deeply customizable.

It must not feel like:

- an Electron dashboard;
- a collage of unrelated widgets;
- a generic AI-generated frontend;
- a tool that guesses data.

## Performance philosophy

Performance is measured, not assumed.

Initial targets are defined in `PERFORMANCE.md`.

## Non-goals for v0.1

Do not attempt:

- cloud sync;
- marketplace;
- remote telemetry;
- mobile companion;
- dozens of AI providers;
- full RTSS feature parity;
- every vendor hardware API;
- advanced AI insights.

The architecture may permit them later.
