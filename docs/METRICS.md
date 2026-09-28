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
ai.codex.session.remaining
ai.antigravity.gemini.weekly.remaining
```

Do not use a closed enum because metrics need to be addable without recompiling Core.

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

Storage read/write throughput is deferred pending validation and source review. LHM 0.9.6 exposes nominal `Throughput / Read Rate` and `Write Rate` sensors through `IOCTL_DISK_PERFORMANCE`, but this host discovered no Storage hardware and LHM issue #1574 records missing/incorrect DLL readings. `Total Activity` is disk I/O activity, not capacity usage. `storage.N.temperature` is deferred: LHM uses different NVMe/SSD/HDD names and can produce zero from absent temperature data. SMART, life/health, lifetime totals and accumulated network totals are outside this baseline. LHM's network update may retain a value after a handled interface error; this baseline cannot independently detect that internal failure.
