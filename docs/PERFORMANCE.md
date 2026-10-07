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

## CER-M4-003 WidgetHost CPU path isolation

Date: 2026-10-06. Verdict: **PERFORMANCE_CAUSE_ISOLATED_FIX_DEFERRED**. The connected cost has a measurable receive/scheduling floor and a second cost when the WinUI row is applied; the changing CPU value drives almost every row application. Disconnected CPU scales with concurrent pending `ConnectAsync` attempts. No product code or performance budget changed. The measured local alternatives either did not improve CPU consistently or traded CPU for a longer possible service-recovery wait. Changing the rendering strategy or connection topology needs a separate M4/Advisor decision.

This study reused CER-M4-002's external process-time harness: Release, fresh WidgetHost process per run, 8-second warm-up, 12-second CPU interval, one-second Working Set/Private Bytes samples, five runs per condition, no debugger. CPU is percent of one logical/core-equivalent CPU, not whole-machine percent. All CER-M4-003 decisive runs opened Cerith on secondary `\\.\DISPLAY2` (1280x720); the harness moved the window before warm-up and checked the monitor before and after visible sampling. Minimized runs checked placement before minimizing. The disposable variants copied the current WidgetHost sources outside the repository and kept the same three real Named Pipe subscriptions unless the row below says otherwise. Counters were kept in memory and written only on normal close. Counter totals cover about 20 seconds per process, including warm-up; CPU percentages cover only the following 12-second interval. Raw logs and variants remain outside the repository.

| Connected condition on DISPLAY2 | Runs | CPU % of one core, median (range) | Working Set MiB | Private MiB |
| --- | ---: | ---: | ---: | ---: |
| Receive and validate only; no presentation/UI | 5 | 1.038 (0.778–1.168) | 126.88 | 138.39 |
| Receive plus `MetricPresentation.Create`; no UI | 5 | 1.038 (1.038–1.557) | 126.85 | 138.20 |
| Visible row updates, without `AutomationProperties.SetName` | 5 | 1.946 (1.686–2.335) | 126.93 | 138.38 |
| Full path, disposable instrumented host | 5 | 2.206 (1.686–2.465) | 127.11 | 138.36 |
| Untouched canonical WidgetHost, visible | 5 | 2.074 (1.946–2.204) | 125.88 | 137.96 |
| Untouched canonical WidgetHost, minimized | 5 | 1.556 (1.426–2.204) | 125.90 | 138.16 |

The five-run counter totals were 405 snapshots in receive-only and 408 in each other three-subscription stage. The full stage produced 408 presentations but applied the row only 112 times; the no-Automation-name stage applied it 111 times. Per-metric full-stage snapshots were CPU/FPS/Codex = 102/204/102, approximately 1/2/1 per second across each process. Thus the existing identical-presentation guard works. Formatting added no detectable median CPU. Applying the row added a substantial cost even without Automation name mutation; the additional name-mutation difference overlaps the run ranges and is not a reason to remove accessibility.

| Full UI, one real subscription | Runs | CPU % of one core, median (range) | Snapshots over five process lifetimes | Row applications |
| --- | ---: | ---: | ---: | ---: |
| `cpu.total.usage` | 5 | 2.594 (2.205–2.851) | 103 | 103 |
| `game.fps.display` | 5 | 0.778 (0.649–0.908) | 205 | 5 |
| `ai.codex.quota.primary.remaining` | 5 | 0.778 (0.649–1.037) | 102 | 5 |
| All three | 5 | 2.206 (1.686–2.465) | 408 | 112 |

The single-metric results associate most changing-row work with CPU, while FPS arrives twice as often but normally presents the same stopped state. They do not form additive per-metric costs: CPU-only exceeded the earlier all-three median, showing temporal host variation. The untouched canonical visible median was 2.074% in this session versus CER-M4-002's 1.102%; compare conditions within this session rather than treating either session as a universal idle number.

Two further disposable UI ablations tested simple fixes without touching product code. A field-by-field equality check kept the accessibility name current but avoided redundant text/state/confidence/opacity assignments. In five alternating full/selective pairs, the full-minus-selective CPU differences were +0.905, +0.129, +0.648, -0.389 and +0.001 percentage points: the direction was not consistent. Suppressing only the visible value assignment in another five pairs likewise varied from -0.389 to +1.428 points. Neither establishes a reliable single-property correction; suppressing a real changing value is not acceptable product behavior.

| Service absent on DISPLAY2 | Runs | CPU % of one core, median (range) | Attempts / timed-out failures over five process lifetimes | Aggregate time inside `ConnectAsync` |
| --- | ---: | ---: | ---: | ---: |
| One subscription; 5 s connect, 1 s outer delay | 5 | 1.425 (0.648–1.554) | 20 / 15 | 87.2 s |
| Three subscriptions; current 5 s connect, 1 s delay | 5 | 3.484 (2.460–5.308) | 60 / 45 | 261.5 s |
| Three; 5 s connect, 5 s delay | 5 | 1.941 (1.682–3.754) | 45 / 30 | 156.0 s |
| Three; 5 s connect, 10 s delay | 5 | 1.296 (0.130–2.463) | 30 / 30 | 150.1 s |
| Three; 500 ms connect, 1 s delay | 5 | 2.722 (2.204–3.370) | 210 / 210 | 105.5 s |
| Three; 1 s connect, 1 s delay | 5 | 3.242 (2.590–4.927) | 165 / 150 | 156.9 s |

The aggregate connection time sums concurrent subscription waits, so it may exceed process wall time. Absent-server failures normally timed out rather than returning immediately. Longer outer delays reduced both time spent in pending connections and CPU. The 500 ms condition created far more cancellation exceptions yet used less CPU than the current five-second wait, so exception construction is not the dominant cause. `Debug.WriteLine` calls are conditional and omitted from the Release build; no debugger was attached. The current one-second outer delay contributes to how often a new five-second pending connect starts, but the pending connection occupies most disconnected time. A longer delay can add up to that delay to service recovery, and the shorter connect timeouts have overlapping CPU ranges and less margin than the observed 100–200 ms healthy subscribe handshakes. No retry or timeout policy was adopted solely for its benchmark result.

One native lifecycle check on DISPLAY2 observed real CPU `Fresh/Measured`, FPS `Stopped/Unknown` without a selected PID, and Codex `Error · route_unsupported/Unknown`; all three became `Unavailable/No snapshot` 318 ms after stopping the service, then CPU returned to Fresh 2069 ms after launching the service again. The widget closed normally. These are single transition timings, not a recovery-latency distribution. A profiler trace was not collected: `dotnet-trace` was unavailable, and although Windows Performance Recorder was present, no cheap stack-analysis path was available. No stack-level attribution is claimed.

The correction gate remains deferred. Independent Advisor review accepted `PERFORMANCE_CAUSE_ISOLATED_FIX_DEFERRED` as cost-category isolation, not an exact profiler-stack root cause. The property-level guard did not show a stable gain; no product fix was adopted. There is no post-fix before/after claim, no added dependency, project, or abstraction, and M4 remains ACTIVE / INCOMPLETE.

For the next product task only, the Advisor permits experimental evaluation of bounded process-level reconnect coordination, such as single-flight or serialized connection attempts: evaluate at most one expensive absent-service `ConnectAsync` attempt at a time. Existing MetricId-per-subscription/per-pipe semantics remain unchanged; after service availability is restored, individual subscriptions still establish independent connections. This is reconnect coordination; shared/multiplexed telemetry data connections are not authorized. Measure recovery from a clearly observed TelemetryService-ready point to restored WidgetHost data against a provisional envelope of normally <= 3 seconds and approximately <= 5 seconds on a controlled healthy host; do not adopt 10+ second recovery solely to improve idle CPU. Verify any candidate on the same display and service states. This experimental boundary is not implemented by CER-M4-003 and does not revise performance budgets.

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
