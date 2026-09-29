# Handoff — CER-M2-001 PresentMon displayed telemetry vertical slice

Status: READY FOR ADVISOR REVIEW. Single write owner: Codex. Branch `codex/CER-M2-001-presentmon-display`, based on `main` at `89ddd69c13affd76b3b6085a25753bb7b83b0a3f`. M0 and M1 remain complete; M2 remains incomplete and CER-M2-002 has not started. No PR or merge.

## Implementation boundary

TelemetryService accepts one explicit positive `--pid` and publishes only `game.fps.display` and `game.frametime.display`. The PresentMon adapter owns the native session/tracking/query handles and verifies the API version, metric metadata, data layouts and sample freshness before returning readings. It publishes through existing MetricRegistry, MetricBus and Named Pipe protocol v1. WidgetHost consumes only the MetricIds. No Core contract, package, generic provider framework, logging of process metadata or solution-wide architecture change was introduced.

Cerith carries only the official x64 `PresentMonAPI2Loader.dll` and the adjacent upstream MIT license. It does not redistribute PresentMon middleware or the Service; the official Service installs its matching API. The loader is signed and copied into build/publish output. A 32-bit TelemetryService cannot load it and receives a sanitized `presentmon_architecture` error while hardware publication remains independent.

## Official release and local runtime

Official PresentMon v2.6.0 (released 2026-09-21) MSI SHA-256: `0cb43d2622356c1e277a77f840887f9000cea4e2d4236c0850922fc204c6f820`; Authenticode: Valid, Intel Corporation. The installed API reported version 3.4.0. The SDK loader is version 2.6.0.0, x64, SHA-256 `BA5863F35687F0A1E3A98C5E22535313CB5A37E7DDAFAF8861477BF390FF43BE`; its Intel signature is valid. Service was restored to Running/Automatic after fault injection. These probes were run from a non-elevated user process; the Windows service itself runs under its service account.

## Runtime evidence

- A temporary D3D11 window presented real frames under an explicit PID. At a roughly 165 Hz cap, repeated pipe samples changed around 158–165 FPS and 6.0–6.3 ms. Both IDs were `Fresh`/`Measured`; UI Automation read `164.9 FPS` and `6.07 ms` while the pipe client was non-elevated.
- A no-frame target produced no zero FPS/frame-time sample. The API probe observed zero dynamic values and no display-change frames; Cerith withheld values until valid recent frame evidence existed.
- Closing the target changed both IDs to valueless `Stopped`/`Unknown` on the next approximately 500 ms poll. A two-swap-chain D3D11 target produced `Error`/`Unknown`, code `multiple_swap_chains`, with neither chain selected or combined; closing it then produced `Stopped`.
- Stopping `PresentMonSharedService` left TelemetryService alive and publishing `cpu.total.usage` as `Fresh`/`Measured`; the two gaming IDs became valueless `Unavailable`/`Unknown`. Restarting the official service restored fresh gaming samples without restarting TelemetryService or WidgetHost.
- Startup discovered CPU 1, GPU 1, Storage 0 and Network 13. During PresentMon loss, hardware publication continued. This is one local machine and one D3D11 probe; no commercial game, anti-cheat environment, cross-vendor hardware or controlled capture-overhead comparison was tested.
- Over 12 seconds with a target active, TelemetryService used 0.188 seconds CPU (1.56% of one logical core by process CPU-time delta) and working set increased from 79.6 to 80.5 MiB. PresentMonService working set was 27.9 MiB at both endpoints; its CPU delta was below the available sampling resolution. These are local spot measurements, not a performance guarantee or a before/after target-impact test.
- Forty-five pipe FPS samples had 496 ms minimum, 500 ms median and 502 ms maximum gaps. API sample recency is separately checked against QPC and display-change evidence.

## Pre-mortem outcome

| Risk | Result |
| --- | --- |
| Service/API version mismatch | Installed API v3.4.0 was introspected; unsupported version/metadata paths become sanitized Error, never guessed queries. |
| Loader/process architecture mismatch | Official x64 loader verified and runtime-tested; 32-bit mismatch has a sanitized failure path. No 32-bit runtime was installed for a separate execution test. |
| Service or capability absent | Service stop produced `Unavailable`; hardware stayed live; service restart recovered without restarting Cerith. |
| Permission/elevation assumption | Non-elevated client, TelemetryService, pipe client and WidgetHost completed the real frame flow. |
| No-frame window becomes zero | No-frame probe returned no value; only finite positive samples with recent frame evidence become Fresh. |
| Stale Fresh after target/service loss | Target close produced valueless Stopped; Service stop produced valueless Unavailable. |
| Target exits during query | Process state is checked around capture queries; deterministic stop tests and real close probe did not retain stale values. The narrow exit/query race is handled as a valueless stopped/error result. |
| Multiple swap chains | Two-chain real probe produced sanitized `multiple_swap_chains`; no chain is selected or averaged. |
| Cancellation or native-handle leak | Query/tracking/session destruction is centralized; temporary native API probes returned successful cleanup statuses and all probe processes exited. Long-duration leak soak was not run. |
| Anti-cheat or ETW limitations | No commercial game/anti-cheat test was run; behavior is claimed only for the tested D3D11 workload and official Service/API. |
| Process metadata privacy | Explicit PID is not logged; no process name, executable path, title, command line or raw provider payload is emitted. |
| Provider-framework overdesign | Existing registry, bus and pipe are reused; native seam is limited to the concrete PresentMon ABI. |
| M1 hardware regression | Core, Telemetry and Integration suites passed; concurrent runtime kept CPU and other hardware publication alive through PresentMon stop/restart. |
| Dependency or provenance error | MSI and loader hashes/signatures were verified; upstream MIT license accompanies the only redistributed binary; no package or middleware was added. |

## Verification and handoff

`dotnet restore Cerith.sln`: PASS. Release solution build: PASS, 0 warnings/errors. Core 39/39, Telemetry 16/16 and Integration 9/9: PASS. Deterministic tests cover descriptors, fresh values, unavailable/API error, cleanup/retry, invalid values, cancellation and target stop. Real D3D11 end-to-end flow, service loss/recovery, no-frame, process exit and multiple swap chains were separately exercised. `git diff --check`: PASS. Graphify incremental refresh and HTML/report regeneration: 627 nodes, 1,222 edges and 70 labeled communities; integrity diagnostic found no missing or dangling endpoints, self-loops or collapsed edges, and no sensitive files were skipped. Graphify token usage metadata was unavailable and is recorded as unavailable, not as a measured zero. Its C# extractor warned that two `Cerith.Core` namespace declarations share one node ID and deduplicated that identical node; the graph health check passed.

Canonical semantics, architecture, performance/provenance and decision records are in `docs/METRICS.md`, `docs/ARCHITECTURE.md`, `docs/PERFORMANCE.md`, `docs/REFERENCE_PROJECTS.md` and `.agent/DECISIONS.md`. Do not mark M2 complete or start CER-M2-002 as part of this review.

# Previous handoff — CER-M1-004 hardware/provider diagnostics and M1 exit

Status: CER-M1-004 DONE; M1 — Hardware MVP DONE; M2 NEXT/NOT STARTED. Advisor PASS final for diagnostics and M1 exit. Approved product head `1028767c0e38c06061707eeed694e54fc6b15f6a` on base `17bdeb61b65edc28db01df5a2935f2e8fd109747` passed PR #29 Build and Test and Repository Quality (CodeQL SKIPPED under policy), then was squash-merged into `main` at `59ecb648a9f714376578fc16cdaa446571bd4874`. The integrated tree matches the approved head. This handoff records the post-merge state; no product code changed after that head.

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

Conclusion after Advisor PASS final and PR #29 merge: `M1 — Hardware MVP: DONE` with both evidence gates PASS and the listed limits explicit. M2 is next and has not started.

The product PR changed the provider, TelemetryService Program, WidgetHost MainWindow, one existing Telemetry test, canonical docs and operational memory. This reconciliation changes only `.agent/STATE.md`, `.agent/TASKS.md`, `.agent/HANDOFF.md`, `.agent/WORKLOG.md` and `docs/ROADMAP.md`. New tracked files/dependencies: none. No code structure changed, so Graphify refresh is unnecessary.
