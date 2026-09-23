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
- AI quota monitors such as CodexBar, OpenUsage and TokenTracker.

The goal is not to clone any of them.

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

### Gaming

FPS, frame time, 1% lows, 0.1% lows, frame pacing, presentation/display metrics, active sessions.

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
