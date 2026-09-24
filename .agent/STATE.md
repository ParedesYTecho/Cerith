# Estado actual

Proyecto: Cerith
Nombre: provisional
Hito: M0 — Foundation
Rama canónica: `main`
Estado: CER-M0-001, CER-M0-002 y CER-M0-003 completadas y mergeadas en `main`. CER-M0-004 implementada en `feat/CER-M0-004-metric-bus`, pendiente de review independiente y merge. CER-M0-005 sigue sin reclamar.

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

`MetricBus` distribuye snapshots por `MetricId` exacto a múltiples suscriptores con buffers acotados y backpressure. Cancelación y disposal cierran las suscripciones; no hay replay ni dependencia de `MetricRegistry`. D-015 y `docs/METRICS.md` recogen el contrato. Verificación local: Release build PASS (0 advertencias/errores), Core 39/39 PASS, `git diff --check` PASS. Graphify code-only actualizado a 286 nodos y 463 aristas; la reextracción semántica documental sigue pendiente. CI remoto y review del Advisor pendientes.

## Siguiente

Review independiente de CER-M0-004 y merge por el Advisor si obtiene PASS. CER-M0-005 permanece `TODO` / `unclaimed`. Dependabot #3/#4 sigue separado.
