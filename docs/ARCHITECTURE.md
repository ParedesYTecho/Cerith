# Cerith Architecture

Version: 1.0

## High-level design

```text
┌─────────────────────────────────────────────┐
│             Cerith.ControlCenter           │
│ editor · settings · history · diagnostics  │
└──────────────────────┬──────────────────────┘
                       │ IPC
┌──────────────────────▼──────────────────────┐
│              Cerith.WidgetHost             │
│ desktop · taskbar · overlay · dashboard    │
└──────────────────────┬──────────────────────┘
                       │ subscriptions
┌──────────────────────▼──────────────────────┐
│                Cerith.Core                 │
│ MetricBus · Registry · Snapshot contracts  │
│ future rules · expressions · profiles      │
└──────────────────────┬──────────────────────┘
                       │
┌──────────────────────▼──────────────────────┐
│          Cerith.TelemetryService           │
│ providers · refresh · cache · backoff      │
└─────────┬────────────┬────────────┬─────────┘
          │            │            │
      Hardware       Gaming         AI
```

## Initial solution structure

```text
Cerith/
├─ Cerith.sln
├─ src/
│  ├─ Cerith.Core/
│  ├─ Cerith.TelemetryService/
│  ├─ Cerith.WidgetHost/
│  └─ Cerith.ControlCenter/
├─ tests/
│  ├─ Cerith.Core.Tests/
│  ├─ Cerith.Telemetry.Tests/
│  └─ Cerith.Integration.Tests/
└─ tools/
```

Do not create additional projects without a concrete requirement.

## Cerith.Core

Owns fundamental contracts:

- `MetricId`;
- `MetricDescriptor`;
- `MetricSnapshot`;
- `MetricState`;
- `MetricConfidence`;
- `MetricRegistry`;
- `MetricBus`.

Core must not depend on:

- WinUI;
- concrete providers;
- LibreHardwareMonitor;
- PresentMon;
- Claude/Codex/Antigravity;
- SQLite implementation.

## Cerith.TelemetryService

Acquires and normalizes measurements.

M0 only requires a deterministic `SyntheticProvider`.

Future provider groups:

```text
Hardware
Gaming
AI
System
```

## Cerith.WidgetHost

Lightweight always-on runtime.

Future responsibilities:

- desktop widgets;
- taskbar widgets;
- overlays.

It should not host the heavy visual editor.

## Cerith.ControlCenter

Configuration/editor application.

Future responsibilities:

- visual editor;
- ComponentLab;
- providers/settings;
- history;
- themes;
- profiles;
- plugin management;
- diagnostics.

## Process separation

Preferred long-term shape:

```text
Cerith.TelemetryService.exe
Cerith.WidgetHost.exe
Cerith.ControlCenter.exe
```

Possible future:

```text
Cerith.PluginHost.exe
```

Do not split processes merely for conceptual purity. Benchmark first.

## Provider model

A provider:

```text
discovers capability
refreshes source
normalizes
publishes MetricSnapshot
reports health
```

Provider-specific semantics remain outside WidgetHost.

## Widget model

Correct:

```text
Gauge → gpu.0.temperature.core
```

Incorrect:

```text
NvidiaTemperatureGauge
```

## IPC

Requirements:

- local-only by default;
- versioned;
- bounded;
- cancellable;
- reconnectable;
- no raw secrets;
- low overhead.

The first M0 implementation uses Windows Named Pipes (D-016): TelemetryService serves bounded, versioned snapshots and WidgetHost subscribes through a local-only client. This does not define the future public plugin protocol.

For the M0 exit demo (D-018), TelemetryService runs the SyntheticProvider and pipe server in one console process. A minimal unpackaged WinUI 3 WidgetHost window reads that pipe through NamedPipeMetricClient and displays `synthetic.counter`. Start the two processes manually in that order. This window is not the widget engine.

For CER-M1-001, TelemetryService instead reads CPU total usage from LibreHardwareMonitor once per second and publishes `cpu.total.usage` through the same bus and pipe. WidgetHost subscribes by MetricId and displays the percentage. SyntheticProvider remains available to foundation tests; this slice does not add other hardware metrics.

CER-M1-002 extends that slice with one LHM `Computer` for CPU and discovered GPUs, plus one direct Windows physical-memory read per tick. The provider publishes independent snapshots for CPU, RAM and GPU metrics through the existing bus. The pipe server accepts concurrent sessions, each subscribed to one exact MetricId under protocol v1. WidgetHost opens one connection per displayed metric; it remains a small demo window, not a widget engine.

CER-M1-003 enables Storage and Network on that same `Computer`, captures their discovered devices at startup and publishes the selected normalized metrics in `docs/METRICS.md`. LHM can change those groups at runtime: removed captured devices become unavailable; additions require a service restart before Cerith assigns an index. WidgetHost displays a compact `storage.0`/`network.0` sample alongside the existing metrics; all discovered devices still publish on the bus. This is not a device catalog or a general hotplug system. Hardware/provider health diagnostics remain outside this task and M1 remains incomplete.

CER-M1-004 reports startup discovery counts and per-sample Fresh/Stale/Unavailable/Error totals with sanitized error-code counts in TelemetryService. Counts describe observed publication outcomes, not an independent health verdict. Each `MetricSnapshot` remains the authority for its metric. WidgetHost displays sanitized errors and marks subscriptions unavailable after a receive timeout or disconnect, then retries. Zero discovered devices have an unknown cause; LibreHardwareMonitor internal failures and sensor truth cannot be independently detected here. No diagnostics protocol, metric IDs, logging framework or ControlCenter UI is added.

## Storage

SQLite is the default future local persistence layer for:

- history;
- layouts;
- profiles;
- rules;
- non-secret settings.

Secrets use Windows credential facilities.

## Dependency direction

```text
UI → Core contracts
Providers → Core contracts
Storage → Core contracts
Platform integrations → contracts

Core → no UI/provider implementation
```

Circular dependencies are defects.
