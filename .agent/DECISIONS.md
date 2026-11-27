# Decisions

## D-001 — Clean start

Cerith starts in a clean repository.

WidgetOSD is legacy reference only.

## D-002 — Native Windows-first

Primary direction: C#/.NET/WinUI/Windows APIs.

## D-003 — Universal metric architecture

Widgets bind to Metric IDs, never directly to providers.

## D-004 — AI quotas start from zero

Legacy quota logic is not ported.

## D-005 — Truthful states

Availability/freshness, confidence, unlimited and error semantics remain distinct. State and confidence are orthogonal; see D-014.

## D-006 — Development workflow separate from product

Graphify, model routing and handoffs are development infrastructure.

## D-007 — Open-source direction

Cerith should be genuinely open source.

License remains provisional until dependency review.

## D-008 — Native translation of UI references

Web UI references inspire design; production runtime remains native.

## D-009 — Public-ready from first commit

No private information is permitted in committed history merely because the repository starts private.

## D-010 — Zero AI slop

Generated code receives normal engineering review.

Every new file, dependency and abstraction must justify its existence.

## D-011 — Test framework

Los proyectos de pruebas usan `MSTest.Sdk` con Microsoft.Testing.Platform y perfil de extensiones `None`. Su licencia MIT es compatible y queda limitada a pruebas; cobertura e informes se añadirán si el flujo los necesita.

## D-012 — Ejecución de pruebas en .NET 10

`global.json` selecciona Microsoft.Testing.Platform (MTP) para `dotnet test`, requerido por `MSTest.Sdk` con .NET 10.

MTP devuelve `ZeroTests` (código 8) cuando un proyecto no descubre ni ejecuta pruebas. La tolerancia global usada durante CER-M0-001 fue retirada al añadirse las primeras pruebas reales.

Los tres proyectos de pruebas tienen casos reales y se ejecutan sin tolerancia `ZeroTests`. La excepción individual se retiró al incorporar las primeras pruebas de cada proyecto; no se añaden tests dummy.

## D-013 — CodeQL según visibilidad

El repositorio privado actual no dispone de code scanning. CodeQL se omite mientras sea privado y se ejecuta automáticamente cuando `github.event.repository.visibility == 'public'`. El job conserva solo los permisos necesarios: `actions: read`, `contents: read` y `security-events: write`.

## D-014 — Contrato fundamental de métricas

`MetricId` conserva texto no vacío exacto con igualdad ordinal case-sensitive y sin gramática cerrada. `MetricState` (freshness/disponibilidad) y `MetricConfidence` (unknown/measured/estimated) son dimensiones ortogonales. Los snapshots usan `MetricValue` tipado y `DateTimeOffset ObservedAt`; la edad se deriva de una hora de referencia. `Unlimited` no contiene `Value` y usa confidence `Unknown`. `MetricDescriptor` es provider-independent; source y error code sanitizado pertenecen al snapshot.

## D-015 — Distribución de snapshots

`MetricBus` ofrece suscripciones async por `MetricId`, con buffer acotado por subscriber y backpressure cuando se llena, sin pérdidas silenciosas. Entrega solo publicaciones futuras; no guarda ni reproduce el último valor. Cancelación y disposal retiran suscripciones. `MetricRegistry` y `MetricBus` permanecen desacoplados.

## D-016 — IPC transport

**Selected for the first implementation:** Windows Named Pipes using asynchronous byte streams and `PipeOptions.CurrentUserOnly`.

The two measured runs put Named Pipes at 77.9–80.9 µs median / 121.4–127.9 µs p95 for 256-byte payloads, and 79.0–80.4 µs / 123.4–125.0 µs for 4096 bytes. TCP was in the same latency range. AF_UNIX was faster (44.1–45.6 µs median / 81.3–82.7 µs p95), but its Windows endpoint is a filesystem path that requires directory ACL policy and explicit stale-path/cleanup handling. Named Pipes need no port discovery or socket-file lifecycle, and their handle cleanup and reconnect behavior passed the prototype checks. The measured AF_UNIX advantage does not justify that extra runtime responsibility for the first same-machine desktop integration.

| Payload | Transport | Median µs, runs 1/2 | p95 µs, runs 1/2 |
| --- | --- | ---: | ---: |
| 256 B | Named Pipes | 80.9 / 77.9 | 127.9 / 121.4 |
| 256 B | TCP loopback | 80.6 / 79.3 | 119.5 / 114.7 |
| 256 B | AF_UNIX | 44.1 / 44.9 | 81.3 / 82.7 |
| 4096 B | Named Pipes | 79.0 / 80.4 | 125.0 / 123.4 |
| 4096 B | TCP loopback | 78.5 / 85.7 | 127.7 / 124.6 |
| 4096 B | AF_UNIX | 45.2 / 45.6 | 82.2 / 81.8 |

| Responsibility | Named Pipes | TCP loopback | AF_UNIX on Windows |
| --- | --- | --- | --- |
| Endpoint discovery | Stable pipe name; no port lookup | Ephemeral port needs handoff; fixed ports can collide | Both processes need the same filesystem path |
| Isolation | `CurrentUserOnly` checks user/elevation; remote-client rejection still required | `127.0.0.1` blocks external hosts, not local processes; no identity handshake | Local-only transport; access inherits the endpoint directory ACL |
| Cleanup / reconnect | Handle disposal; recreate a server instance | Close listener/socket; accept next connection | Close socket and delete path; stale-path recovery must avoid deleting a live endpoint |
| Framing / packaging | Byte stream; custom length prefix. BCL, no package | Same. BCL, no package | Same. BCL, no package; Windows 10/Server 2019+ |
| Errors / operations | Pipe I/O errors/timeouts; named endpoint, no port/firewall diagnostics | Socket errors/port collisions; inspect port and process; protect the handshake | Socket errors plus path/ACL/stale-file cases; inspect endpoint and ACL |

The prototype used identical length-prefix framing on all three. AF_UNIX's test path inherited five ACL rules and had no broad-group allow entry; production must create/use a per-user directory with a reviewed ACL and remove the endpoint safely. TCP bound strictly to `127.0.0.1`, but still needs an ephemeral-port handoff or fixed-port policy and has no process/user authentication; a later implementation would need a protected handshake.

**Security condition for the next implementation:** use only the local pipe name and `CurrentUserOnly` for user/elevation isolation. Windows named pipes can also accept remote clients, and `CurrentUserOnly` is not a remote-client rejection option. Because architecture requires local-only IPC, the implementation task must prove remote clients are rejected using a supported Windows mechanism before exposing the endpoint. The prototype configured `CurrentUserOnly`, but cross-account rejection was not exercised under a second account. See [Named Pipes in .NET](https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication) and [PipeOptions](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0). AF_UNIX's Windows support is documented for Windows 10 / Server 2019 and later ([Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess?view=aspnetcore-10.0#configure-unix-domain-sockets)).

All candidates passed local echo, clean disconnect, reconnect, async-wait cancellation, and endpoint/handle cleanup. Full CPU figures and limitations are in `docs/PERFORMANCE.md`. This is a prototype decision, not a production protocol or performance guarantee.

## D-017 — M0 IPC snapshot protocol

Following D-016, M0 uses Windows Named Pipes with remote-client rejection and current-user access. A byte stream carries length-prefixed frames bounded to 64 KiB, with an explicit protocol version and `subscribe`, `accepted`, and `snapshot` messages. One connection subscribes to one exact MetricId. `accepted` follows active bus subscription; snapshots are future publications only, with no replay. This is an internal M0 wire contract, not a stable public/plugin protocol.

## D-018 — M0 native visual pipeline

CER-M0-008 uses one unpackaged, framework-dependent WinUI 3 window in WidgetHost to display the existing `synthetic.counter` IPC stream. TelemetryService is a console executable that publishes the existing SyntheticProvider once per second. The two executables start manually; this does not establish the M4 widget engine or the final visual design.

WidgetHost references Microsoft.WindowsAppSDK 2.5.1 (stable, Microsoft Software License Terms) for WinUI 3. `WindowsPackageType=None` enables the standard runtime bootstrap; `WindowsAppSDKSelfContained=false` uses an installed Windows App Runtime. The .NET BCL and existing code cannot provide WinUI 3. The package brings transitive Windows App SDK, WebView2, Windows ML, BuildTools and MIT-licensed System.Numerics.Tensors components; these extra capabilities are not used by this slice. The SDK adds a runtime prerequisite and Microsoft-serviced code. Review its license and transitive terms against the draft GPL-3.0-or-later application license before public release.

## D-019 — Backend hardware inicial de M1

`LibreHardwareMonitorLib 0.9.6` (MPL-2.0) es el backend inicial de adquisición hardware para `cpu.total.usage`. Solo TelemetryService/provider code lo referencia; Core, MetricId y WidgetHost permanecen provider-independent. El BCL/Windows API disponible no sustituye razonablemente la adquisición LHM ya verificada para este slice. La librería trae un grafo transitivo y aumenta el footprint y la superficie de mantenimiento; no se añade una arquitectura general de providers.

Audit del transitivo `Mono.Posix.NETStandard 1.0.0`: el `.nupkg` local firmado pasó `dotnet nuget verify --all`; su `.nuspec` tiene `licenseUrl` pero no `<license>`/SPDX ni metadata de repositorio, y el archivo no incluye LICENSE/NOTICE. No se encontró evidencia de licencia incompatible o restrictiva, pero el SPDX/licencia exacta de ese artefacto no queda demostrado. Conservar esta incertidumbre de provenance/third-party-notice para el audit de dependencias previo al public release; no atribuirle una licencia exacta por inferencia.

## D-020 — PresentMon displayed telemetry boundary (CER-M2-001)

Status: Advisor PASS; integrated by product PR #31 (squash `9902cc19ff0f2911ab4ee0ece2337d12b1d9edcf`).

Use the official PresentMon Service/API, pinned to upstream v2.6.0. The local Service/API runtime reported API 3.4.0. Cerith tracks one explicit `--pid`, queries only average displayed FPS and displayed frame time, and publishes through the existing MetricBus, Named Pipe v1 and MetricId-only WidgetHost. A frame query validates that displayed samples continue; no-frame/zero results are Unavailable, process exit is Stopped, and operational failures are valueless Error snapshots with sanitized codes. Multiple swap chains produce `multiple_swap_chains`; this baseline does not choose or combine them. Exact metric semantics are in `docs/METRICS.md`.

The official Service/SDK investigation is recorded in `docs/REFERENCE_PROJECTS.md`. The v2.6.0 MSI SHA-256 was `0cb43d2622356c1e277a77f840887f9000cea4e2d4236c0850922fc204c6f820` and Windows Authenticode verified its Intel Corporation signature. Cerith carries only the official x64 `PresentMonAPI2Loader.dll`, copied from the v2.6.0 SDK: SHA-256 `BA5863F35687F0A1E3A98C5E22535313CB5A37E7DDAFAF8861477BF390FF43BE`. Its upstream `LICENSE.txt` identifies MIT and is included beside the binary. The loader is signed by Intel. Do not redistribute `PresentMonAPI2.dll` or the Service; users need the official Service installed. No NuGet dependency or solution-wide platform-target change was added. A 32-bit process cannot load this x64 loader and reports sanitized `presentmon_architecture`; hardware polling remains independent.

`PresentMonCapture` is the only native boundary and owns the API session, tracking state and query handles. It exists for deterministic provider testing and explicit resource lifetime; it is not a provider framework. Logs omit PID and process metadata. Missing Service/API capability does not stop the M1 hardware provider. CER-M2-002 and M2 exit are not part of this decision.

## D-021 — Displayed FPS P1 statistic (CER-M2-002)

Use PresentMon v2.6.0 `PM_METRIC_DISPLAYED_FPS` with `PM_STAT_PERCENTILE_01` in a separate requested 10-second dynamic query. Publish it as `game.fps.display.p01`, rate / `double` / FPS, through the existing metric and IPC contracts. Keep its warm-up and state separate from the one-second displayed averages. Exact estimator semantics, recent-display evidence, reset conditions and the frame-ring history limit are in `docs/METRICS.md`.

This is PresentMon's reciprocal displayed-interval percentile, not the arithmetic mean of the slowest 1% of frames or an integral low. PresentMon v2.6.0 emits zero intervals for non-displayed frames and retains them in percentile input; `PM_STAT_PERCENTILE_01` reverses rank, linearly interpolates the 99th percentile of interval samples, then reciprocates ([display samples](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/CommonUtilities/mc/MetricsCalculatorDisplay.cpp#L23-L33), [sample collection](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/DynamicMetric.h#L64-L72), [percentile and reciprocal handling](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/DynamicStat.cpp#L157-L206)). Do not use `PM_STAT_COUNT`; the dynamic-query implementation rejects it. Upstream issue [#363](https://github.com/GameTechDev/PresentMon/issues/363) is a historical report against v2.2.0, not evidence of a v2.6.0 defect; direct raw-frame comparison and controlled runtime checks remain required for this slice. No dependency or IPC redesign is justified.

## D-022 — Selected game/session summary (CER-M2-003)

The session is the lifetime of the one explicit `--pid` process. Publish only `game.session.application` as a string MetricSnapshot, named from Windows `Process.ProcessName` and carried on the existing MetricBus, Named Pipe v1 and MetricId-only WidgetHost path. Keep the same opened `Process` object/handle across PresentMon capture retries and confirm that this process remains alive around polling; do not reopen by numeric PID after exit. If the initial process handle cannot be acquired, keep `target_identity_failed` terminal until TelemetryService restarts; a later numeric-PID lookup cannot prove continuity. This keeps process identity independent of PresentMon service/frame availability and prevents a reused PID from becoming a continuation of the original session. No automatic detection, additional process metadata, new dependency, Core change or IPC change is part of this decision.

PresentMon v2.6.0 exposes `PM_METRIC_APPLICATION` as a static query and describes it as the target executable name ([API header](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonAPI2/PresentMonAPI.h), [capture application metrics](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/README-CaptureApplication.md)). It still requires a live PresentMon API session, and its documented value includes `.exe`; it therefore does not define the session label. The Windows property returns the process name without path/extension ([Microsoft documentation](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.processname?view=net-10.0)). Exact states, privacy boundary and UI behavior are in `docs/METRICS.md`.

## D-023 — M3 source contract and first implementation order (CER-M3-001)

Choose **Codex for CER-M3-002**, then Claude, then Antigravity. This is implementation ordering, not a provider dependency in Core or WidgetHost. Evidence, exact upstream revisions and unresolved source discrepancies are canonical in `docs/AI_QUOTA_REFERENCE_MATRIX.md`; field semantics, six proposed MetricIds, state/cache/auth policy and acceptance tests are canonical in `docs/PROVIDERS_AI_QUOTAS.md`. No provider is implemented by this decision.

Codex has a publicly inspectable official client/protocol mapping, demonstrable used/duration/reset fields, independent implementations and an official UI/status validation route. A bounded standard-host Windows GET confirmed access/shape without credential mutation. Select direct read-only `/backend-api/wham/usage` as an **undocumented official-client-observed** source: the documented app-server remains a semantic cross-check because its managed auth path can proactively renew tokens. Restrict the first slice to proven standard ChatGPT file-auth/routing capability; no cookie/WSL/keyring scan, account switch, login/refresh, provider framework or new dependency. A positive percentage is informational, not request-admission permission. Auth/identity/reset failure cannot become zero or a fresh cached value.

Claude follows because its documented statusline is event-fed, version/session-dependent and needs an explicitly configured receiver with proven identity; its broader OAuth usage endpoint/scope/schema is corroborated externally rather than by an auditable official implementation here. Antigravity follows after that because shared group/window semantics are now documented, but CLI quota JSON schema/account binding/auth side effects and internal project/group/RPC mappings still require validation. Do not upgrade older per-model availability or apparently full fractions into verified quota, and do not treat CLI-owned authentication as proof of no renewal.

This ordering balances verifiable semantics, credential safety, useful live quota, validation access and one concrete lifecycle. It does not claim the selected backend is a supported public API or all plans are covered. Parser fixtures alone cannot accept CER-M3-002: independent official UI/status comparison, identity isolation, no-renewal transport proof and reset/failure conformance remain required. M3 stays ACTIVE / INCOMPLETE; CER-M3-002 is NEXT / NOT STARTED.

## D-024 — Broad AI telemetry expansion and reference roles

Status: accepted product/architecture direction after CER-M3-002.

D-023 remains the historical decision that selected Codex as the first M3 vertical slice. Its **future sequential order** (Claude, then Antigravity) is superseded by this decision; the completed Codex rationale and contract remain valid.

M3 now targets broad AI telemetry acquisition:

- the current 17-provider usage-limit catalog recorded in `docs/AI_TELEMETRY.md`;
- the current 42-tool local AI activity catalog recorded there;
- provider-by-provider verification rather than parity claims.

Reference roles are deliberately split:

- TokenTracker is the primary operational reference for broad local acquisition, zero-config onboarding, usage-limit breadth and analytics/dashboard information architecture;
- Gengchou is the primary reference for Windows taskbar/floating/tray/detail compact surfaces and resilient shell placement;
- AI Limits is the primary reference for compact adaptive visual density and measured lightweight native runtime;
- first-party sources remain higher authority for provider semantics when available.

Cerith does not adopt any of those projects as its architecture. Widget-facing behavior remains MetricId-driven and provider-specific auth/acquisition stays outside Core and WidgetHost.

For provider setup, prefer safe automatic reuse of already-authorized official local state. One explicit human authorization/setup is allowed when genuinely required, but successful setup must persist and ordinary app startup must not repeatedly demand configuration. Refresh/session ownership is provider-specific; administrator rights do not substitute provider authorization.

Quota, credits, balance, billing, token activity, derived cost and service status are separate semantics. Missing telemetry is never zero.

Parallel provider work is allowed only with isolated write ownership. One worker owns one provider boundary; shared integration/auth infrastructure has one integration owner. Do not create a generic ProviderManager/Factory/framework until multiple implemented providers prove the abstraction.

Licensing remains part of implementation acceptance: Gengchou and TokenTracker are MIT at the pinned references and may be considered for focused adaptation with provenance/notice review; AI Limits is GPL-3.0-or-later and remains research-only unless a separate license decision permits reuse.

## D-025 — CER-M4-005 MetricId binding metadata

Keep `MetricRegistry` as the single in-process source for `MetricDescriptor`. `NamedPipeMetricServer` receives that same registry. Exact-ID `accepted` still means the bus subscription is active and will receive future publications only; it does not require descriptor readiness. If the descriptor exists at acceptance, include it there. If registration happens later, include it once with the first subsequent snapshot, whether that snapshot has a value or not. Unknown IDs remain accepted without fabricated metadata, and registration is never polled or used to reorder provider startup.

The descriptor carries semantic `MetricId`, `DisplayName`, `SemanticType`, `Unit`, `ValueKind`, and optional `Description`. WidgetHost binds its rows by exact `MetricId`, validates descriptor and snapshot IDs, and rejects a present value whose kind conflicts with the descriptor. The descriptor supplies display name and unit; numeric formatting stays local to the presentation. If metadata is absent, show the exact ID and no invented unit.

Keep protocol version 1: descriptor fields are optional additions, do not change subscription acceptance, replay, or snapshot semantics, and default readers tolerate unknown fields. New readers tolerate an older descriptor-free peer. Do not create a catalog, new message kind, registry feed, or multiplexed pipe.

After `accepted`, silence is not a transport failure. WidgetHost waits for a metric publication or actual pipe closure/cancellation; it shows `Waiting` before the first snapshot and preserves the last provider-reported snapshot while the pipe remains open. A closed pipe marks the subscription unavailable and reconnects. Providers own Fresh/Stale/Unavailable/Stopped/Error transitions. This accommodates slow publishers such as OpenCode activity and Codex quota without cadence inference or reconnect churn, while retaining CER-M4-004 single-flight connection establishment.

## D-026 — M4 exit criteria and scope (2026-11-14)

M4 listed fourteen items and no exit test, so it could not end. It now has five explicit exit criteria in `docs/ROADMAP.md`.

Three items leave M4. The taskbar surface: Windows 11 has no supported API for embedding content in the taskbar, and the known techniques depend on Explorer internals; it needs its own evaluation before any commitment. The simple graph: the sparkline covers bounded in-memory trends, and anything richer needs stored history, which is M7. The container component: the ordered composition is the container until the M5 editor needs more.

A tray indicator stays in M4 because the shell supports it and a monitoring widget must be able to run in the background.

## D-027 — Agent workflow (2026-11-14)

Process lives in `AGENTS.md`; `.agent/STATE.md` and `.agent/TASKS.md` are short and rewritten, not appended; routing is by capability tier. Verification is proportional to risk, evidence goes in PR descriptions, and a check that cannot be run is recorded as a limit instead of blocking a merge. This replaces the checkpoint-log workflow (HANDOFF/WORKLOG) and supersedes the process parts of D-006. Reason: the mandatory reading had grown past 130 KB and five finished PRs sat unmerged for a month behind gates that could not be run.

## D-028 — Function-first sequence: history before editor (2026-11-14)

The owner's priority is a complete, usable product: collect verifiable telemetry, record it locally, and be able to query and analyse it. The old order put the visual editor (M5) and rules (M6) before history (M7).

New order: M5 History & Recording, M6 Analysis & Dashboard, M7 Telemetry breadth, M8 Overlay & surfaces, M9 Rules/alerts/profiles, M10 Visual editor & personalisation, M11 Distribution. The plugin SDK, ecosystem and remote telemetry are unscheduled. The current visual design is provisional; the definitive identity waits for M10.

M4's remaining exit criteria (144 DPI sparkline case, dialog tokens and high contrast, idle cost) are finishing checks and do not block M5.

Initial direction for the history store, to be confirmed by the M5 design task: TelemetryService is the only writer; storage is SQLite through the Windows-provided `winsqlite3.dll` already used for layouts, so no new dependency; other processes open the file read-only.

## D-029 — Closing review of the project's foundations (2026-11-14)

A strategic pass checked what the original brief asked for. Result, without repeating earlier audits:

- **Foundations.** The `MetricId` contract, truthful states, process split and native UI stand. The agent workflow was replaced (D-027) and passed a clean-session run on a Standard-tier model. M4 got exit criteria (D-026) and the roadmap was resequenced to function first (D-028).
- **References.** `docs/REFERENCE_PROJECTS.md` already records role, pinned revision, licence and lessons for Gengchou (MIT; Windows shell and compact surfaces), AI Limits (GPL-3.0-or-later; ideas only, no code) and TokenTracker (MIT; acquisition breadth, local analytics, dashboard). Nothing to add. How these tools store history was not studied; that research belongs to CER-M5-001.
- **Public-repository security.** Tracked files contain no secret patterns, e-mail addresses or personal paths; commit authors use GitHub no-reply addresses; code scanning has no open alerts; CI workflows have read-only permissions. Gaps: GitHub secret scanning, push protection and Dependabot alerts are disabled (owner decision, see `TASKS.md`). The `.gitignore` patterns `**/*token*` and `**/*cookie*` would have silently ignored source files with those words in the name; source and docs are now exempt.
- **Routing and continuity.** Standard tier leads day-to-day work; the strategic tier plans and is never substituted silently (`MODEL_ROUTING.md`).
- **Harness.** No skill, hook, plugin or MCP server is required (`MODEL_ROUTING.md`, Harness).


## D-030 — History store design (2026-11-14)

Confirms the direction of D-028. Reason: the store must stay small and cheap, survive crashes, and never slow the bus.

- **Engine and location.** SQLite through the system `winsqlite3.dll` (no dependency), file `%LOCALAPPDATA%\Cerith\history.db`, WAL, `synchronous=NORMAL`. TelemetryService is the only writer. Readers (ControlCenter) open the file read-only directly; no pipe query protocol until a reader needs something a file cannot give. Its own file, not `layout.db`, so retention and corruption handling never touch user settings.
- **Schema** (`application_id` 0x43455248, `user_version` 1): `metric(id, name, kind)` dictionary; `sample(metric, at, state, confidence, num, txt)` keyed by `(metric, at)` without rowid, `at` in Unix milliseconds UTC, `num` untyped so integers stay exact; `rollup(bucket, metric, start, n, min, max, sum)`; `meta`.
- **Values.** Double/Int64/Boolean/TimeSpan(ticks)/DateTimeOffset(Unix ms) go in `num`; Decimal and String (≤1024 bytes) in `txt`, so decimals stay exact. A non-finite double, an oversized string or a value whose kind differs from the metric's first kind is rejected and counted, never coerced. Snapshots without a value are stored as their state with a null value (never zero), and only when the state changes.
- **Write path.** `MetricBus.Tap` calls an observer synchronously for every published snapshot; `HistoryRecorder` only enqueues into a bounded queue (16384), counts drops on overflow and logs them, and a writer flushes one transaction every 2 s. A process crash loses at most the unflushed 2 s; committed data survives a crash (WAL), and only an OS crash or power loss can lose the last few commits. A failed batch is dropped and counted; the service keeps running.
- **Tiers and retention.** Raw samples 12 h, one-minute rollups 90 days, one-hour rollups 730 days. Rollups hold count/min/max/sum of Fresh numeric values only (Stale is never averaged in). Maintenance runs at start and hourly: it rolls completed buckets up after a 5-minute delay, then prunes, and never deletes raw rows beyond the rollup watermark. The file does not shrink after pruning; space is reused.
- **Review fixes.** A Fresh sample is never overwritten by a Stale republish at the same instant; a late sample behind the rollup watermark rewinds it (and a watermark in the future after a clock jump is pulled back); rollups use `total()` so large integers cannot overflow; state rows are re-stored after pruning; the cache reloads after a failed batch; the recorder flushes on dispose and survives any exception.
- **Not in this slice.** Gaming sessions and AI usage events (CER-M5-002/003), a settings UI for retention, `VACUUM`.

## D-031 — Gaming sessions derived inside the history store (2026-11-14)

A session is the lifetime of the running game as already defined by D-022: it starts at the first Fresh `game.session.application` string and ends at the first non-Fresh value of that metric or a different label. `HistoryStore` derives sessions from the snapshots it records, so there is no new bus metric, IPC message or provider change, and it stores only the label D-022 already publishes (no PID, path or title). Tables `session` and `session_stat` (schema version 2, added in place with `IF NOT EXISTS`). Statistics (count, min, max, average) cover every Fresh numeric metric recorded while the session is open and are accumulated per batch, so they stay exact after raw samples are pruned. A session left open by a dead writer ends at the last instant the store saw (`meta.last_seen`) when the writer next opens, and on a clean dispose. Sessions and their statistics are kept as long as the hourly rollups (730 days). Readers use `HistoryStore.ReadSessions`.

## D-032 — AI activity events derived inside the history store (2026-11-14)

The OpenCode collector publishes only its latest activity as eight metrics in one round (one ObservedAt, `occurred_at` last). `HistoryStore` turns each distinct `occurred_at` with a complete Fresh round into one `ai_event` row (source, time, model id, provider id, five token counts), unique on `(source, at)` so repeated polls and restarts add nothing; values from different rounds are never mixed. Limit, stated honestly: activities superseded between two 5 s polls are never observed, so events are observed activity, not complete usage accounting. Schema version 3; events kept 730 days; `HistoryStore.ReadAiEvents`. Quota snapshots need nothing extra: they are ordinary metrics already recorded.

## D-033 — Strategic review: always-on app before the dashboard (2026-11-27)

Reviewed with the Strong tier at the owner's explicit request while the strategic tier was unavailable.

**Finding.** M5 can record everything, but in daily use it records almost nothing: the owner has to start a console TelemetryService and the widget by hand, nothing starts with Windows, and gaming telemetry only works when the service is launched with the game's `--pid`. A dashboard built now would show empty history.

**Decisions.**

1. **New M6 Always-on app**, before the dashboard: the UI app starts the service, both are single-instance, optional per-user start with Windows, automatic game detection from the foreground process that presents frames (moved up from telemetry breadth), executable icon, PresentMon-missing message, one-folder publish. Its exit includes the day-long measurement that closes M5.
2. **Two processes, not three.** All UI surfaces, including the dashboard, live in the WinUI process that is today `Cerith.WidgetHost`; the empty `Cerith.ControlCenter` project is removed. Reason: each WinUI process costs about 110 MB of working set before showing anything (`docs/PERFORMANCE.md`), and one tray and one entry point are simpler for the user. TelemetryService stays separate so recording survives UI failures and closed windows.
3. **User context, not elevated, not a Windows service.** AI providers need the user's own local sessions and credentials; a LocalSystem service cannot read them, and Cerith installs no kernel driver. Sensors that require elevation are reported Unavailable. An optional elevated hardware helper is decided in the distribution milestone if users need it.
4. **M4 closed with known limits.** Its remaining criteria were a hardware-dependent check (144 DPI), cosmetics the owner has deprioritised, and an idle measurement that is more meaningful for the whole app. They are carried forward, not dropped.
5. **Order after M6:** M7 Analysis & Dashboard, M8 Telemetry breadth, M9 Overlay & surfaces, M10 Rules/alerts/profiles, M11 Visual identity & personalisation (the canvas editor only if the dialog proves insufficient), M12 Distribution.

**Unchanged.** The `MetricId` contract, truthful states, the history design (D-030 to D-032), per-provider isolation and the local-first, no-cloud stance stand.

## D-034 — Automatic game detection: full-screen foreground, then proof by frames (2026-11-27)

Rule and constants are in `docs/METRICS.md` ("Automatic target selection"). Windows offers no documented "this is a game" signal for another process: `SHQueryUserNotificationState` names no process and also reports full-screen video, and `GameConfigStore` is undocumented, keyed by full paths (which Cerith does not read) and misses titles Game Bar never saw. So, like PresentMon's own overlay, Cerith follows the foreground process, filtered by "covers its monitor" and a short exclusion list, and asks PresentMon for proof before publishing anything, so a rejected process never creates a session. One capture is open at a time. Windowed games need `--pid`.

The target's identity is now held with a `SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION` handle instead of `Process.Handle` (full access), which anti-cheat protected games refuse; D-022's guarantee is unchanged because the PID cannot be reused while the handle is open. Amends D-022 ("no automatic detection").

## D-035 — Open source under GPL-3.0-or-later (2026-11-27, owner decision)

The owner chose open source and GPL-3.0 over Apache-2.0: whoever distributes a modified Cerith must publish its source. "Or later" follows the earlier draft in `OPEN_SOURCE_STRATEGY.md`. Because the app links Microsoft's proprietary Windows App SDK components, `LICENSE-EXCEPTION.md` grants an additional permission under GPL section 7. This is the project's own reading, not legal advice; adding the permission later would need every contributor's consent, so it is there from the start. Name and artwork are excluded.

`Mono.Posix.NETStandard` (D-019's open question) is no longer shipped (`ExcludeAssets="all"`): it serves LibreHardwareMonitor on Linux only, and the service reports the same hardware sample counts without it.

Consequence for research references: GPL-3.0-or-later projects such as AI Limits, and MIT ones such as TokenTracker, may now be adapted with attribution and notice review, never copied blindly (supersedes the "research-only" licensing note recorded for AI Limits).
