# Handoff — CER-M0-007 Named Pipe snapshot bridge

Fecha: 2026-09-26
Estado: implementación DONE; review independiente pendiente; no merge ni CER-M0-008.
Branch: `feat/CER-M0-007-named-pipe-bridge`
Base: `origin/main` `4eb610b71247c279e5538078a93742565e4cedcf`
HEAD de código: `f29c8d3ef0d2fa3a988e656390c5f7abb79824d5` (el commit de cierre de handoff será posterior)
PR: https://github.com/ParedesYTecho/Cerith/pull/16 contra `main`; sin merge

## Diseño y archivos

`src/Cerith.Core/MetricIpcProtocol.cs` es el contrato compartido: protocolo v1, frames byte stream con prefijo big-endian Int32 y límite duro de 64 KiB, JSON BCL con `subscribe`, `accepted` y `snapshot`. Codifica los siete tipos de `MetricValue` sin alterar contratos ni confundir ausencia y cero. Es interno M0, no API plugin estable.

`src/Cerith.TelemetryService/NamedPipeMetricServer.cs` crea el pipe con Win32 `CreateNamedPipeW`, `FILE_FLAG_OVERLAPPED` y `PIPE_REJECT_REMOTE_CLIENTS`. Una DACL protegida creada mediante SDDL concede acceso solo al SID del usuario actual; el descriptor se libera tras crear el handle. `NamedPipeServerStream` envuelve ese handle para E/S async y cancelación. Cada conexión lee un ID exacto, inicia la suscripción a `MetricBus`, envía `accepted`, transmite snapshots futuros y cancela/retira la suscripción tras desconexión. Una nueva instancia acepta al cliente siguiente. Un cliente por conexión y una conexión simultánea en M0.

`src/Cerith.WidgetHost/NamedPipeMetricClient.cs` conecta a `.` con `CurrentUserOnly`, espera `accepted` antes de completar `ConnectAsync`, lee snapshots y libera el pipe. No hay retry automático. `tests/Cerith.Integration.Tests/NamedPipeMetricTests.cs` protege flujo real, round-trip de valores/estados, casing, ausencia de replay, cleanup/reconnect, cancelación, frames inválidos y seguridad del pipe.

Modificados: `.github/workflows/build.yml` retira la tolerancia ZeroTests; `.agent/DECISIONS.md` registra D-017 y actualiza D-012; `docs/ARCHITECTURE.md` corrige el transporte; `.agent/TASKS.md`, `.agent/STATE.md`, `.agent/WORKLOG.md` y este handoff registran el estado. Los cuatro archivos nuevos separan contrato, servidor, cliente y pruebas; todos tienen consumidores actuales. Sin proyectos ni paquetes nuevos.

## Evidencia

En Windows, `GetNamedPipeInfo` sobre el cliente conectado devolvió el bit `0x8` para el servidor Cerith; un pipe de control creado con BCL `CurrentUserOnly` devolvió `0x0`. Un cliente local del usuario actual conectó. No se ejecutó prueba desde otra máquina ni otra cuenta. El test compara el flag efectivo; la DACL de usuario actual se configura en la creación del handle.

Release build: PASS, 0 warnings/errors. Core.Tests: 39/39 PASS; Telemetry.Tests: 7/7 PASS; Integration.Tests: 8/8 PASS. `git diff --check`: PASS. Graphify code-only: 407 nodos, 845 aristas. CI remoto del HEAD de código `f29c8d3`: Build and Test PASS, Repository Quality PASS, CodeQL SKIPPED según D-013. El commit de cierre de handoff provocará una nueva ejecución de CI.

Riesgos/limitaciones: el protocolo M0 permite una sola métrica y una conexión simultánea; no tiene replay ni negociación de versiones ni retry automático. La política de composición/arranque de procesos queda para la siguiente task. Review del Advisor pendiente, especialmente native creation, lifecycle y cancelación. Dependabot #3/#4 y Graphify label drift permanecen fuera de scope.
