# Estado actual

Proyecto: Cerith
Nombre: provisional
Hito: M1 — Hardware MVP DONE; CER-M2-001 lista para revisión del Advisor; M2 incompleto
Rama canónica: `main`
Estado: CER-M2-001 — READY FOR ADVISOR REVIEW en `codex/CER-M2-001-presentmon-display`, basado en `main` `89ddd69c13affd76b3b6085a25753bb7b83b0a3f`; la corrección de Advisor parte de `c63e25110e4c18be3a6698e58b80cbbd4f411dba`. M0 y M1 completados; M2 no está completo y CER-M2-002 no ha comenzado.

## Listo

- Esqueleto de solución y referencias entre proyectos conforme a `docs/ARCHITECTURE.md`.
- Contratos fundamentales de métricas y sus pruebas integrados en `main`.
- `MetricRegistry` y sus pruebas están integrados en `main` mediante PR #8.

## Verificación de CER-M0-002

- PR #6: review del Advisor aprobada; merge mediante squash en `e895e1666d376a22c15a31d6b809c5a6b716307d`.
- `dotnet build Cerith.sln --configuration Release`: PASS, 0 advertencias y 0 errores.
- `Cerith.Core.Tests`: PASS, 17/17.
- Telemetry.Tests e Integration.Tests siguen vacíos; cada uno conserva una tolerancia temporal individual.
- Una ejecución de Core filtrada a cero pruebas devuelve MTP `ZeroTests` (8) y falla sin tolerancia.
- GitHub Actions: Build and Test PASS; Repository Quality PASS; CodeQL SKIPPED según D-013.
- Los PR Dependabot #3 y #4 siguen siendo mantenimiento separado y no forman parte de CER-M0-002.

La política temporal por proyecto de `ZeroTests` está registrada en D-012.

## Verificación de CER-M0-003

- PR #8: review del Advisor PASS; merge mediante squash en `00e8a9f1d35d9d96b754298a55ff4cfb416bcc71`.
- Build and Test: success; Repository Quality: success; CodeQL: skipped según D-013.
- `main` local y `origin/main` coinciden en el SHA del merge; working tree limpio durante la reconciliación.
- Los PR Dependabot #3 y #4 siguen siendo mantenimiento separado.

## CER-M0-004

`MetricBus` distribuye snapshots por `MetricId` exacto a múltiples suscriptores con buffers acotados y backpressure. Cancelación y disposal cierran las suscripciones; no hay replay ni dependencia de `MetricRegistry`. D-015 y `docs/METRICS.md` quedaron integrados. PR #10 pasó review del Advisor con PASS y se mergeó mediante squash en `bd305ae1b2975c8d3a81125688ec21639ab90817`. Build and Test: success; Repository Quality: success; CodeQL: skipped según D-013. Verificación local: Release build PASS (0 advertencias/errores), Core 39/39 PASS y `git diff --check` PASS. Graphify code-only: 286 nodos y 463 aristas; reextracción semántica documental pendiente.

## CER-M0-005

`SyntheticProvider` publica `synthetic.counter` determinista mediante `MetricBus`, registra su descriptor en `MetricRegistry` y usa `TimeProvider`. Los valores generados usan confidence `Unknown` porque no son mediciones ni estimaciones de telemetría real. PR #12 pasó review independiente del Advisor con PASS y se integró mediante squash en `main` (`729075dcfcef1ae849b9339a96b438d234e7ff37`). Build and Test y Repository Quality: success; CodeQL SKIPPED según D-013.

## CER-M0-006

Se compararon Named Pipes, TCP loopback y AF_UNIX con dos runs de 5000 round trips por candidato/payload (256 y 4096 bytes), cada uno precedido por 200 warmups. Las tres opciones pasaron reconnect/cancellation/cleanup. D-016 quedó integrada y elige Windows Named Pipes para la primera implementación; los resultados están en `docs/PERFORMANCE.md`. PR #14 pasó review independiente del Advisor con PASS y se mergeó mediante squash en `dc6c051f0734fc5eb8a301a16bda671d2ba94221`. No se implementó IPC de producto. La implementación futura debe conservar el requisito local-only y demostrar el rechazo de clientes remotos; `CurrentUserOnly` por sí solo no basta.

## Siguiente

M0 — Foundation DONE tras Advisor y PR #20. CER-M1-001/002/003/004 están DONE e integradas en `main`. CER-M1-004 recibió PASS final del Advisor; PR #29 pasó Build and Test y Repository Quality (CodeQL SKIPPED según política) y se integró por squash en `59ecb648a9f714376578fc16cdaa446571bd4874`. M1 — Hardware MVP DONE tras la auditoría de `docs/ROADMAP.md`. CER-M2-001 implementa FPS y frame time mostrados con PresentMon oficial y ha pasado Release build, suites y validación D3D11 real por pipe y WidgetHost. Una ejecución same-PID observó `Fresh` antes y después de la fase de dos cadenas, con estados `Error/multiple_swap_chains`, `Unavailable` y `Fresh` intercalados durante esa fase. El rerun instrumentado confirmó `Present` exitosos en ambas cadenas, pero no reprodujo esa secuencia; el count de PresentMon es por filas de consulta, no por objetos activos. Los detalles y límite están en `.agent/HANDOFF.md`. DLL y licencia MIT están presentes en build/publish. Queda para revisión del Advisor. M2 sigue incompleto y CER-M2-002 no iniciada. La prueba elevada del servicio/LHM 0.9.6 descubrió cuatro Storage y publicó usage/total/free coherentes `Fresh`/`Measured` por IPC; sin elevación descubrió cero en esta máquina. Una señal de consola dirigida produjo `TelemetryService stopped.` y exit code 0. Read/write throughput, identidad física persistente y algunas topology changes siguen fuera del baseline. Los fallos internos de LHM pueden ser invisibles para Cerith.

PR #23 de context integrity/provenance se integró en `94de88be841968be18fd6d3225bd184044347af6`; CER-M1-001 se integró después en `b7e40447f48cd6afd2b015a94a49d825f28099f9`, sin conflicto.

Deuda explícita:

- Revisar la compatibilidad de Windows App SDK y sus transitivas con la licencia antes de fijar o publicar la licencia definitiva.
- El linked source de `NamedPipeMetricClient` en Integration.Tests es un workaround limitado de M0, no un patrón permanente.
- `Mono.Posix.NETStandard 1.0.0`: no se halló incompatibilidad conocida; la licencia/SPDX exacta sigue siendo una deuda de provenance/third-party-notice que debe resolverse o documentarse antes del public release.
- CER-M1-002 se probó localmente con una CPU y una GPU AMD; NVIDIA/Intel, sensores ausentes y múltiples GPUs no están validados en máquina real. `gpu.N` no garantiza identidad física persistente entre reinicios o reenumeración. CI no depende de hardware.

Dependabot #3/#4 y Graphify label drift permanecen separados.
