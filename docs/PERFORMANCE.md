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

## CER-M4-001 WidgetHost idle observation

Date: 2026-10-06. Two local 12-second samples measured WidgetHost, one with the window visible and one minimized. Process CPU time was converted to percent of one logical core; working set was recorded during each sample. At 96 DPI, the visible window measured 1.172% CPU and 127.25 MiB working set; minimized measured 0.781% CPU and 127.66 MiB. The unchanged-value path updates rows only when presentation output changes; the observation does not establish the approximately-zero idle CPU target.

The measured working set exceeds the `< 50 MB` minimal always-on target and the `well below 100 MB` common-runtime preference. At the time of CER-M4-001 there was no comparable pre-M4 or bare-WinUI baseline, so that observation did not assign the absolute cost to CER-M4-001. CER-M4-002 below provides that attribution. These are one-machine observations, not product guarantees.


## CER-M4-002 native WidgetHost baseline attribution

Date: 2026-10-06. Verdict: **PERFORMANCE_ACTION_REQUIRED**. The memory gap is dominated by the framework/runtime floor on this host, while current WidgetHost CPU has a reproducible Cerith-attributable component that needs a follow-up before M4 exit.

The protocol used Release builds, a fresh process for every run, 8 seconds of warm-up and a fixed 12-second measurement interval. CPU is `100 * process TotalProcessorTime delta / elapsed wall time`, reported as percent of one logical/core-equivalent CPU. Whole-machine normalized CPU is that value divided by 16 logical processors. Working Set and Private Bytes were sampled once per second during the interval; each run uses the median memory sample, and the table reports the median and range across independent runs. No debugger was attached. WidgetHost processes were closed through their normal window lifecycle between runs; duplicate/stale instances caused the helper to abort.

Host facts: Windows 11 Pro 10.0.26200 build 26200, 16 logical processors, 15.91 GiB physical memory, x64; .NET runtime 10.0.12 / SDK 10.0.401; installed Windows App Runtime 2.5.1 x64. The minimal control is a disposable unpackaged `.NET 10` WinUI 3 application with `Microsoft.WindowsAppSDK 2.5.1`, `WindowsPackageType=None` and `WindowsAppSDKSelfContained=false`. It contains one `Window` with an empty `Grid`, no Cerith/Core reference, telemetry, Named Pipes, timer, animation or background loop. A second disposable control keeps the current CER-M4-001 XAML and three `MetricReadout` rows but does not start any subscriptions; it exists only to separate static UI cost from telemetry-path cost. Neither control is tracked by the repository.

| Condition | Runs | CPU % of one core, median (range) | Working Set MiB, median (range) | Private MiB, median (range) |
| --- | ---: | ---: | ---: | ---: |
| Minimal WinUI visible | 5 | 0.000 (0.000–0.000) | 108.20 (108.05–108.22) | 132.35 (132.14–132.44) |
| Minimal WinUI minimized | 5 | 0.000 (0.000–0.000) | 108.20 (107.87–108.30) | 132.16 (132.01–132.37) |
| Current M4-001 UI only, visible | 5 | 0.000 (0.000–0.000) | 115.81 (115.66–116.24) | 134.31 (134.04–134.66) |
| Current WidgetHost, service absent, visible | 5 | 1.946 (1.167–3.373) | 119.13 (118.97–119.16) | 135.70 (135.46–135.79) |
| Current WidgetHost, live, visible | 8 | 1.102 (0.778–1.427) | 125.89 (125.60–126.15) | 137.89 (137.58–138.12) |
| Current WidgetHost, live, minimized | 5 | 0.908 (0.778–1.166) | 125.96 (125.49–126.03) | 138.04 (136.64–138.25) |
| Immediate pre-M4 WidgetHost `fc2f85e...`, live, visible | 8 | 0.454 (0.259–1.037) | 128.53 (125.78–129.59) | 142.40 (139.99–143.29) |

The live service was the canonical current TelemetryService, and process accounting remained WidgetHost-specific. UI Automation verified the three representative live states during the run: CPU was `Fresh/Measured`, gaming was `Stopped/Unknown` with no selected PID, and Codex quota was `Error/route_unsupported/Unknown`. The pre-M4 WidgetHost was built from `fc2f85eaad588d0fcf807c5836ed184b2a051e90` in an isolated disposable worktree and connected to the same current Named Pipe service. It therefore controls the WidgetHost implementation while keeping the producer/protocol environment fixed; it is not a pure historical full-stack replay.

On this host the bare WinUI/.NET/Windows App SDK floor is about 108 MiB Working Set and 132 MiB Private Bytes. The current static M4-001 UI adds about 7.6 MiB Working Set and 2.0 MiB Private Bytes above that floor while remaining at 0.000% measured CPU. Stable live WidgetHost adds about 17.7 MiB Working Set and 5.5 MiB Private Bytes above the minimal WinUI control. This makes the current `< 50 MB` always-on target structurally incompatible with this framework-dependent WinUI stack on this host, and the `well below 100 MB` common-runtime preference is also below the measured bare process floor. These budgets remain aspirational until an Advisor/user decision changes their scope or value.

CPU attribution differs from memory. The static current UI measured 0.000%, but stable live WidgetHost measured 1.102% of one core. Service absence increased CPU further to 1.946% while no snapshots were available, so the three concurrent connect/timeout/retry loops have a material disconnected cost. Stable live connections use more memory than the disconnected state by about 6.8 MiB Working Set / 2.2 MiB Private Bytes, but use less CPU. Minimizing the live window reduced CPU only modestly (1.102% to 0.908%) and did not materially change memory; this does not prove any specific hidden/rendering mechanism.

CER-M4-001 improved memory relative to the immediate pre-M4 host by about 2.6 MiB Working Set and 4.5 MiB Private Bytes in the pooled visible/live samples. CPU moved in the opposite direction: pooled current M4-001 visible/live median was 1.102% versus 0.454% pre-M4. A second bounded three-run comparison reproduced the ordering (current 1.167%, pre-M4 0.389%). The experiment therefore supports a current WidgetHost telemetry/update-path CPU regression at process level, despite the reduction from 21 pre-M4 subscriptions to 3 representative M4-001 subscriptions. It does not isolate the delta to `MetricPresentation`, UI Automation name mutation, row layout work, Named Pipe delivery, or another specific operation inside that path.

Required follow-up before M4 exit: isolate the current connected update path and disconnected reconnect path with the same process-time protocol, identify the concrete operation(s) responsible for the approximately 1% connected and approximately 2% disconnected CPU, then make the smallest evidence-backed correction. Do not change IPC semantics, WinUI/.NET versions, deployment mode, accessibility, truthful metric states or performance budgets merely to improve the benchmark.

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

## Diagnostic capture performance (future)

Test / Diagnostic Session mode may intentionally collect denser evidence than the normal always-on path, but it still must not become the workload being measured.

When implemented:

- batch or buffer high-frequency writes instead of synchronously persisting every frame sample;
- measure diagnostic-mode CPU, memory and I/O overhead on representative gaming sessions;
- report that overhead with each validation methodology where it can affect the result;
- do not use diagnostic-mode resource usage as a claim about normal always-on runtime;
- do not hide a telemetry disagreement by reducing capture fidelity solely to meet a budget.

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
