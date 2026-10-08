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

CER-M4-003's Advisor boundary permitted bounded reconnect coordination while preserving independent MetricId connections, with recovery normally within 3 seconds and approximately 5 seconds as the controlled-host ceiling. CER-M4-004 exercised that boundary below. Shared/multiplexed telemetry data connections remain unauthorized, and the experiment does not revise performance budgets.

## CER-M4-004 — WidgetHost disconnected reconnect CPU

Date: 2026-10-07. Verdict: **PERFORMANCE_RECONNECT_FIX_VERIFIED**. A process-local single-flight attempt reduced disconnected CPU without changing Named Pipe v1 or sharing telemetry data connections. Each subscription still opens its own pipe for its own MetricId. The implementation also marks availability false when a read stream ends cleanly, as well as when it throws.

The paired Release comparison used the exact base `fe619e9581aca038a16abb3a6087f129225dce3b` and the production candidate as separate binaries. Each run used a fresh minimized WidgetHost on non-primary `DISPLAY2`, an absent TelemetryService, 8 seconds of warm-up and a 12-second `Process.TotalProcessorTime` interval; Working Set and Private Bytes were sampled once per second. Pair order alternated. The window was placed without activation and the harness rejected any run that left WidgetHost as the foreground process. CPU is percent of one logical/core-equivalent CPU.

| Disconnected on DISPLAY2 | Pairs | CPU % median (range) | Paired candidate wins | Median paired delta, percentage points | Working Set MiB median | Private Bytes MiB median |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Base | 10 | 3.434 (1.944–5.314) | — | — | 119.42 | 136.09 |
| Single-flight candidate | 10 | 1.555 (1.037–2.333) | 10/10 | -1.748 (range -4.147 to -0.907) | 118.20 | 134.96 |

The candidate's median disconnected CPU was 54.71% lower and below 2% of one core. The paired results all favored the candidate. The two CPU ranges still reflect host variation; these measurements are specific to this host and session, not a universal idle-CPU guarantee.

Connected checks used separate base and candidate Release binaries, the same service and process-time protocol, and fresh WidgetHost processes. Visible testing used five pairs: base/candidate median CPU was 1.944%/1.556%, the candidate won 3/5, and the median paired delta was -0.518 percentage points (ranges 1.167–2.332% and 1.296–2.332%). Minimized testing extended to ten pairs after early variation: medians were 1.621%/1.620%, the candidate won 5/10, and median paired delta was -0.065 percentage points (ranges 1.036–3.629% and 1.036–3.370%). No material connected CPU regression was observed. Candidate Working Set and Private Bytes medians were lower in both views; first-snapshot startup medians were about 0.935/0.949 seconds visible and 1.056/1.056 seconds minimized (candidate/base).

Lifecycle validation used a real Named Pipe v1 `cpu.total.usage` subscribe accepted response as TelemetryService-ready. Across ten service stop/start cycles, all three rows recovered in a median 1.036 seconds (0.985–1.054 seconds); all ten were within 3 seconds and 5 seconds. Starting WidgetHost with the service absent kept all rows truthfully `Unavailable / No snapshot` after 5.834 seconds; after the service accepted a real subscribe, all three rows returned in 0.970 seconds. After a clean EOF, all rows became unavailable within 27 ms; a further 12-second absent-service hold measured 1.432% CPU, followed by full recovery in 1.005 seconds. Closing while absent at 0.25, 1.2 and 2.2 seconds completed gracefully while all three subscriptions were connecting. Live rows remained CPU `Fresh / Measured`, FPS `Stopped / Unknown`, and Codex `Error · route_unsupported / Unknown`.

Validation: Release solution build passed with 0 warnings/errors; Core, Telemetry and Integration suites passed 39/397/22 (458/458, zero skipped); focused Named Pipe client tests passed 10/10. A read-only concurrency review returned PASS after the clean-EOF correction. No IPC contract, telemetry topology, dependency, project or performance budget changed. M4 remains ACTIVE / INCOMPLETE; M3 remains DONE and M7 remains NOT STARTED. M5, taskbar/tray and OSD work remain outside this task.

## CER-M4-006 bounded composition and MetricBus scale characterization

Date: 2026-10-07/08. Bus decision: **KEEP_CURRENT_BUS**. Track C copied and verified the actual canonical base Core source into a disposable Release console harness; no product bus, dependency, project or backpressure-policy change was justified. Three independent warmed runs tested 10/50/100/250/500 subscriptions, exact distinct IDs with one match, distributed publication and many same-ID readers. Prebuilt immutable snapshots isolated publication cost from construction, IPC and UI. Tiered compilation was disabled for stable warmed code; these are not startup measurements. Reported p50/p95 are medians of per-batch percentiles, not pooled percentiles.

| Subscriptions, capacity 1 with active readers | One match p50/p95 µs | Distributed p50/p95 µs | Same ID p50/p95 µs |
| ---: | ---: | ---: | ---: |
| 10 | 0.7 / 4.9 | 0.6 / 2.9 | 3.8 / 9.6 |
| 50 | 1.1 / 5.0 | 1.3 / 1.5 | 17.6 / 26.7 |
| 100 | 1.6 / 4.3 | 1.8 / 2.1 | 34.1 / 44.1 |
| 250 | 3.3 / 6.2 | 3.6 / 4.9 | 83.0 / 109.5 |
| 500 | 6.1 / 9.4 | 7.1 / 10.0 | 163.9 / 235.8 |

A buffered capacity-64 isolation measured 216 B/publication for one match and 288–4208 B for 10–500 same-ID subscribers. Capacity 64 is a measurement control, not the production capacity. Nine lossless slow-reader proofs blocked for 199.5–204.7 ms while a requested 200 ms hold kept its capacity-1 channel full. Independent-ID concurrent publications continued, but later publications awaited by the same sequential caller stalled. An index would not remove that deliberate backpressure. `BoundedChannelFullMode.Wait`, ordinal exact routing, cancellation and disposal are unchanged. The 500-reader bus stress is not a 500-pipe runtime claim: the current server's maximum instance count is 255.

At plausible M4 loads of 10–50 distinct IDs, matching cost was not presently material. A paced 100-publications/s experiment overlapped timer-control CPU ranges; incremental bus CPU was inconclusive. A callback MODEL excluding actual subscription code, IPC and WinUI measured 500 field assignments at 5.3/6.6 µs and formatting callbacks at 58.9/122.8 µs (p50/p95). Those figures are not an end-to-end candidate speedup or a process-memory result. F3 is characterized with linear matching and sequential backpressure retained as known limits; revisit indexing only when an actual workload makes matching material. No drop/coalescing-policy decision is triggered.

An additional three-process warmed model includes the actual notification primitive's `GetInvocationList`, synchronous iteration and per-delegate exception isolation, rather than a prebuilt delegate array. The real grouping [2,1,1,2] measured 0.1/0.2 µs p50/p95 and 144 B per four-ID cycle with assignment callbacks; formatting measured 0.8/0.9 µs and 336 B. This still models callbacks and excludes actual subscription execution, IPC and WinUI. The small invocation-array allocation does not justify a notification framework or an optimization claim.

The candidate surface requests four distinct IDs for six components. A disposable background console probe used the actual candidate subscription/client/presentation source against the actual local service, with acceptance-only counting added outside the repository. CPU/RAM were Fresh/Measured, gaming Stopped/Unknown without a target, and Codex Error/route_unsupported/Unknown. Initial startup accepted exactly one pipe for each ID, including CPU and quota shared by readout/bar; service restart added exactly one accepted pipe per ID. Service-absent recovery took 914.8 ms after a separate accepted-ready probe; restart recovery took 995.0 ms; transport loss cleared values within 19.7 ms. These single-run no-window observations are not WinUI recovery bounds. The real 5-second OpenCode activity stream retained its Unavailable/Unknown snapshot through a 3.5-second quiet hold with one accepted connection and no churn. Cancellation awaited all loops and cleared binding state. No runtime snapshots were fabricated.

**Native acceptance: PASS at 96 DPI.** The first DISPLAY2 gate aborted when that monitor vanished. Fer subsequently authorized interactive use of the currently available primary screen. Actual native absent startup renders exact MetricIds with Unavailable/No snapshot; descriptor labels/units appear after service recovery. Initial restart loss cleared within 21.2 ms and restored rows in 1017.6 ms; absent recovery took 1043.7 ms after service readiness. After the minimum-layout correction, restart cleared within 43.3 ms and recovered in 1079.2 ms; close during connection took 125.4 ms. Normal close passed. These are single observations, not guaranteed timing bounds. CPU/RAM were Fresh/Measured, gaming Stopped/Unknown and Codex Error/route_unsupported/Unknown; both native bars shared their respective readout values and metadata. A validation helper initially searched descriptive labels during descriptor-free startup; correcting it to accept the exact IDs resolved that harness failure without product changes.

Dark/light at default 420×620 and minimum 360×340 passed actual native inspection, vertical-scroll access and full containment of the bottom bar's range text within both the Win32 client rectangle and ScrollViewer viewport. A disposable Root.RequestedTheme override exercised light mode without changing Windows settings or fabricating snapshots. The original minimum test revealed a real bug: Root's client-area minima duplicated dimensions intended for the outer native window, clipping the bottom range behind the caption/frame allowance. Removing those two Root constraints preserves the native presenter minima and fixes the viewport. Higher physical DPI and live monitor-scale transitions remain unverified under F5. The paired measurements below precede that minimum-only correction; their default-layout geometry was unchanged because the removed minima were inactive at 420×620. Carry-forward is bounded to those conditions, not a claim of new minimum-size performance measurements. Existing aspirational budgets and F7 remain unchanged/open.

### Completed native base/candidate characterization

Three alternating pairs per condition used separately built Release binaries from canonical base `aafc90425b5c17e6514989c4b7f4ddd0f577c6f5` and technical candidate `09b0e690be6bfe68092a534692eda76b99ebd4c2`, on the currently available primary display. The base's disposable startup placement/no-activation change is outside production; the candidate uses its production startup. Both are placed before warm-up. Each fresh process uses the established 8-second warm-up, 12-second process CPU interval and 1 Hz Working Set/Private Bytes sampling, without a debugger. The same actual base service is used for healthy conditions. CPU is percent of one core; memory is MiB. Ranges span the three per-run results; memory entries summarize per-run sample medians.

| Condition | Variant | CPU median (range) | Working Set median (range) | Private median (range) |
| --- | --- | ---: | ---: | ---: |
| Absent | Base | 0.909 (0.908–1.425) | 117.62 (117.08–118.14) | 134.77 (134.16–134.92) |
| Absent | Candidate | 1.297 (0.908–1.816) | 120.29 (120.27–120.42) | 137.89 (137.59–138.04) |
| Connected visible | Base | 0.778 (0.649–0.778) | 128.03 (127.55–129.13) | 137.93 (137.59–139.01) |
| Connected visible | Candidate | 1.427 (1.427–1.427) | 134.64 (131.85–134.76) | 144.08 (141.36–144.19) |
| Connected minimized, verified | Base | 2.077 (2.076–2.206) | 127.45 (127.27–128.55) | 137.51 (137.49–138.83) |
| Connected minimized, verified | Candidate | 2.596 (2.077–3.633) | 134.26 (131.37–134.38) | 143.73 (141.44–143.95) |

Candidate-minus-base paired CPU differences were −0.128/0.908/approximately zero percentage points absent, 0.649/0.649/0.778 visible, and approximately zero/0.390/1.557 verified minimized. The absent result is mixed; the visible increase repeats. Paired memory increases were 2.15–3.19 MiB Working Set / 2.67–3.73 MiB private absent, 3.82–7.21 / 3.43–6.60 visible, and 4.10–6.93 / 3.93–6.46 verified minimized. Earlier intended-minimized runs requested minimization before healthy startup and did not verify the actual window state. Their raw results are retained outside the repository but excluded from this minimized receipt. The corrected helper minimizes after startup and checks native IsIconic after minimization, warm-up and throughout sampling.

Bounded follow-up controls removed the bars, then RAM, while holding the candidate layout constant; a final same-content control used the canonical three IDs/XAML/340 height with the candidate subscription/client/readout pipeline. Across two alternating pairs each, full-minus-four-readout visible CPU differences were +0.520/−0.260 points; four-minus-three-readout differences were approximately zero/−0.519; same-content pipeline differences were −0.128/+0.648. The last control's Working Set differences were +0.36/+0.40 MiB and private −0.76/−0.17 MiB. Two corrected minimized bar/control pairs differed by +0.649/approximately zero CPU points. These mixed comparisons do not precisely attribute CPU to a bar, RAM or one property. Different foreground states and disposable startup sources weaken CPU attribution; separate-session control differences must not be added into an exact decomposition.

Track C independently reviewed 34 applicable raw runs and returned **characterization complete; no blocking implementation-specific performance defect established for the bounded CER-M4-006 scope**. The larger full surface's repeated visible cost and additional memory remain measured facts. Actual four-stream/six-consumer ownership, matched callback counts, preserved recovery/quiet-stream/close behavior and guarded updates without tree reconstruction support that limited disposition. CPU nonregression, statistical equivalence, zero overhead and budget achievement are not proven. No numerical allowance or optimization is invented. **KEEP_CURRENT_BUS; F7 remains OPEN and M4 closure is not established.**

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

## CER-M4-007 — bounded readout-layout comparison

Date: 2026-10-08. Baseline is integrated CER-M4-006 (`905ab5ce09593ad934c9c7aeba8f81bcaf258d17`); candidate changes only MetricReadout XAML. Both use the production startup, four MetricIds/six consumers, default geometry and the same real TelemetryService. Two alternating baseline/candidate pairs per visible/native-verified minimized condition produced eight applicable Release runs. Each used 8 s warm-up and 12 s sampling (actual elapsed 12.0485–13.2355 s), process CPU-time deltas and 1 Hz Working Set/Private Bytes. Both visible candidates and baselines became foreground through normal production activation; minimized state was verified during warm-up and every sample. CPU hardware was Fresh/Measured, gaming Stopped/Unknown and Codex Error/route_unsupported/Unknown. The first attempt stopped before sampling because equivalent executable paths used different separators; normalized expected paths retained the ownership check.

| Condition | CPU one core median (range) | Working Set median (range), MiB | Private Bytes median (range), MiB |
|---|---:|---:|---:|
| Visible baseline |1.2315% (1.0370–1.4260)|135.13 (135.08–135.18)|143.45 (143.16–143.74)|
| Visible candidate |1.0370% (1.0370–1.0371)|134.60 (134.50–134.70)|143.21 (143.13–143.29)|
| Minimized baseline |1.4262% (1.1665–1.6859)|134.94 (134.68–135.20)|143.67 (143.32–144.02)|
| Minimized candidate |1.0558% (0.9444–1.1673)|133.19 (131.98–134.40)|142.10 (140.98–143.22)|

Paired candidate-minus-baseline CPU differences were 0.0000/−0.3889 percentage points visible and −0.5186/−0.2221 minimized. WS differences were −0.48/−0.58 MiB visible and −2.70/−0.80 minimized; private differences were +0.13/−0.61 and −2.34/−0.80MiB. This small single-host comparison detected no repeatable layout-specific regression; it does not prove an optimization, general CPU nonregression or budget achievement. Timer quantization, changing hardware values and ambient scheduling remain limitations. Synthetic native state fixtures were not running during these real-service measurements. F7 stays OPEN and budgets are unchanged.

## CER-M4-008 DPI candidate characterization

Eight same-content Release fresh-process comparisons use the exact integrated CER-M4-007 baseline and production PMv2/lifecycle candidate, the same actual service and four MetricIds/six consumers. Two alternating pairs each run non-minimized and minimized at the original 100% scale. Twelve one-second samples record process CPU delta/actual elapsed time, Working Set and Private Bytes; elapsed intervals span 12.052–13.4954 seconds. Minimized samples verify native iconic state. These runs contain no synthetic provider fixture; representative CPU/FPS/Codex states remain Fresh/Stopped/route_unsupported. Foreground stays the owned Settings frame in all eight runs; non-minimized does not mean foreground. This characterizes the 96 DPI baseline, not higher-DPI overhead.

| Condition | Baseline CPU % of one core, median (range) | Candidate CPU | Baseline WS / private MiB, medians | Candidate WS / private |
| --- | --- | --- | --- | --- |
| Non-minimized | 1.8140 (1.2955–2.3324) | 1.6850 (1.4255–1.9445) | 133.655 / 142.785 | 135.365 / 143.990 |
| Minimized | 1.4792 (1.2736–1.6848) | 1.6206 (1.4260–1.8151) | 132.250 / 141.045 | 133.980 / 142.455 |

Non-minimized paired CPU signs differ; both minimized differences are positive (0.1303/0.1524 percentage points). In the first minimized pair the candidate consumes one extra 15.625 ms CPU tick; in the second both CPU deltas equal 171.875 ms while the baseline sampling interval is longer. Memory medians increase about 1.2–1.7 MiB, within overlapping individual-run ranges. This small characterization is inconclusive for general performance nonregression and establishes no optimization or budget PASS. It does not justify speculative product changes. Independent read-only review finds no demonstrated blocking candidate-specific CPU regression and requires no further runs for this bounded DPI fix. General nonregression remains INCONCLUSIVE. First-observed healthy states arrived 230–479 ms later in all four candidate pairs; this consistent short-startup signal is not isolated attribution, and startup equivalence is not proven. Longer matched CPU intervals and a separately controlled startup investigation are needed before broader claims. F7 and the existing native framework floor remain open.

## CER-M4-010 — native ring overhead characterization

The candidate adds one static CPU MetricRing with its existing readout, sharing CPU's subscription with readout/bar. Six→seven components retain four exact-ID streams. Original WinUI geometry has no player, animation callback or polling loop; the installed determinate ProgressRing was rejected by native painted-accuracy evidence. No optimization or budget achievement is claimed.

Twenty short Release process measurements compare a clean5980e6fb baseline archive with the identified candidate build: two alternating baseline/candidate pairs per condition,8s warm-up/12s samples, actual elapsed12.0439–13.1781s. CPU uses TotalProcessorTime deltas/actual elapsed as percent of one logical core; Working Set and Private Bytes use1Hz samples. Connected cases use the same actual TelemetryService/cadence, with CPUFresh/Measured, FPSStopped/Unknown and CodexError/route_unsupported/Unknown. Synthetic fixtures are absent. Non-minimized default windows retain Settings as foreground at the recorded endpoints; do not call these foreground-rendering measurements. Offscreen cases use360×340 windows scrolled to the bottom, verify the candidate ring offscreen during samples, and have the owned benchmark as foreground at the final endpoint. Minimized state is verified through native IsIconic during warm-up/sampling. Disconnected cases have no service and no numeric ring.

| Condition | Baseline / candidate CPU%, median | Baseline / candidate WS MiB, median | Baseline / candidate private MiB, median |
| --- | ---: | ---: | ---: |
| Connected, non-minimized | 2.6568 / 2.3977 | 135.065 / 136.275 | 143.340 / 144.405 |
| Connected, minimized | 2.7231 / 2.1751 | 132.495 / 134.590 | 141.375 / 142.830 |
| Connected, ring offscreen | 2.0095 / 1.9442 | 134.690 / 133.805 | 142.695 / 142.370 |
| Disconnected, non-minimized | 1.4906 / 1.4905 | 121.360 / 121.690 | 137.815 / 138.165 |
| Disconnected, minimized | 1.2963 / 1.8804 | 121.315 / 121.985 | 137.685 / 138.380 |

Short paired candidate-minus-baseline CPU differences: connected non-minimized+0.1301/−0.6482pp; minimized−0.5782/−0.5178pp; offscreen+0.3888/−0.5195pp; disconnected non-minimized+0.2588/−0.2590pp; disconnected minimized+1.0371/+0.1311pp. The latter positive signal prompted four targeted longer runs, not a code change:20s warm-up/60s samples, two alternating pairs. Their baseline/candidate CPU was1.0123/1.3756% and0.9861/0.9344% (actual60.1981–60.2139s), differences+0.3633/−0.0517pp. Long candidate memory increases were WS+0.87/+0.77MiB, private+0.75/+0.63MiB. This does not reproduce consistently positive long CPU differences, but does not erase the short signal or prove equivalence. Ring-specific attribution remains unestablished.

A final rebuild changed the managed DLL hash relative to the earlier24-run artifact; that difference is not attributed or treated as binary equivalence. Two unchanged canonical rebuilds give the same final hash. Production component-source bytes and final MetricRing/MetricReadout/MetricBar/MainWindow XBF hashes match the native fixture copy. The complete final Release output is pinned outside Git and passes another actual-service lifecycle run. One additional pinned-build long disconnected-minimized pair gives baseline0.9082% versus candidate1.0119% (+0.1037pp/+62.5ms CPU over60.2165/60.2201s), WS+0.11MiB and private+0.28MiB. These two observations are separate from the earlier24, for26 total measured runs. A first resource-hash lookup used the wrong PRI filename; the correct project-named PRI and component hashes are recorded privately. Diagnostic LF builds are not performance evidence; canonical App bytes are restored.

First-observed healthy states in the four default connected candidate pairs arrive159–635ms later; the offscreen pairs have opposite signs. Startup includes acquisition cadence and polling, so this is not isolated constructor attribution or an equivalence proof. The earlier CER-M4-008 startup signal stays open. Endpoint foreground checks do not prove continuous foreground stability. Small samples, quantized CPU ticks, changing hardware and ambient scheduling remain limitations. Independent read-only review of the earlier24-run source/evidence finds no demonstrated blocking ring-specific defect, recommends keeping the minimal implementation and retaining general performance nonregression INCONCLUSIVE. Independent final source/artifact/evidence review accepts the separation and corroboration for bounded characterization, with no concrete blocker or further required runtime revalidation. Final-build connected performance was not separately measured: those earlier connected observations remain source-qualified, not exact-final-binary results. F7 budgets, framework floor and startup attribution remain OPEN; no approximately-zero idle CPU, memory-budget PASS, zero overhead or optimization is claimed.

## CER-M4-011 — disconnected pipe acquisition attribution

Investigation date: 2026-10-08. .NET 10's `NamedPipeClientStream.ConnectAsync(CancellationToken)` runs its infinite transport-connect loop on a ThreadPool worker, using `SpinWait` after failed Windows probes. Missing pipes return immediately; this is yielded/spaced spinning, not continuous full-speed spinning. Official pinned implementation: [connection loop](https://raw.githubusercontent.com/dotnet/runtime/v10.0.12/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeClientStream.cs) and [Windows transport](https://raw.githubusercontent.com/dotnet/runtime/v10.0.12/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeClientStream.Windows.cs). A zero timeout is rejected for shipping: busy `WaitNamedPipe(0)` uses the server-defined default wait, not an immediate check.

Disposable isolated .NET Release probes used one absent random local pipe, six-second warm-up, actual twenty-second CPU intervals and process WS/private medians. Three alternating current/zero-timeout+100ms pairs measured current1.2459/1.4793/1.8691% of one core versus0.7007/0.7008/0.5450%; the zero candidate is absent-pipe evidence only. A positive1ms/100ms candidate measured0.8563/0.5451% against current1.4015/1.5573%. No-connect console controls measured0.4671/0.6229%, including the probe's own in-process sampling work and short-interval noise; this does not separately attribute a native framework floor. These probes are attribution evidence, not WidgetHost or live telemetry validation.

Two alternating positive1ms comparisons gave100ms-delay CPU0.5452/0.7012% and250ms-delay0.6230/0.5450%. Allocation over20s was461,024 bytes for each100ms run versus260,008/258,664 bytes at250ms; all runtime/probe allocation is included, not attributed solely to exceptions. No consistent250ms CPU advantage was established.100ms is the lower-latency candidate; timings are policy values, not exact scheduler guarantees. The candidate bounds only pipe acquisition, catching only its TimeoutException and awaiting cancellable delay. Subscribe/ACK remains outside that retry, with the unchanged caller's five-second whole-handshake token and existing one-second outer retry.

A pipe that becomes available during the inner delay can wait roughly100ms plus scheduling/native-call overhead. Busy pipes share that tradeoff; short availability windows can be missed, and delayed transport reduces remaining ACK budget. No hard recovery bound or busy-server latency equivalence is claimed. Security/current-user/local scope, independent per-MetricId pipes and singleflight remain unchanged. The identified production comparisons below and independent final source/evidence review are complete; exact-head review/CI/integration remain pending; F7, framework floor, startup attribution and general nonregression remain OPEN/INCONCLUSIVE.

Identified production Release comparison: baseline DLL SHA256 `9B37AE90FE2335962BEE293ABC50FFA7649A35927865D571049DE8964BDADF88` (integrated010) versus candidate `EEC4913632CBD08023ADFC306167D554B751CECA6C0700BD0E03F4BE431BA50B`. Both complete outputs were pinned outside Git; the candidate DLL still matches the unchanged repository output after validation. Twelve observations use the same real service for connected runs and no service for disconnected runs, native IsIconic confirmation, process CPU deltas divided by actual elapsed time, and once-per-second WS/private samples. Two alternating pairs per condition, baseline/candidate then candidate/baseline:

| Condition | Pair | Baseline/candidate one-core CPU | Baseline/candidate WS MiB | Baseline/candidate Private MiB |
|---|---|---|---|---|
| Absent, minimized,20s warm/60s sample | 1 | 1.0121 / 0.4931% | 123.78 / 123.95 | 138.66 / 139.36 |
| Absent, minimized,20s warm/60s sample | 2 | 0.9601 / 0.4931% | 123.60 / 124.19 | 139.20 / 139.70 |
| Connected, minimized,8s warm/12s sample | 1 | 1.8716 / 3.2410% | 133.65 / 133.50 | 143.02 / 142.69 |
| Connected, minimized,8s warm/12s sample | 2 | 2.7225 / 2.5923% | 135.85 / 133.10 | 143.86 / 141.38 |
| Connected, minimized,20s warm/60s sample | 1 | 0.5450 / 0.1816% | 131.85 / 132.28 | 139.53 / 139.93 |
| Connected, minimized,20s warm/60s sample | 2 | 0.7007 / 0.4670% | 131.59 / 132.13 | 139.09 / 140.45 |

Long actual intervals60.2039–60.2217s; short12.0522–13.3579s. Disconnected CPU reductions51.3/48.6% (approximately50% across the two pair medians) corroborate the isolated mechanism and narrow correction on this host. Residual CPU remains approximately0.49% of one core; not all residual cost is attributed to pipe acquisition. WS rises0.17/0.59MiB, private0.70/0.50MiB in those pairs: no memory improvement or budget PASS.

The first short connected pair increased+1.3694core percentage points; the second decreased−0.1302pp. That unresolved positive observation prompted the four longer connected runs, rather than being discarded from source inspection. Both long differences are negative (−0.3634/−0.2337pp), so consistently positive connected CPU differences are not reproduced. This does not erase the short signal or prove equivalence. No connected optimization is claimed: live hardware values/formatting frequency, ambient scheduling, startup/tiering and quantized CPU ticks vary. Long connected WS/private increases also remain recorded. First healthy CPU/FPS/Codex readouts arrived1124.26/958.44ms versus short baseline962.60/1442.20ms; long candidate1041.86/962.83ms versus960.93/949.76ms. Those acquisition/startup observations do not isolate constructor latency; existing startup attribution remains OPEN.

Three additional native actual-service cycles pass loss/recovery/close/absent reopen: loss140.95–158.12ms; recovery1989.05–2035.73ms from service restart launch through readiness to Fresh; connected close75.92–87.35ms and absent close81.22–85.83ms. Their clock includes service startup and is not comparable directly to earlier ready-to-recovery timings. These are individual bounded observations, not latency guarantees. The first long-connected harness generated an unmatched foreach and failed parsing: NOT RUN, no receipt counted. Its corrected script was parsed successfully before only the long measurement was started; the three completed native cycles were preserved and not repeated.

No general nonregression, approximately-zero idle CPU, memory-budget PASS or startup-equivalence claim. F7, native framework floor, mixed-monitor coverage and existing startup attribution remain open. Independent final source/evidence review PASS accepts bounded corroboration with no blocking defect or further required runtime check. Exact committed-head review, CI and protected integration remain required.
