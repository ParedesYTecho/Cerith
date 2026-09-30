# Universal Metric Contract

## Principle

Every measurement in Cerith must be representable through one core metric model.

## Metric IDs

Metric IDs are immutable, open-ended strings. Preserve their exact text and compare them ordinally and case-sensitively. Reject null, empty, whitespace-only, and leading/trailing whitespace; do not lowercase IDs or impose a speculative grammar.

Use hierarchical strings as a convention:

Examples:

```text
cpu.total.usage
cpu.package.temperature
gpu.0.temperature.core
gpu.0.power.board
ram.used.bytes
game.fps.display
game.frametime.p99

ai.claude.session.remaining
ai.claude.weekly.remaining
ai.codex.quota.primary.remaining
ai.codex.quota.secondary.remaining
ai.codex.quota.primary.reset
ai.codex.quota.secondary.reset
ai.codex.quota.primary.duration
ai.codex.quota.secondary.duration
```

Do not use a closed enum because metrics need to be addable without recompiling Core.

The six Codex quota IDs are registered by TelemetryService; their semantics and conformance tests are in [PROVIDERS_AI_QUOTAS.md](PROVIDERS_AI_QUOTAS.md). Other AI examples remain prospective. No fixed Antigravity weekly window is assumed.

## Descriptor

Descriptors are provider-independent. A descriptor identifies the semantic metric; concrete source information belongs to each snapshot.

Current fields:

```text
Id
DisplayName
Description?
SemanticType
Unit?
ValueKind
```

## Registry

`MetricRegistry` keeps one in-memory `MetricDescriptor` per `MetricId`. The first registration wins; duplicate IDs do not replace it. Lookup returns the stored descriptor, and catalog enumeration returns a read-only point-in-time snapshot. Operations are thread-safe. The registry stores descriptors only, not measurements or snapshots.

## Bus

`MetricBus` fans out snapshots to multiple independent subscribers by exact `MetricId`. A subscription receives only publications made after activation. Each subscriber has a bounded buffer; a full buffer applies backpressure rather than silently dropping snapshots. Cancellation or disposal ends a subscription, and disposing the bus completes active readers. The bus has no replay, latest-value cache, or dependency on `MetricRegistry`.

## Snapshot

Snapshots are immutable. `Value` is optional and uses a typed value kind, not an arbitrary object or JSON value. Supported kinds are `double`, `long`, `decimal`, `bool`, `string`, `DateTimeOffset`, and `TimeSpan`.

```text
MetricId
Value?
ObservedAt: DateTimeOffset
State: Fresh | Stale | Unavailable | Stopped | Error | Unlimited
Confidence: Unknown | Measured | Estimated
Source?
ErrorCode? (sanitized code only)
```

`MetricState` describes availability/freshness; `MetricConfidence` describes whether the value is measured or estimated. These dimensions are independent, so stale values retain either measured or estimated confidence.

Age is derived as `reference_time - ObservedAt`; it is not stored in the snapshot. A stale snapshot retains its observation timestamp so callers can determine its age.

## Value and state rules

- `Fresh` and `Stale` require a value.
- `Unavailable`, `Stopped`, and `Error` contain no value.
- `Unlimited` contains no value; the state represents the absence of a finite limit.
- A snapshot without a usable value has `Unknown` confidence.
- Zero is a real value; absence is represented separately.
- Unlimited is not 100%, and an error is not 0%.
- Estimated remains visible when the value becomes stale.
- Error details use a short sanitized code, never an exception or raw provider message.

## Semantic types

Examples:

```text
percentage
temperature
power
frequency
rate
duration
timestamp
bytes
count
currency
quota_remaining
quota_used
balance
subscription_status
activity
boolean
text
```

A currency balance must not be rendered as generic percentage quota unless a real denominator exists.

## Derived metrics

Derived metrics are first-class and record their inputs.

Example:

```text
gpu.vram.usage_percent =
gpu.vram.used / gpu.vram.total * 100
```

## Sampling

Sampling cadence belongs to provider/refresh policy, never to individual widgets.

One sample can feed:

- text;
- chart;
- rule;
- history;
- multiple widgets.

CER-M1-001 uses `cpu.total.usage` as a provider-independent percentage (`double`, `%`). A finite reading in the 0–100 domain is `Fresh`/`Measured`; a missing reading is `Unavailable`/`Unknown`, and a failed refresh or invalid reading is `Error`/`Unknown` with a sanitized code. No missing or invalid reading is published as zero.

CER-M1-002 adds the following measured metrics. RAM uses one `GlobalMemoryStatusEx` physical-memory sample: `ram.total.bytes` and `ram.available.bytes` are the API values, `ram.used.bytes = total - available`, and `ram.usage = 100 × used / total`. Byte values are `long`; usage is `double`. A zero total, inconsistent available value, or failed API call produces `Error` without a value. This baseline does not activate LHM's Memory/SPD group.

For each discovered GPU, `gpu.N` is assigned by ordinal sorting of LHM 0.9.6 `IHardware.Identifier.ToString()` at startup. Identifiers remain internal and duplicate identifiers fail discovery. This order is deterministic for the identifiers discovered in that startup. LHM derives AMD and NVIDIA identifiers from adapter indices and uses different schemes for Intel integrated and discrete GPUs; stable association of `gpu.N` with the same physical device across reboot, driver changes, adapter reenumeration or topology changes is unproven. Multiple GPUs have not been physically validated. Persistent device identity remains a later-slice concern; these provider-specific identifiers are not part of MetricId. The provider publishes:

| MetricId suffix | LHM sensor (exact type + name) | Unit / value kind |
| --- | --- | --- |
| `.usage` | Load / GPU Core | % / double |
| `.temperature.core` | Temperature / GPU Core | °C / double |
| `.clock.core` | Clock / GPU Core | MHz / double |
| `.vram.used.bytes` | SmallData / GPU Memory Used | B / double |
| `.vram.total.bytes` | SmallData / GPU Memory Total | B / double |
| `.fan.rpm` | exactly one Fan sensor | RPM / double |

LHM 0.9.6 defines `SmallData` as MiB (2²⁰ bytes); the provider multiplies by 1,048,576 before publishing. GPU sensor availability varies by device. A missing sensor/value is `Unavailable` without a value; multiple matching sensors are `Error` (`ambiguous_sensor`). Refresh failure or an invalid reading is `Error` without a value. Percentages must be finite and within 0–100; clock, byte and fan values must be finite and nonnegative; temperature must be finite. Values are never clamped or replaced by zero.

CER-M1-003 adds storage and network metrics from the same LHM 0.9.6 `Computer`. The mappings below use exact sensor type and name in [StorageDevice](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3370efb858411f19511373eff65a218701/LibreHardwareMonitorLib/Hardware/Storage/StorageDevice.cs), [Network](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3370efb858411f19511373eff65a218701/LibreHardwareMonitorLib/Hardware/Network/Network.cs), and the [sensor unit contract](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3370efb858411f19511373eff65a218701/LibreHardwareMonitorLib/Hardware/ISensor.cs).

| MetricId | LHM sensor (type / name) | Published unit |
| --- | --- | --- |
| `storage.N.usage` | Load / Used Space | % |
| `storage.N.total.bytes` | Data / Total Space | B |
| `storage.N.free.bytes` | Data / Free Space | B |
| `network.N.download.bytes_per_second` | Throughput / Download Speed | B/s |
| `network.N.upload.bytes_per_second` | Throughput / Upload Speed | B/s |
| `network.N.usage` | Load / Network Utilization | % |

The general LHM `ISensor` comment defines `Data` as 2³⁰ bytes, but `StorageDevice` creates `Total Space` and `Free Space` through [BlackSharp.Core 1.0.7 `ByteToGigabyte`](https://github.com/Blacktempel/BlackSharp/blob/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0/BlackSharp.Core/Converters/DataStorageSizeConverter.cs), which divides by 10⁹. Cerith therefore multiplies **storage** Data readings by 1,000,000,000 to recover bytes. Network `Throughput` is already bytes/s. Percentages must be finite and within 0–100; byte counts and rates must be finite and nonnegative; storage total must be positive. Missing sensors/values are `Unavailable`, ambiguous matches and invalid readings are `Error`, and no missing/error value becomes zero. LHM 0.9.6 can turn a missing `TotalFreeSize` into a `Free Space` reading of zero; Cerith accepts that reading only when `Used Space` has a value. A real zero remains measured zero.

For each kind, `N` is the ordinal index after sorting LHM `IHardware.Identifier.ToString()` at startup. Duplicate identifiers fail discovery. LHM storage identifiers combine the device class (`nvme`, `ssd`, `hdd`) with drive number; network identifiers contain the interface ID. These provider details remain internal. The ordering is deterministic for the discovered set in one startup; physical-device identity across reboot, re-enumeration, rename or interface replacement is unproven. [StorageGroup](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3370efb858411f19511373eff65a218701/LibreHardwareMonitorLib/Hardware/Storage/StorageGroup.cs) and [NetworkGroup](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3370efb858411f19511373eff65a218701/LibreHardwareMonitorLib/Hardware/Network/NetworkGroup.cs) can add/remove hardware after discovery. Cerith publishes `Unavailable` for a removed captured device; newly added devices, including a re-added interface, wait until service restart for an index. An interface that stays enumerated may still report valid zero throughput while disconnected. Network includes LHM's physical and virtual interfaces and filters, excluding its loopback/tunnel/unknown types; Cerith does not aggregate them.

Storage read/write throughput is deferred pending validation and source review. LHM 0.9.6 exposes nominal `Throughput / Read Rate` and `Write Rate` sensors through `IOCTL_DISK_PERFORMANCE`, but the non-elevated host discovered no Storage hardware and LHM issue #1574 records missing/incorrect DLL readings. A later elevated run discovered Storage and validated only usage/total/free; it did not reinstate throughput. `Total Activity` is disk I/O activity, not capacity usage. `storage.N.temperature` is deferred: LHM uses different NVMe/SSD/HDD names and can produce zero from absent temperature data. SMART, life/health, lifetime totals and accumulated network totals are outside this baseline. LHM's network update may retain a value after a handled interface error; this baseline cannot independently detect that internal failure.

## M1 hardware diagnostics

TelemetryService prints startup counts for discovered CPU, GPU, Storage and Network hardware, followed by aggregate per-sample state counts and known sanitized error-code counts. A zero count means LHM did not discover that category; it does not prove the machine lacks it or identify permissions as the cause. The aggregate is printed when state or code counts change. It is an operational diagnostic, not a new metric or provider health verdict. A metric's own `MetricSnapshot` remains authoritative: `Fresh` with `Measured` includes a value (including real zero), `Unavailable` and `Error` contain no value, and known failures carry sanitized codes. Missing hardware may produce no metric ID at all; missing sensors on discovered hardware produce `Unavailable` snapshots. The console and WidgetHost expose no provider-specific hardware identifiers or raw exceptions. WidgetHost displays an error code where one exists and changes a disconnected or silent subscription to `unavailable` before retrying.

LHM may catch errors internally or retain a prior sensor value. Cerith cannot identify such readings as erroneous or prove physical sensor truth from these diagnostics. Startup discovery does not track new devices; existing hotplug and persistent-identity limits still apply.

An elevated CER-M1-004 probe of the unchanged service and LHM 0.9.6 discovered four Storage devices. The existing local pipe published `storage.0.usage`, `storage.0.total.bytes` and `storage.0.free.bytes` as `Fresh`/`Measured` doubles: usage was about 33.8%, total positive, and free between zero and total. `100 × (1 - free/total)` agreed with usage within 0.000002 percentage points. The provider's Storage `Data` conversion remained ×1,000,000,000 bytes; no read/write throughput MetricIds were added. The same probe without elevation discovered zero Storage. This establishes a positive elevated path here, not non-elevated Storage support or independent sensor accuracy. Missing/error snapshots still have no value under the existing Core contract; absent hardware can emit no Storage snapshots at all.

## Future hardware metric design constraints (not implemented)

The hardware catalog may expand after the current milestone sequence, but future IDs are not committed by this section.

Fan telemetry must distinguish physical observation from control intent:

- RPM is an observed rotational speed when the provider exposes a valid Fan sensor;
- a fan control/duty percentage is the provider-reported control level, not “percent of maximum RPM” unless a provider explicitly defines it that way;
- GPU fan telemetry may expose both quantities independently;
- a CPU-fan concept is registered only when the source can identify the CPU_FAN channel without guessing;
- ambiguous motherboard fan channels are not automatically classified as CPU, chassis or PSU;
- fan actuation/curves are outside the current read-only metric contract.

Additional future CPU/GPU candidates include verified power, voltage, clocks, temperatures and vendor sensors. Their MetricIds, source mappings and units must be defined only when the relevant implementation slice validates them.

## Displayed gaming metrics (CER-M2-001)

| MetricId | PresentMon source | Statistic | Semantic type / value / unit |
| --- | --- | --- | --- |
| `game.fps.display` | `PM_METRIC_DISPLAYED_FPS` | `PM_STAT_AVG` over 1000 ms | rate / `double` / FPS |
| `game.frametime.display` | `PM_METRIC_DISPLAYED_FRAME_TIME` | `PM_STAT_AVG` over 1000 ms | duration / `double` / ms |
| `game.fps.display.p01` | `PM_METRIC_DISPLAYED_FPS` | `PM_STAT_PERCENTILE_01` over a requested trailing 10,000 ms | rate / `double` / FPS |

`game.fps.display` maps to `PM_METRIC_DISPLAYED_FPS`: the rate at which new frames are displayed on screen (PresentMon v2.6.0 defines Displayed FPS as “rate of frame change measurable at display”). It is not `PM_METRIC_APPLICATION_FPS`. Application-versus-generated-frame classification and frame-generation distinctions are deferred and are not claimed by CER-M2-001. These definitions follow the pinned [PresentMon v2.6.0 metric definitions](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/README-CaptureApplication.md) and its [API metric enums](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonAPI2/PresentMonAPI.h).

`game.fps.display.p01` uses PresentMon's `PM_STAT_PERCENTILE_01` for the same displayed-FPS metric over a requested trailing 10-second window. In v2.6.0, PresentMon emits a zero `msBetweenDisplayChange` sample for a non-displayed frame and retains finite zero samples in the percentile input ([display metric calculation](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/CommonUtilities/mc/MetricsCalculatorDisplay.cpp#L23-L33), [dynamic sample collection](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/DynamicMetric.h#L64-L72)). It reverses the percentile rank for reciprocal metrics, linearly interpolates the 99th percentile of those interval samples, then takes the reciprocal ([percentile and reciprocal handling](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/DynamicStat.cpp#L157-L206), [rank selection](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/DynamicStat.cpp#L311-L315)). A selected zero interval cannot be reciprocated and produces no value. This estimator is not the arithmetic mean of the slowest 1% of frames or an integral 1% low; Cerith does not remove dropped-frame zero samples or calculate a second percentile. In the controlled high-drop validation, `pmConsumeFrames` returned positive `PM_METRIC_BETWEEN_DISPLAY_CHANGE` intervals while a separate 10-second `PM_METRIC_DROPPED_FRAMES` average measured the dropped fraction; the raw oracle reconstructed the corresponding zero-sample count from that fraction before calculating P99. Cerith publishes no P1 value until 10 seconds have elapsed from the first positive displayed-change observation in the capture, and requires positive display evidence no more than two seconds old. A gap longer than two seconds resets warm-up; capture or target restart also starts a new warm-up. The P1 snapshot has its own state, so warm-up, invalid values or a P1-query error do not invalidate the one-second displayed-FPS and frame-time metrics. Multiple rows in the P1 query produce valueless `Error/multiple_swap_chains`.

The requested 10-second interval is not a guarantee that PresentMon retained 10 seconds of frame metrics. Its per-swap-chain frame-metric ring holds 4096 samples, so retained history may be shorter at rates above roughly 410 frames per second. Cerith does not gate publication on `PM_STAT_COUNT`; PresentMon v2.6.0 does not implement that statistic for dynamic queries. The adapter uses PresentMon's percentile result rather than duplicating its percentile calculation.

PresentMon v2.6.0's `pmPollDynamicQuery` output count is a per-poll result-row count, not an inventory of active swap-chain objects. Its implementation treats the caller's input count as the maximum row capacity, builds rows from swap chains with frame samples in the query time window, and returns one fallback row when that set is empty ([`FrameMetricsSource`](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/FrameMetricsSource.cpp#L347-L377), [`DynamicQuery`](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/DynamicQuery.cpp#L470-L500)). TelemetryService polls the dynamic query about every 500 ms. It also consumes the frame query for `PM_METRIC_PRESENT_START_QPC` and `PM_METRIC_BETWEEN_DISPLAY_CHANGE`; a reading is `Fresh`/`Measured` only when the API returns exactly one result row, both averages are finite and positive, and a positive display-change sample has a QPC timestamp no more than two seconds old. This prevents a cached dynamic-query value from appearing Fresh after frame events stop. A poll reporting more than one result row is `Error` (`multiple_swap_chains`) because this baseline does not choose or combine chains; a later poll reporting exactly one may recover to `Fresh` when valid samples resume, without restarting capture.

No target PID and a terminated target publish `Stopped` without values. A selected target with no usable display sample, including the API's zero/no-frame result, publishes `Unavailable` without values. An absent service or capability is also unavailable; operational API failures and invalid/non-finite values publish `Error` with a short sanitized code. No missing frame or error is converted to FPS or frame-time zero. Target selection is the explicit ephemeral `--pid` input; process paths, titles, command lines and PID values are not emitted by Cerith. PresentMon's ETW flush period is left unchanged.

## Selected game/session summary (CER-M2-003)

`game.session.application` is a string (`text`, no unit) describing only the one process selected by the explicit `--pid` argument. Windows `Process.ProcessName` supplies the executable name without its path or `.exe` extension ([Microsoft documentation](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.processname?view=net-10.0)). The snapshot source is `Windows`. Cerith does not publish the PID, executable path, command line, window title, package identity, process tree, or an automatically discovered process. WidgetHost subscribes by this MetricId and renders it as `Selected game`.

The session is the lifetime of that selected operating-system process, not the lifetime of a PresentMon capture. TelemetryService retains the same `Process` object/handle across capture close/retry and checks that target before and after polling. It never binds a later process that happens to reuse the numeric PID. No `--pid`, an initially missing target, or observed target exit publishes valueless `Stopped` for this metric and the gaming metrics. While the process is alive and its name is available, this metric remains `Fresh`/`Measured` even when PresentMon is unavailable, has no displayed frames, or is retrying. Frame availability and session identity therefore remain independent.

If Windows cannot read the process name, this metric is `Unavailable` without a value; malformed name data is `Error/invalid_application_name`, and other metadata failures are `Error/application_metadata_failure`. Failure to acquire the initial process handle is terminal for that configured `--pid`: the session publishes `Error/target_identity_failed` until TelemetryService restarts, because a later PID lookup cannot prove it found the same process. A later process-status query failure is `Error/target_status_failed` while the retained handle remains the identity anchor. These states do not convert a readable process name or valid frame metrics into zero. The metric uses the existing MetricBus, Named Pipe v1 and MetricId-only WidgetHost path; Core and the wire protocol do not change.


## Future gaming metric separation (not implemented)

Future gaming work should preserve distinct provider semantics rather than collapsing multiple rates into one generic FPS value. Candidate concepts include Application FPS, Displayed FPS, Presented FPS, 0.1% low, frame pacing, dropped/repeated frames, frame type/generated-frame information and advanced latency.

PresentMon v2.6.0 exposes separate Application, Displayed and Presented FPS metrics plus frame type information. Future Cerith slices must validate the exact provider semantics before fixing MetricIds or deriving relationships between them. In particular, Cerith must not infer “generated FPS” by subtracting one FPS rate from another unless the provider's frame classification makes that derivation valid.
