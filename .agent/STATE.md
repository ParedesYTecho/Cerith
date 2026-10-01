# Estado actual

Proyecto: Cerith
Nombre: provisional
Hito: M0 — Foundation DONE; M1 — Hardware MVP DONE; M2 — Gaming MVP DONE; M3 — AI Quotas MVP ACTIVE / INCOMPLETE
Rama canónica: `main`
Estado: CER-M3-001/002/003 DONE; M3 ACTIVE / INCOMPLETE bajo D-024. CER-M3-004 sigue IN PROGRESS en la rama preservada codex/CER-M3-004-integration, HEAD 2640d434cbd515fb04edec8ba0d8e06f85370ea6. Cursor FINALPASS y Antigravity FINALPASS están preservados en staging; Sol aprobó el follow-up acotado de coexistencia/lifecycle en ese HEAD, con Release enfocado independiente 8/8 y diff completo de nueve archivos/diffcheck limpio. Las tres suites Release, higiene configurada, privacidad de diff acotada y coexistencia quedaron verificadas en el checkpoint previo. Cursor auth de silent startup está DEFERRED/UNRESOLVED tras una traza oficial acotada que confirmó Cursor 3.22.12 firmado pero no pudo extraer su payload Inno 6.4.0.1 con el extractor upstream verificado hasta 6.3.3; no se probó la vida/renovación del token. La comparación live UI/cuota permanece NOT RUN. Owner de integración: Dewey One / GPT-6 Luna, esfuerzo Max, última verificación autoritativa turn_context 2026-10-01 08:26:49.296 UTC; Main reviewer route requested by user: GPT-5.6 Sol via ChatGPT Web; actual same-thread runtime override remains unverified. No routing policy change is recorded before authoritative proof. Cinco workers activos: Kiro, Grok Build, Copilot, ZCode y OpenCode Go; ZCode devolvió MID FIXES en 962ee24 y Averroes continúa Luna Max en su rama. Claude, Gemini y Kimi están BLOCKED en sus tips preservados. No importar otro proveedor hasta resolver la aceptación del grupo actual; sin merge a main, sin framework/dependencia genérica de auth y sin cierre de M3.

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

CER-M3-004 permanece IN PROGRESS en HEAD 2640d434cbd515fb04edec8ba0d8e06f85370ea6. Cursor y Antigravity son los únicos imports de proveedor del staging; Sol bounded follow-up review PASS en 2640d43, independiente Release enfocado 8/8. El gate Cursor de vida/renovación de silent startup queda DEFERRED/UNRESOLVED por payload oficial opaco (3.22.12 firmado; Inno 6.4.0.1 supera soporte del extractor upstream verificado); comparación live de cuenta/UI NOT RUN. No importar el siguiente grupo hasta resolver la aceptación actual. Kiro, Grok Build, Copilot, ZCode y OpenCode Go siguen activos; ZCode está en MID FIXES 962ee24 bajo Averroes. Main reviewer route requested by user: GPT-5.6 Sol via ChatGPT Web; actual same-thread runtime override remains unverified. No hay merge canónico ni cierre de M3; los 16 targets siguen activos.

PR #23 de context integrity/provenance se integró en `94de88be841968be18fd6d3225bd184044347af6`; CER-M1-001 se integró después en `b7e40447f48cd6afd2b015a94a49d825f28099f9`, sin conflicto.

Deuda explícita:

- Revisar la compatibilidad de Windows App SDK y sus transitivas con la licencia antes de fijar o publicar la licencia definitiva.
- El linked source de `NamedPipeMetricClient` en Integration.Tests es un workaround limitado de M0, no un patrón permanente.
- `Mono.Posix.NETStandard 1.0.0`: no se halló incompatibilidad conocida; la licencia/SPDX exacta sigue siendo una deuda de provenance/third-party-notice que debe resolverse o documentarse antes del public release.
- CER-M1-002 se probó localmente con una CPU y una GPU AMD; NVIDIA/Intel, sensores ausentes y múltiples GPUs no están validados en máquina real. `gpu.N` no garantiza identidad física persistente entre reinicios o reenumeración. CI no depende de hardware.

Dependabot #3/#4 se integraron posteriormente como mantenimiento de GitHub Actions, sin cambio de comportamiento de producto. Graphify label drift permanece separado.

Futuro no activo: `docs/ROADMAP.md` y los documentos de producto registran una expansión posterior de telemetría hardware/gaming (incluyendo CPU_FAN/GPU fan verificables, métricas CPU/GPU adicionales y separación de FPS) y un modo Test / Diagnostic Session para validación reproducible y comparación externa. Nada de ello cambia el alcance de M2 ni inicia M4; M3 está ACTIVE / INCOMPLETE.
