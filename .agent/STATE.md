# Estado actual

Proyecto: Cerith
Nombre: provisional
Hito: M0 — Foundation DONE; M1 — Hardware MVP DONE; M2 — Gaming MVP DONE; M3 — AI Quotas MVP ACTIVE / INCOMPLETE
Rama canónica: `main`
Estado: CER-M3-001/002/003 DONE; CER-M3-004 IN PROGRESS y M3 ACTIVE / INCOMPLETE. Nueve integraciones no-Codex están staged: ocho conservan sus FINAL PASS anteriores y Ark Agent Plan se implementó en `0c9ba1c04b1899a502f5fc554a7dc8196a41dd5f`, importado con historial por merge `1d5cb488e56600124ced8e6e8b3cf537ff3783e2`; la revisión global de misión sigue pendiente. Release build: 0 warnings/errors; MTP directo con mínimo esperado: Core 39/39, Telemetry 335/335, Integration 10/10; Ark Agent 19/19, coexistencia 1/1 y aislamiento del supervisor 10/10. Graphify code graph: 3.028 nodos, 8.283 aristas y 120 comunidades; no se ejecutó etiquetado LLM. Hygiene de nombres y `git diff --check` pasan; escaneo de patrones sensibles en el diff: 0 coincidencias. PR #47 sigue draft; `main` local `3f5ca74a26edf88a02e6bda05f759ee3c74032cd` y `origin/main` `72b43b268b4992d0b0ea45b6e29067d10450274f` permanecen intactas. Ark Agent Plan es `LIVE_NOT_AVAILABLE_ON_THIS_HOST` porque no hay CLI/perfil local compatible ni sesión autenticada y la consola oficial pidió login; se usaron documentación primaria, acción CLI verificada en dry-run y pruebas deterministas. Copilot es `LIVE_VERIFIED` sólo para porcentajes remaining frente a la UI de la misma cuenta; resets ausentes siguen sin valor. Qoder CN también es `LIVE_NOT_AVAILABLE_ON_THIS_HOST`; Cursor auth queda `DEFERRED/UNRESOLVED` y live NOT RUN. Qoder International y Ark Coding Plan continúan como trabajo de sus owners, con worktrees limpios observados en `183a47c932b8a76f2d2058ebcc95b10cca09fddb` y `c5841bf2ee6140d61ee517db62d8b176a996ce21`. Se conservan las disposiciones previas de Claude, Gemini, Kimi, Grok Build y Devin para continuarlas con las rutas seguras disponibles. No hay gates Sol por proveedor; la única revisión externa GPT-5.6 Sol High Web se reserva al candidato de misión completo y requiere recibo verificable de modelo/ruta. Sin merge a main ni declaración de misión completa.

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

Mantener PR #47 draft y `main` intacta. Dejar a Qoder International y Ark Coding Plan continuar en sus worktrees existentes hasta handoff autónomo; conservar sus findings. Seguir con máxima validación segura de Claude, Gemini CLI / Code Assist, Kimi, Grok Build y Devin según sus disposiciones preservadas. Integrar próximos handoffs completos en grupos pequeños y verificar Release/coexistencia antes de avanzar. No solicitar gates Sol por proveedor; la revisión global GPT-5.6 Sol High Web sólo corresponde cuando el candidato de misión esté completo y el modelo/ruta puedan verificarse. M3 y la misión de 16 providers siguen incompletas.

PR #23 de context integrity/provenance se integró en `94de88be841968be18fd6d3225bd184044347af6`; CER-M1-001 se integró después en `b7e40447f48cd6afd2b015a94a49d825f28099f9`, sin conflicto.

Deuda explícita:

- Revisar la compatibilidad de Windows App SDK y sus transitivas con la licencia antes de fijar o publicar la licencia definitiva.
- El linked source de `NamedPipeMetricClient` en Integration.Tests es un workaround limitado de M0, no un patrón permanente.
- `Mono.Posix.NETStandard 1.0.0`: no se halló incompatibilidad conocida; la licencia/SPDX exacta sigue siendo una deuda de provenance/third-party-notice que debe resolverse o documentarse antes del public release.
- CER-M1-002 se probó localmente con una CPU y una GPU AMD; NVIDIA/Intel, sensores ausentes y múltiples GPUs no están validados en máquina real. `gpu.N` no garantiza identidad física persistente entre reinicios o reenumeración. CI no depende de hardware.

Dependabot #3/#4 se integraron posteriormente como mantenimiento de GitHub Actions, sin cambio de comportamiento de producto. Graphify label drift permanece separado.

Futuro no activo: `docs/ROADMAP.md` y los documentos de producto registran una expansión posterior de telemetría hardware/gaming (incluyendo CPU_FAN/GPU fan verificables, métricas CPU/GPU adicionales y separación de FPS) y un modo Test / Diagnostic Session para validación reproducible y comparación externa. Nada de ello cambia el alcance de M2 ni inicia M4; M3 está ACTIVE / INCOMPLETE.
