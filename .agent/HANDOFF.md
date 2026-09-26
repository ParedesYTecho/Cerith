# Handoff — CER-M0-007 post-merge

Fecha: 2026-09-26
Estado: CER-M0-007 DONE; PR #16 pasó review independiente del Advisor con PASS y se integró mediante squash.
Rama canónica: `main`
`main` y `origin/main`: `192afc43632ea50fe92e6416e65431ce0bbe7da8`
PR: https://github.com/ParedesYTecho/Cerith/pull/16

## Resultado integrado

D-017 registra el contrato IPC M0. El bridge Named Pipe real permite el recorrido `SyntheticProvider → MetricBus → Named Pipe → WidgetHost client`. El protocolo v1 usa frames length-prefixed con máximo de 64 KiB y mensajes `subscribe`, `accepted` y `snapshot`; una conexión se suscribe a un único MetricId exacto, sin replay.

El servidor crea un pipe Win32 overlapped con `PIPE_REJECT_REMOTE_CLIENTS` y ACL protegida para el usuario actual. `GetNamedPipeInfo` confirmó el bit efectivo `0x8`; el pipe de control sin rechazo devolvió `0x0`. El cliente local conecta con `CurrentUserOnly`. No se ejecutó ni se afirmó prueba desde otra máquina ni otra cuenta.

La suscripción del MetricBus se activa antes de enviar `accepted`; la desconexión cancela y retira la suscripción y permite reconectar. El framing, los siete tipos de MetricValue, estados y campos del snapshot, cancelación, casing, ausencia de replay y frames inválidos están cubiertos por Integration.Tests.

## Archivos de implementación integrados

- `src/Cerith.Core/MetricIpcProtocol.cs`
- `src/Cerith.TelemetryService/NamedPipeMetricServer.cs`
- `src/Cerith.WidgetHost/NamedPipeMetricClient.cs`
- `tests/Cerith.Integration.Tests/NamedPipeMetricTests.cs`
- `.github/workflows/build.yml`, `docs/ARCHITECTURE.md`, `.agent/DECISIONS.md` (D-017), además de la memoria operativa.

No se añadieron paquetes ni proyectos. CER-M0-008 todavía no está reclamada; no iniciarla en esta reconciliación. Dependabot #3/#4 permanece separado. Graphify label drift sigue pendiente como tooling.

## Verificación

- Core.Tests: 39/39 PASS.
- Telemetry.Tests: 7/7 PASS.
- Integration.Tests: 8/8 PASS.
- Los tres proyectos se ejecutan sin tolerancia ZeroTests.
- Build and Test: PASS.
- Repository Quality: PASS.
- CodeQL: SKIPPED según D-013.
- Reconciliación: `main` local y `origin/main` exactas en el merge SHA indicado; working tree limpio antes de los cambios operativos.
