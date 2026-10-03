# Cerith Roadmap

## M0 — Foundation

### M0.1 Repository / safety

- canonical docs;
- Git hygiene;
- code-quality policy;
- security baseline;
- CI skeleton;
- agent workflow;
- dependency/license discipline.

### M0.2 Metric Core

- MetricId;
- MetricDescriptor;
- MetricSnapshot;
- MetricState;
- MetricConfidence;
- MetricRegistry;
- MetricBus.

### M0.3 Runtime skeleton

- TelemetryService;
- WidgetHost;
- ControlCenter shell;
- IPC prototype;
- diagnostics.

For M0, ControlCenter shell means the existing project and dependency direction; its functional UI waits for a concrete capability. Diagnostics means only the runtime baseline: TelemetryService reports ready with the pipe and `synthetic.counter`, then normal shutdown; WidgetHost shows Connecting, the value, or unavailable. Hardware/provider health diagnostics belong to M1.

Exit:

```text
SyntheticProvider
→ normalized MetricSnapshot
→ MetricBus
→ WidgetHost
→ visible value
```

with tests, this runtime diagnostic baseline and the measured IPC baseline (D-016).

## M1 — Hardware MVP

**M1 — Hardware MVP: DONE**.

- LibreHardwareMonitor;
- CPU;
- GPU;
- RAM;
- VRAM;
- basic fans;
- storage/network basics;
- diagnostics.

Exit evidence: CER-M1-001/002/003 provide the LHM CPU/GPU/VRAM/fan, Windows RAM and Storage/Network basics; CER-M1-004 adds sanitized discovery and sample diagnostics plus disconnect/reconnect behavior. The non-elevated host produced CPU/RAM/GPU/VRAM/fan and Network readings but LHM discovered zero Storage. A separate elevated run of the unchanged LHM 0.9.6 service discovered four Storage devices and published `storage.0.usage`, `storage.0.total.bytes` and `storage.0.free.bytes` as coherent Fresh/Measured values. This proves a positive Storage path only with elevation on this host; non-elevated Storage remains unavailable here. A targeted console-signal test also observed `TelemetryService stopped.` and exit code 0. Deferred throughput, absent sensors, internal LHM failures, persistent device identity and topology limits remain explicit in `docs/METRICS.md`. At the time of this M1 exit, M2 was next; no M2 work was part of that exit.

Later hardware telemetry expansion (non-blocking; not part of the M1 exit):

- verified CPU-fan telemetry when a CPU_FAN identity can be established;
- GPU fan RPM and, when exposed with verified semantics, fan control/duty percentage;
- additional CPU/GPU clocks, power, voltage and temperature metrics;
- supported vendor-specific sensors whose source and units can be independently verified;
- no guessing that an ambiguous motherboard fan channel is CPU, chassis or PSU;
- fan control and custom fan curves are a separate future capability, not part of this read-only telemetry expansion.

## M2 — Gaming MVP

- PresentMon;
- FPS;
- frame time;
- 1% low;
- active game/session summary.

Later:

- Application FPS, Displayed FPS and Presented FPS as distinct concepts;
- 0.1% low;
- frame pacing;
- dropped/repeated-frame information;
- frame type and generated-frame distinctions where the provider can prove them;
- advanced latency;
- optional RTSS integration where it adds independently validated value.

Do not infer generated FPS by subtracting two rates unless the provider semantics and frame classification make that derivation valid.

Current status: M2 DONE. CER-M2-001 (displayed FPS and frame time) is DONE via product PR #31, squash `9902cc19ff0f2911ab4ee0ece2337d12b1d9edcf` (approved head `086db95e29124900854e29d2b70caa16b3f43889`). CER-M2-002 (displayed FPS P1) is DONE via product PR #36, squash `0a7ce2ee87aa27e65efea804fa4c0767faa17cbf` (approved head `cf49247d53808f3cb0653708316c8cc732450c2a`). CER-M2-003 (selected game/session summary) is DONE via product PR #38, squash `5364ec7824f424e0206c9ebdecf826e16484d60d` (approved head `561450d391234c98294fdd5fc6e99336575a13d1`; integrated tree matches). Its explicit-target identity, lifecycle and privacy contract are in D-022 and `docs/METRICS.md`; the live service-loss probe confirmed label continuity and frame-metric recovery without TelemetryService restart or retarget. M3 progress is recorded in its section below.

### Post-M2 diagnostic validation mode (future; non-blocking)

After M2 is complete, evaluate a user-invoked **Test / Diagnostic Session** flow in ControlCenter. The intent is development, troubleshooting and telemetry validation, not always-on logging.

A diagnostic session should be able to:

- capture every Cerith metric sample available for the selected game/session, including state, confidence, source and timestamps;
- capture relevant sanitized provider/runtime diagnostics, lifecycle transitions, sampling cadence and version/environment metadata needed to reproduce a problem;
- keep the capture local by default and make its contents inspectable before export;
- export a deterministic session bundle suitable for offline analysis;
- optionally align and compare Cerith results with an independently collected reference trace from another telemetry tool, without treating that tool as infallible ground truth;
- report disagreements explicitly instead of silently normalizing them away.

This mode must not collect secrets, arbitrary personal data, command lines, private paths or unrelated system activity merely because it is called “diagnostic”. Its overhead must be measured, and diagnostic-mode overhead must not be confused with normal runtime overhead.

This future capability does not change M2 acceptance criteria and does not start before M2 closes.

## M3 — AI Telemetry & Quotas

Current status: ACTIVE / INCOMPLETE. CER-M3-001 source reconnaissance/contract is DONE. CER-M3-002 Codex is DONE: implementation, automated conformance, official usage-UI comparison, bounded official Codex app-server corroboration and CI completed; PR #43 was squash-merged as `3c3db6dd1102ec3ed2d95b6234e5550df08ab34a`.

D-024 supersedes only the **future sequential ordering** portion of D-023. D-023 remains historical authority for why Codex was the first vertical slice. M3 now continues as a broad AI-telemetry expansion rather than a fixed Claude-then-Antigravity sequence.

### M3.A — quota / allowance acquisition

Target the **17 usage-limit integrations confirmed to exist in the pinned reference source** and recorded in `AI_TELEMETRY.md`. This is reference-source coverage, not proof that all 17 semantics or Windows/runtime paths are already validated in Cerith. Start from the working Codex contract and validate each provider independently.

Requirements:

- prefer safe reuse of existing official local authorization;
- allow a one-time explicit setup when genuinely required;
- ordinary future startups must not require repeated provider setup;
- preserve account/workspace/project identity boundaries;
- keep quota, credits, balance, billing and activity semantically separate;
- never invent missing values;
- use sanitized fixtures and bounded live validation;
- record shared-upstream limitations instead of pretending multiple clients are independent sources;
- isolate provider-specific implementation paths so bounded workers can proceed in parallel without sharing write ownership.

The exact acquisition mechanism remains provider-specific: documented API, official-client-observed endpoint, CLI, local RPC, credential-backed request or another verified source. An undocumented/internal source is a risk to manage, not automatically a reason to discard an integration already demonstrated by a current reference implementation.

### M3.B — local AI tool activity acquisition

Target the **42-product activity catalog confirmed in the pinned TokenTracker reference** and recorded in `AI_TELEMETRY.md`. The catalog mixes passive, hook-driven, local-database and remote acquisition paths; Cerith validates platform/source semantics per collector rather than claiming blanket local/Windows parity.

The goal is local-first structured telemetry such as:

- model/provider identity;
- input/output/cache/reasoning token counters when actually reported;
- timestamps and bounded session/activity identity;
- project attribution when the source exposes it safely;
- cost inputs/estimates with explicit provenance.

Do not ingest prompt/completion bodies or source code merely because a log contains them. Cost estimates are derived telemetry and never substitute provider-reported billing/quota.

### CER-M3-003 — expansion plan and foundation contract

DONE via PR #45 squash `2aa865ef35000680a3110ff7134b836bd966ef82`. It records the reference audit, target catalogs, auth/setup policy, milestone mapping and parallel ownership boundaries. It introduced no product runtime infrastructure.

### CER-M3-004 — AI quota expansion wave 1

IN PROGRESS. The integration branch has ten non-Codex quota providers staged, including Ark Coding Plan; Qoder International is explicitly deferred with its branch preserved. This wave is not a demand to implement every provider in the 16-provider reference catalog. A mission candidate requires safe accepted integrations, evidence-backed dispositions for unresolved targets, local Release validation and hosted CI; it does not claim M3 completion.

The minimum pre-wave foundation is ownership, not a new framework: provider-specific source/test/fixture paths are exclusive to one write owner, while shared registration/lifecycle/dependencies/docs belong to one integration owner. No ProviderManager, Factory, universal auth layer or other shared runtime abstraction is justified before multiple accepted providers demonstrate the same concrete need.

The 16-provider usage-limit catalog remains research reference scope; current provider dispositions live in the active task record. Current unresolved priority is Claude, then Gemini CLI / Code Assist where enterprise scope remains distinct from Antigravity, then Grok Build. Marginal unresolved providers may be deferred after preserving their exact blockers; existing accepted integrations remain staged. Provider work uses one owner per provider path and one owner for shared integration files. Integrate accepted providers in small verified groups with CI and coexistence checks between groups. Do not treat 17/17 implementation parity as a CER-M3-004 candidate gate. Candidate status requires a later independent review and Advisor/user decision before approval or merge.

A worker that proves one-time human authorization is necessary documents the exact mechanism and returns any shared setup/storage requirement to the integration owner instead of silently introducing a global auth system.

M3 cannot exit by classifying the entire target catalog as blocked/deferred. Before each implementation wave starts, its committed provider/collector set must be explicit. M3 exit requires, at minimum:

- the completed Codex quota slice plus additional accepted quota integrations from the broad catalog;
- at least one accepted passive/local activity collector flowing through MetricSnapshot → MetricBus → IPC → a MetricId consumer;
- verified coexistence showing absent, slow or failing AI integrations do not stop quota, hardware or gaming acquisition;
- every remaining target either implemented with verified semantics or explicitly tracked as blocked/deferred with concrete evidence.

Breadth targets are not permission to fabricate parity, and blocked/deferred classification cannot substitute for delivering the committed wave.

## M4 — Widget Engine & Compact Surfaces

- text;
- metric;
- bar;
- ring;
- sparkline;
- simple graph;
- container;
- binding;
- persistence;
- themes;
- compact floating surface;
- taskbar surface;
- tray indicators;
- detail popup / expanded inspection surface.

The same MetricId-driven component semantics must feed every surface. Gengchou and AI Limits are interaction/compactness references, not architecture bases. Adaptive density, truthful stale/error states, DPI/theme correctness and measured idle overhead are acceptance concerns. Provider-specific drawing logic is not.

## M5 — Visual Editor

- canvas;
- selection;
- snap;
- layers;
- inspector;
- undo/redo;
- alignment;
- copy/paste.

## M6 — Rules & Profiles

- conditions;
- alerts;
- visual reactions;
- contextual profiles.

## M7 — History & Analytics Dashboard

- SQLite;
- retention/downsampling;
- charts;
- sessions;
- AI usage trends;
- model/provider breakdown;
- local cost analysis with explicit pricing provenance;
- GitHub-style activity heatmap;
- safe project attribution;
- dashboard composition over the same metric/history contracts used by widgets.

TokenTracker is a product/information-architecture reference for the dashboard, not a mandate to embed its web stack or cloud features. Local history remains authoritative by default.

## M8 — Public Plugin SDK

- stable versioned contracts;
- permissions;
- examples;
- docs.

## M9 — Ecosystem

- widget/theme packs;
- registry concept;
- import/export.

## M10 — Remote / Multi-PC

- remote metric protocol;
- second-machine dashboards.

## Rule

Do not skip foundations to chase screenshots.
