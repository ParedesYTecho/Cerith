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

## AI telemetry acquisition

AI observability has multiple acquisition mechanisms but one committed runtime publication path in the current architecture:

```text
source
  → provider/collector normalization
  → MetricSnapshot
  → MetricBus
  → IPC
  → MetricId-driven consumers
```

Quota/allowance sources may be provider APIs, official-client sources, CLIs or local RPCs. Local activity sources may be passive logs/databases, structured events or narrowly scoped hooks. Those acquisition mechanisms do not create a second shared runtime contract.

M7 may later require a richer persistence/history event contract for detailed sessions, projects or model breakdowns. If so, design and record that contract explicitly at M7; M3 collectors must not bypass MetricBus, write directly to a dashboard, or smuggle an undeclared event schema through JSON strings.

Do not force both acquisition classes through one speculative provider framework. Provider-specific code may remain separate until repeated concrete implementations prove a smaller shared primitive is worthwhile.

Quota credentials and local activity sources have different privacy/lifecycle rules. A hook that reports local token counters is not authorization to query a subscription endpoint; a quota token is not permission to ingest conversation content.

Provider implementation should be physically isolatable enough that bounded workers can own one provider without editing surface/rendering code. Shared integration files, secure-secret storage and any reusable auth/setup primitive require one integration owner.

## Cerith.WidgetHost

Lightweight always-on runtime.

Future responsibilities:

- desktop widgets;
- compact floating monitoring surfaces;
- taskbar widgets/panels;
- tray indicators;
- overlays;
- expanded/detail inspection surfaces.

All surfaces consume normalized MetricId state. Compact views may choose density/headline presentation policy, but provider acquisition/auth logic never belongs in WidgetHost.

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

CER-M1-004 reports startup discovery counts and per-sample Fresh/Stale/Unavailable/Error totals with sanitized error-code counts in TelemetryService. Counts describe observed publication outcomes, not an independent health verdict. Each `MetricSnapshot` remains the authority for its metric. At that milestone, WidgetHost marked subscriptions unavailable after a receive timeout or disconnect, then retried. Zero discovered devices have an unknown cause; LibreHardwareMonitor internal failures and sensor truth cannot be independently detected here. No diagnostics protocol, metric IDs, logging framework or ControlCenter UI is added.

CER-M2-001 adds a second concrete acquisition lifecycle in TelemetryService. It tracks only the explicit `--pid` target through the official PresentMon Service/API, normalizes displayed FPS and frame time to the existing `MetricSnapshot` contract, and publishes on the existing MetricBus and Named Pipe v1. WidgetHost remains MetricId-only. A narrow native capture boundary owns PresentMon session/query handles and supports deterministic provider tests; PresentMon types and binaries do not enter Core. Service/API absence does not stop hardware acquisition. The pinned API, loader provenance and state rules are recorded in D-020 and `docs/METRICS.md`.

CER-M4-005 keeps `MetricRegistry` as the single semantic source and extends v1 subscription frames with optional descriptor metadata. `accepted` includes a descriptor already registered; a descriptor registered later accompanies the first later snapshot, including valueless states. Missing metadata does not reject or delay an exact bus subscription. Metadata is sent once per pipe, and WidgetHost uses the MetricId as a truthful label fallback while waiting. After acceptance, stream silence alone does not mean the service disconnected: WidgetHost waits for a snapshot or pipe closure, leaving metric state transitions to the provider. The three exact-MetricId pipes and CER-M4-004 availability-attempt coordination remain independent.

## WidgetHost local composition (CER-M4-006)

The native surface creates one `MetricSubscription` for each distinct exact MetricId requested by its components. That subscription owns one client/read loop and the current descriptor/snapshot; local consumers receive the same committed state on the WinUI context. `MetricReadout` and `MetricBar` can therefore consume one stream simultaneously. Different MetricIds still use independent pipes; local fan-out is not IPC multiplexing. The window owns cancellation and awaits all loops/client disposal before detaching consumers and closing. Removing one event consumer does not cancel other consumers; subscription lifetime remains the window's lifetime for this fixed composition.

`NamedPipeMetricClient` bounds each transport-only connection probe to a positive 1 ms timeout and yields with a cancellable 100 ms delay after transport timeout. This avoids the runtime's long missing-pipe spin; busy-instance availability can incur the same polling delay. Subscribe/ACK is not retried or given that short timeout: the existing subscription's five-second linked token covers transport and acceptance together, followed by its one-second outer retry. No deadline reset, additional process, shared telemetry pipe or protocol change is introduced.

`MetricConnectionAttempt` shares only availability-attempt coordination. A pending attempt releases followers on success/failure/cancellation, and generation checks prevent old completion from restoring invalidated availability. Individual consumer exceptions are diagnosed locally and do not reconnect telemetry. Acceptance replaces the descriptor, including null, and clears the previous snapshot before Waiting; late metadata is committed before its associated snapshot notification. Transport loss clears the active value. Providers remain authoritative for metric states while an accepted pipe stays open.

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

## Component-private sparkline observations (CER-M4-012)

The native sparkline is a consumer of the existing exact-ID subscription and shared readout/explicit range. Its private sequence holds at most60 distinct Fresh finite in-range observations during that component's lifetime. This is M4 presentation state: the bus, Core and MetricSubscription do not retain or replay observations; no persistence/history/query/public contract is introduced.

X uses actual ObservedAt instants relative to the retained first/last observation. Equal instants (including equivalent offsets) replace the last sample; backward time, source/confidence or descriptor semantic/unit/kind changes reset. Null/Waiting/loss and valueless/Unlimited/nonnumeric/nonfinite/out-of-range states clear the sequence. Valid Stale freezes/dims previously received Fresh points without adding its value; the next Fresh starts a new sequence. Silence on an accepted transport never invents a timeout or a state transition. Source is optional provenance, not a session identity; undetectable replacement under identical metadata/source cannot be inferred.

The oldest sample is evicted only when admitting the61st distinct point; this local presentation bound does not drop IPC messages or change backpressure. Fewer than two points render no line. Native segments connect retained observations without smoothing, extrapolation or claims about intermediate measurements. The current readout, range, count and observed span remain visible/accessibly named. Geometry redraws on admitted changes or native size changes; repeated identical observations do not rebuild the points. Native/performance and final integration gates are separate from this contract review.
