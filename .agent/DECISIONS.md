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

`Cerith.Core.Tests` se ejecuta sin tolerancia. Solo `Cerith.Telemetry.Tests` y `Cerith.Integration.Tests`, mientras sigan vacíos, pueden ignorar el código 8 en sus invocaciones individuales. Retirar cada excepción al añadir su primer test real; no añadir tests dummy.

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
