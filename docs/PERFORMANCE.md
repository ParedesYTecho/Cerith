# Performance Budget

## Principle

Performance is a feature.

Cerith should not noticeably consume the resources it monitors.

## Initial aspirational budgets

```text
idle CPU: approximately 0%, measured over sustained interval
minimal always-on memory: < 50 MB target
common full runtime: preferably well below 100 MB
gaming overhead: negligible
startup: near-instant
```

These are targets, not claims.

## Cadence

Different sources require different refresh rates.

```text
frame telemetry        high frequency
hardware temperatures  medium
AI quota               low
billing/subscription   very low
```

Never poll AI quota endpoints every second.

## Rendering

Prefer compositor-driven updates.

Avoid rebuilding large trees for numeric changes.

## History

Batch/downsample.

Never synchronously persist every high-frequency frame sample to SQLite.

## Benchmark scenarios

Maintain repeatable cases:

- idle desktop;
- overlay active;
- editor open;
- many providers;
- many widgets;
- high metric subscription count.

Track regressions.

## IPC M0 prototype baseline

Date: 2026-09-25
Environment: Windows 11 Pro, version 10.0.26200 (build 26200); .NET SDK 10.0.401, runtime 10.0.12; target `net10.0-windows10.0.19041.0`.

The temporary BCL-only harness ran a server and client in separate processes. For each transport/payload pair it used two independent runs of 200 warmup and 5000 measured request/echo round trips over an established connection. Every request used the same deterministic payload and a four-byte big-endian length prefix; no JSON or Cerith serialization was involved. Latency is per round trip. The median is the middle sample (mean of the two middle values for this even sample count); p95 uses nearest rank. The payload sizes were 256 and 4096 bytes. Run order was reversed for the second payload to reduce ordering bias.

| Payload | Transport | Median RTT µs, runs 1/2 | p95 RTT µs, runs 1/2 | CPU ms per 5000 RTTs, client/server/combined, runs 1/2 | RTT/s, runs 1/2 |
| --- | --- | ---: | ---: | ---: | ---: |
| 256 B | Named Pipes | 80.900 / 77.900 | 127.900 / 121.400 | 906.250/734.375/1640.625 · 593.750/812.500/1406.250 | 11480 / 11945 |
| 256 B | TCP loopback | 80.600 / 79.300 | 119.500 / 114.700 | 390.625/546.875/937.500 · 578.125/468.750/1046.875 | 11634 / 11955 |
| 256 B | AF_UNIX | 44.100 / 44.900 | 81.300 / 82.700 | 421.875/296.875/718.750 · 343.750/343.750/687.500 | 21953 / 21491 |
| 4096 B | Named Pipes | 79.000 / 80.400 | 125.000 / 123.400 | 687.500/640.625/1328.125 · 671.875/796.875/1468.750 | 11727 / 11568 |
| 4096 B | TCP loopback | 78.500 / 85.700 | 127.700 / 124.600 | 421.875/640.625/1062.500 · 437.500/671.875/1109.375 | 11486 / 11008 |
| 4096 B | AF_UNIX | 45.200 / 45.600 | 82.200 / 81.800 | 421.875/296.875/718.750 · 328.125/437.500/765.625 | 21835 / 21948 |

CPU is `Process.TotalProcessorTime` around each process's measured interval; combined is client plus server CPU. Server sampling brackets the measured request stream using control frames. Values include .NET async/runtime and harness overhead. Throughput is measured round trips divided by the client's measured wall time.

Functional checks passed twice for each candidate: local connection, two clean disconnects and reconnects, cancellation of an outstanding async read after 400 ms, and cleanup. TCP bound only to `127.0.0.1` on an ephemeral port; its port was handed to the child client by the parent process. Named Pipes used asynchronous byte mode with `CurrentUserOnly` at both ends. AF_UNIX ran on a 70-character endpoint path; Windows reported five inherited ACL rules, zero explicit rules, no allow rule for Everyone/Authenticated Users/Builtin Users, and an inheritance-enabled DACL. The socket path was removed after shutdown.

This is a prototype measurement, not a performance guarantee. Results are two runs of 5000 samples per cell on one machine without CPU pinning or background-load control; timer/scheduler noise and process CPU accounting affect the values. `CurrentUserOnly` was configured, but cross-account access was not tested under a second account. The runs compare this framing and async BCL implementation only, not future Cerith serialization, protocol, startup, reconnect latency, or production contention. No microsecond pass/fail threshold is implied.

## CER-M2-001 local PresentMon probe

Date: 2026-09-29. A temporary D3D11 process presented frames on the local display; non-elevated TelemetryService used PresentMon Service/API 2.6.0 / API 3.4.0 and published both gaming MetricIds through Named Pipe v1. Across 45 FPS snapshots, effective poll intervals were 496–502 ms (median 500 ms). Fresh displayed FPS varied from about 158 to 165 in the final run; displayed frame time was about 6.0–6.3 ms. UI Automation read `164.8 FPS` and `6.07 ms` from WidgetHost. After closing the target, both streams changed to `Stopped`/no value by the next 500 ms poll.

With the target active, TelemetryService used 0.188 CPU seconds over a 12.0 second sample (1.56% of one logical core); its working set moved from 79.6 to 80.5 MiB. PresentMonService working set was 27.9 MiB at both ends of a separate 12 second sample. `Get-Process` reported 0.000 CPU seconds for PresentMonService in that interval; treat this as below the counter's useful resolution, not proof of zero CPU. These TelemetryService figures include the existing hardware provider and are not an isolated PresentMon cost. No controlled game-performance comparison was run.

When PresentMonSharedService was stopped during the same process, the two gaming metrics became `Unavailable` while `cpu.total.usage` continued arriving as `Fresh`/`Measured`; restoring the service returned the gaming metrics to Fresh without restarting TelemetryService or WidgetHost. This is a single-host Windows measurement, not a universal performance or capture guarantee.
