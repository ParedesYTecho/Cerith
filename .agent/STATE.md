# Estado actual

Proyecto: Cerith
Nombre: provisional
Hito: M0 — Foundation DONE; M1 — Hardware MVP DONE; M2 — Gaming MVP DONE; M3 — AI Quotas MVP ACTIVE / INCOMPLETE
Rama canónica: `main`
Estado: CER-M3-001/002/003 DONE; M3 ACTIVE / INCOMPLETE bajo D-024. CER-M3-004 sigue IN PROGRESS en `codex/CER-M3-004-integration` con Cursor, Antigravity, ZCode, GitHub Copilot y OpenCode Go FINALPASS integrados; ambos nuevos tips preservan sus historias no-squash. Release build: 0 warnings/errors; Core 39/39, Telemetry 211/211 en ejecuciones por clase, Integration 10/10; prueba de coexistencia misma-bus durante solicitud Cursor pendiente: PASS. Un run local combinado y el Actions `36906898017` reportaron todos los casos Telemetry correctos pero terminaron por unhandled `PeriodicTimer.Dispose` finalizer `NullReferenceException`; el Actions más reciente `36907835155` en `60c9422` pasó Build/Core/Telemetry/IPC y hygiene. Registrar el fallo anterior como anomalía intermitente, sin fallo en el CI actual. Cursor auth DEFERRED/UNRESOLVED y live UI NOT RUN; Antigravity probe sólo schema/identity, producción/UI/cache/version pendientes; Copilot renewal class unknown; OpenCode Go SETUP/LIVE pending, no MFA. Owner Dewey One / GPT-6 Luna Max, runtime `turn_context` verificado 2026-10-01T17:46:59.513Z. Main reviewer externo GPT-6.1 Sol Medium/read-only (última verificación runtime 11:45:23.649Z). Kiro espera FINAL review; Command Code handoff MID `3cac1e552b5e5a87441e1b85cee81a3e0a0b6cbd` espera a Sol; Grok Build, Claude, Gemini y Kimi continúan BLOCKED en sus tips preservados. PR #47 sigue draft. `main` local continúa `3f5ca74a26edf88a02e6bda05f759ee3c74032cd` y `origin/main` `72b43b268b4992d0b0ea45b6e29067d10450274f` (mission base); no se movió ni fusionó canonical main. Misión de 16 providers y M3 siguen incompletos.

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

La wave de integración incorporó los FINALPASS exactos de Copilot y OpenCode Go, preservando ambas historias y el supervisor existente. Mantener PR #47 en draft. Kiro espera FINAL review y el handoff MID de Command Code espera a Sol; integrar únicamente futuros tips FINALPASS explícitos. Copilot renewal sigue unknown, OpenCode Go SETUP/LIVE pending, Antigravity conserva sólo evidencia RPC de schema/identidad, Cursor auth sigue DEFERRED/UNRESOLVED y su comparación UI NOT RUN. La suite Telemetry requiere resolver la salida de proceso por `PeriodicTimer.Dispose` finalizer aun cuando los 211 casos pasan aislados. No fusionar a `main`; los 16 targets y M3 permanecen activos.

PR #23 de context integrity/provenance se integró en `94de88be841968be18fd6d3225bd184044347af6`; CER-M1-001 se integró después en `b7e40447f48cd6afd2b015a94a49d825f28099f9`, sin conflicto.

Deuda explícita:

- Revisar la compatibilidad de Windows App SDK y sus transitivas con la licencia antes de fijar o publicar la licencia definitiva.
- El linked source de `NamedPipeMetricClient` en Integration.Tests es un workaround limitado de M0, no un patrón permanente.
- `Mono.Posix.NETStandard 1.0.0`: no se halló incompatibilidad conocida; la licencia/SPDX exacta sigue siendo una deuda de provenance/third-party-notice que debe resolverse o documentarse antes del public release.
- CER-M1-002 se probó localmente con una CPU y una GPU AMD; NVIDIA/Intel, sensores ausentes y múltiples GPUs no están validados en máquina real. `gpu.N` no garantiza identidad física persistente entre reinicios o reenumeración. CI no depende de hardware.

Dependabot #3/#4 se integraron posteriormente como mantenimiento de GitHub Actions, sin cambio de comportamiento de producto. Graphify label drift permanece separado.

Futuro no activo: `docs/ROADMAP.md` y los documentos de producto registran una expansión posterior de telemetría hardware/gaming (incluyendo CPU_FAN/GPU fan verificables, métricas CPU/GPU adicionales y separación de FPS) y un modo Test / Diagnostic Session para validación reproducible y comparación externa. Nada de ello cambia el alcance de M2 ni inicia M4; M3 está ACTIVE / INCOMPLETE.
