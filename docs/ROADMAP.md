# Cerith Roadmap

## M0 — Foundation

### M0.1 Repository / safety

- canonical docs;
- Git hygiene;
- code-quality policy;
- security baseline;
- CI skeleton;
- agent workflow;
- dependency/license discipline.

### M0.2 Metric Core

- MetricId;
- MetricDescriptor;
- MetricSnapshot;
- MetricState;
- MetricConfidence;
- MetricRegistry;
- MetricBus.

### M0.3 Runtime skeleton

- TelemetryService;
- WidgetHost;
- ControlCenter shell;
- IPC prototype;
- diagnostics.

Exit:

```text
SyntheticProvider
→ normalized MetricSnapshot
→ MetricBus
→ WidgetHost
→ visible value
```

with tests and measured baseline.

## M1 — Hardware MVP

- LibreHardwareMonitor;
- CPU;
- GPU;
- RAM;
- VRAM;
- basic fans;
- storage/network basics;
- diagnostics.

## M2 — Gaming MVP

- PresentMon;
- FPS;
- frame time;
- 1% low;
- active game/session summary.

Later:

- RTSS;
- advanced latency;
- frame-generation distinctions.

## M3 — AI Quotas MVP

Clean implementation:

- Claude;
- Codex;
- Antigravity.

Requirements:

- provider conformance tests;
- sanitized fixtures;
- source/confidence metadata;
- independent cross-validation.

## M4 — Widget Engine

- text;
- metric;
- bar;
- ring;
- sparkline;
- simple graph;
- container;
- binding;
- persistence;
- themes.

## M5 — Visual Editor

- canvas;
- selection;
- snap;
- layers;
- inspector;
- undo/redo;
- alignment;
- copy/paste.

## M6 — Rules & Profiles

- conditions;
- alerts;
- visual reactions;
- contextual profiles.

## M7 — History

- SQLite;
- retention/downsampling;
- charts;
- sessions.

## M8 — Public Plugin SDK

- stable versioned contracts;
- permissions;
- examples;
- docs.

## M9 — Ecosystem

- widget/theme packs;
- registry concept;
- import/export.

## M10 — Remote / Multi-PC

- remote metric protocol;
- second-machine dashboards.

## Rule

Do not skip foundations to chase screenshots.
