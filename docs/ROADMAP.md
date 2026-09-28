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

For M0, ControlCenter shell means the existing project and dependency direction; its functional UI waits for a concrete capability. Diagnostics means only the runtime baseline: TelemetryService reports ready with the pipe and `synthetic.counter`, then normal shutdown; WidgetHost shows Connecting, the value, or unavailable. Hardware/provider health diagnostics belong to M1.

Exit:

```text
SyntheticProvider
→ normalized MetricSnapshot
→ MetricBus
→ WidgetHost
→ visible value
```

with tests, this runtime diagnostic baseline and the measured IPC baseline (D-016).

## M1 — Hardware MVP

**M1 — Hardware MVP: DONE** (proposed for independent Advisor review).

- LibreHardwareMonitor;
- CPU;
- GPU;
- RAM;
- VRAM;
- basic fans;
- storage/network basics;
- diagnostics.

Exit evidence: CER-M1-001/002/003 provide the LHM CPU/GPU/VRAM/fan, Windows RAM and Storage/Network basics; CER-M1-004 adds sanitized discovery and sample diagnostics plus disconnect/reconnect behavior. The non-elevated host produced CPU/RAM/GPU/VRAM/fan and Network readings but LHM discovered zero Storage. A separate elevated run of the unchanged LHM 0.9.6 service discovered four Storage devices and published `storage.0.usage`, `storage.0.total.bytes` and `storage.0.free.bytes` as coherent Fresh/Measured values. This proves a positive Storage path only with elevation on this host; non-elevated Storage remains unavailable here. A targeted console-signal test also observed `TelemetryService stopped.` and exit code 0. Deferred throughput, absent sensors, internal LHM failures, persistent device identity and topology limits remain explicit in `docs/METRICS.md`. M2 is next; no M2 work is part of this exit.

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
