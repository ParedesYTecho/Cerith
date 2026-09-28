# Handoff — CER-M1-004 hardware/provider diagnostics and M1 exit

Status: READY FOR ADVISOR REVIEW, second pass. Base `17bdeb61b65edc28db01df5a2935f2e8fd109747`; first review head `9e5698faff2c0cedc4da7b182ad0869b5a652ea9`; branch `codex/CER-M1-004-hardware-diagnostics-m1-exit`; second review head is branch HEAD after the evidence-only commit. Advisor: DIAGNOSTICS IMPLEMENTATION PASS, M1 EXIT NEEDS EVIDENCE. Both requested gates now pass. No product code changed in this follow-up. No PR or merge. M2 not started.

## Missing diagnostics and design

M0 already printed service ready/stopped and WidgetHost Connecting/value/unavailable. Hardware snapshots already distinguished Fresh, Unavailable and Error with sanitized codes, but there was no discovery summary, no visible error detail in WidgetHost, and a silent stream could leave an old value displayed indefinitely. TelemetryService now prints only discovered CPU/GPU/Storage/Network counts, a per-sample state/error-code summary when it changes, and a warning that LHM internal failures and sensor truth are unverified. Zero discovered means only that LHM did not discover the category; cause is unknown. The ready line follows a successful first publish. Unexpected fatal failures print a generic code instead of a raw exception.

`MetricSnapshot` remains the per-metric authority. The internal `HardwareDiagnosticSummary` in the existing provider file aggregates only one publish tick, then is discarded; it is justified by the console diagnostic boundary and deterministic classification test. No second metric cache, MetricId, diagnostic protocol, new file, package, logging framework or ControlCenter feature. WidgetHost renders known sanitized error codes and marks a disconnected or silent subscription unavailable, then reconnects. Normal readings remain Fresh/Measured; a real zero stays zero.

## Pre-mortem and evidence

| Risk | Result |
| --- | --- |
| False healthy verdict; diagnostic proves itself but not data | No healthy verdict. Counts describe publication outcomes; measured UI values do not prove sensor truth. |
| Capability absent vs sensor unavailable vs known failure vs real zero | Discovery counts, Unavailable/Error/Fresh snapshots and one deterministic test keep these distinct. Zero discovered has unknown cause. |
| Prior value appears valid after runtime loss | Receive timeout and reconnect; real service stop/restart demo showed unavailable then fresh values without restarting WidgetHost. |
| LHM internal failures / unobserved bad values | Still undetectable; explicitly stated in console and canonical metrics docs. |
| Hardware absent, elevation, topology | Storage 0 non-elevated and 4 elevated on this host. Elevation enabled a positive path here; non-elevated support remains unproven. Startup indices, removal and re-add limits remain. |
| Lifecycle/leaks | One LHM Computer per process; WidgetHost connections dispose on retry. Repeated LHM open/close not verified; upstream StorageGroup subscription remains a known limit. |
| Privacy / provider identifiers | Console has category counts and validated codes only; no serial, MAC, IP, path, GUID, raw exception or provider identifier. |
| Duplicate state / framework / dependency | No durable diagnostic state, new dependency or generic framework. |
| Existing CPU/RAM/GPU/VRAM/fan/Storage/Network regression | Release build and all canonical suites PASS; non-elevated demo exercised available groups. Elevated Storage probe exercised the existing positive path without changing code. |

## Verification

Release solution build PASS, 0 warnings/errors. Core 39/39, Telemetry 9/9, Integration 9/9 PASS. The new test checks Fresh measured zero, Unavailable without value and Error with sanitized code. It does not simulate real hardware. `git diff --check` PASS; no configured preflight script. Repository Quality tracked-file filename patterns and final diff/security review PASS. No new dependency or generated/probe file committed.

Non-elevated runtime: LHM discovered CPU 1, GPU 1, Storage 0, Network 13. The first summary was Fresh 44, Unavailable 0, Error 6, with `invalid_percentage` six times (network sensors that returned invalid utilization). UI Automation observed CPU/RAM/GPU/VRAM/fan and Network values, Storage unavailable after the first-snapshot timeout, all displayed values unavailable after service loss, and fresh values again after service restart without restarting WidgetHost. The earlier PTY Ctrl+C returned wrapper code 1 without a shutdown line; this was not an application exit-code test. WidgetHost closed through its window handle.

Gate A PASS, elevated only: a temporary, uncommitted .NET 10 client started the unchanged service and read the existing local pipe. Non-elevated: ready, Storage discovered 0. Elevated via UAC: ready, Storage discovered 4; `storage.0.usage`, `storage.0.total.bytes` and `storage.0.free.bytes` were all `Fresh`/`Measured` doubles without error codes. Usage was about 33.8%; total was positive; free was nonnegative and below total; usage differed from `100 × (1 - free/total)` by under 0.000002 percentage points. Existing Storage `Data` conversion is ×1,000,000,000 bytes. No read/write throughput MetricIds were reinstated. Missing/error values remain absent under the Core contract; zero discovery generated no Storage snapshots. This proves neither non-elevated Storage support nor independent sensor truth. No serial, path, firmware, SMART raw data or provider identifier was retained.

Gate B PASS: a separate isolated service process reached ready. A probe attached only to its console (two members: probe and service), sent CTRL+C, observed `TelemetryService stopped.` in captured stdout and exit code 0 within ten seconds; stderr was empty. This reproduces graceful cancellation and pipe/publisher completion. The earlier PTY result was a wrapper limitation, not a reproduced lifecycle regression. No `Program.cs` change was needed. NVIDIA/Intel/multi-GPU, forced refresh exception, hotplug and repeated LHM open/close remain untested.

Temporary probe directories remain outside the repository: automatic approval review rejected both directory and individual-file cleanup. They contain only temporary probe code, state/count/value results and sanitized service output, with no provider identifiers or raw hardware payloads. No probe file was staged or committed.

## M1 exit audit against docs/ROADMAP.md

| Requirement | Existing implementation and evidence | Limit / exit |
| --- | --- | --- |
| LibreHardwareMonitor | v0.9.6 integrated in one provider; real non-elevated discovery and samples | Backend internal errors can be hidden; non-blocking for baseline |
| CPU | `cpu.total.usage`; observed measured UI reading | Single local CPU; non-blocking |
| GPU | Indexed LHM GPU samples; observed AMD UI reading | Other vendors/multi-GPU and persistent identity unverified; non-blocking |
| RAM | `GlobalMemoryStatusEx`; observed measured UI reading | Physical-memory API only; non-blocking |
| VRAM | GPU memory sensor mapping; observed measured UI reading | Same vendor/identity limits; non-blocking |
| Basic fans | GPU fan sensor mapping; observed Fresh/Measured 0 RPM | Sensor truth and other fans unverified; non-blocking |
| Storage/network basics | Storage source mapping and elevated `storage.0` usage/total/free `Fresh`/`Measured` pipe readings; Network real readings here and positive traffic in CER-M1-003 | Non-elevated Storage discovery 0 on this host; read/write throughput deferred. Positive path and graceful absence satisfy M1 scope with privilege limit explicit |
| Diagnostics | Discovery counts, snapshot states/codes, UI timeout/reconnect; real degraded demo and deterministic test | Cannot independently validate hardware truth; non-blocking |

Conclusion proposed for second Advisor review: `M1 — Hardware MVP: DONE` with both evidence gates PASS and the listed limits explicit. M2 is next but untouched.

First review changed provider, TelemetryService Program, WidgetHost MainWindow, one existing Telemetry test, canonical docs and operational memory. This follow-up changes only `docs/METRICS.md`, `docs/ROADMAP.md`, `.agent/STATE.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md` and this handoff. New tracked files/dependencies: none. Graphify code graph remained at 485 nodes and 960 edges; no code structure changed in this follow-up. Working tree clean after the second review commit and push.
