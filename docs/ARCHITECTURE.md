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

Specific transport is decided after M0 benchmark/prototype.

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
