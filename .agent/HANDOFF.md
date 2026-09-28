# Handoff — CER-M1-004 hardware/provider diagnostics and M1 exit

Status: READY FOR ADVISOR REVIEW. Base `17bdeb61b65edc28db01df5a2935f2e8fd109747`; branch `codex/CER-M1-004-hardware-diagnostics-m1-exit`; review head is the branch HEAD after the single commit. Started from clean `HEAD == main == origin/main`; CER-M1-003 was DONE and reconciled, with no other active M1 writer. No PR or merge. M2 not started.

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
| Hardware absent, elevation, topology | Storage 0 on this non-elevated host; cause not inferred. Startup indices, removal and re-add limits remain. |
| Lifecycle/leaks | One LHM Computer per process; WidgetHost connections dispose on retry. Repeated LHM open/close not verified; upstream StorageGroup subscription remains a known limit. |
| Privacy / provider identifiers | Console has category counts and validated codes only; no serial, MAC, IP, path, GUID, raw exception or provider identifier. |
| Duplicate state / framework / dependency | No durable diagnostic state, new dependency or generic framework. |
| Existing CPU/RAM/GPU/VRAM/fan/Storage/Network regression | Release build and all canonical suites PASS; non-elevated demo exercised available groups. Storage positive path remains unverified. |

## Verification

Release solution build PASS, 0 warnings/errors. Core 39/39, Telemetry 9/9, Integration 9/9 PASS. The new test checks Fresh measured zero, Unavailable without value and Error with sanitized code. It does not simulate real hardware. `git diff --check` PASS; no configured preflight script. Repository Quality tracked-file filename patterns and final diff/security review PASS. No new dependency or generated/probe file committed.

Non-elevated runtime: LHM discovered CPU 1, GPU 1, Storage 0, Network 13. The first summary was Fresh 44, Unavailable 0, Error 6, with `invalid_percentage` six times (network sensors that returned invalid utilization). UI Automation observed CPU/RAM/GPU/VRAM/fan and Network values, Storage unavailable after the first-snapshot timeout, all displayed values unavailable after service loss, and fresh values again after service restart without restarting WidgetHost. The PTY wrapper returned 1 after Ctrl+C without a service shutdown line, so graceful service exit code is not verified. WidgetHost closed through its window handle. No elevated, Storage-positive, NVIDIA/Intel/multi-GPU, forced refresh exception, hotplug or repeated-open/close test was performed.

## M1 exit audit against docs/ROADMAP.md

| Requirement | Existing implementation and evidence | Limit / exit |
| --- | --- | --- |
| LibreHardwareMonitor | v0.9.6 integrated in one provider; real non-elevated discovery and samples | Backend internal errors can be hidden; non-blocking for baseline |
| CPU | `cpu.total.usage`; observed measured UI reading | Single local CPU; non-blocking |
| GPU | Indexed LHM GPU samples; observed AMD UI reading | Other vendors/multi-GPU and persistent identity unverified; non-blocking |
| RAM | `GlobalMemoryStatusEx`; observed measured UI reading | Physical-memory API only; non-blocking |
| VRAM | GPU memory sensor mapping; observed measured UI reading | Same vendor/identity limits; non-blocking |
| Basic fans | GPU fan sensor mapping; observed Fresh/Measured 0 RPM | Sensor truth and other fans unverified; non-blocking |
| Storage/network basics | Storage usage/total/free source-audited and unavailable on zero discovery; Network real readings here and positive traffic in CER-M1-003 | Storage has no positive local runtime validation; read/write throughput deferred. Baseline semantics and graceful absence satisfy M1 scope, with this uncertainty explicit |
| Diagnostics | Discovery counts, snapshot states/codes, UI timeout/reconnect; real degraded demo and deterministic test | Cannot independently validate hardware truth; non-blocking |

Conclusion proposed for Advisor: `M1 — Hardware MVP: DONE` with the listed validation limits explicit. M2 is next but untouched. If independent review requires positive Storage hardware evidence for milestone exit, reject this proposal and block M1 exit on a minimal Storage-positive probe on a suitable host; do not infer it from this machine.

Modified: provider, TelemetryService Program, WidgetHost MainWindow, one existing Telemetry test, `docs/ARCHITECTURE.md`, `docs/METRICS.md`, `docs/ROADMAP.md`, `.agent/STATE.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md`, this handoff. New files/dependencies: none. Graphify code graph refreshed to 485 nodes and 960 edges; its community labels and documentary semantic graph remain stale. Working tree clean after the review commit and push.
