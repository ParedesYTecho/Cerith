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
