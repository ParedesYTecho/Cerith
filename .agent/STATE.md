# Estado actual

Proyecto: Cerith
Nombre: provisional
Hito: M0 — Foundation
Rama canónica: `main`
Estado: CER-M0-001 y CER-M0-002 completadas y mergeadas en `main`. CER-M0-003 está implementada en PR #8 y pendiente de review/merge del Advisor. No se han iniciado CER-M0-004 ni CER-M0-005.

## Listo

- Esqueleto de solución y referencias entre proyectos conforme a `docs/ARCHITECTURE.md`.
- Contratos fundamentales de métricas y sus pruebas integrados en `main`.
- `MetricRegistry` y sus pruebas están en la branch `feat/CER-M0-003-metric-registry`, PR #8.

## Verificación de CER-M0-002

- PR #6: review del Advisor aprobada; merge mediante squash en `e895e1666d376a22c15a31d6b809c5a6b716307d`.
- `dotnet build Cerith.sln --configuration Release`: PASS, 0 advertencias y 0 errores.
- `Cerith.Core.Tests`: PASS, 17/17.
- Telemetry.Tests e Integration.Tests siguen vacíos; cada uno conserva una tolerancia temporal individual.
- Una ejecución de Core filtrada a cero pruebas devuelve MTP `ZeroTests` (8) y falla sin tolerancia.
- GitHub Actions: Build and Test PASS; Repository Quality PASS; CodeQL SKIPPED según D-013.
- Los PR Dependabot #3 y #4 siguen siendo mantenimiento separado y no forman parte de CER-M0-002.

La política temporal por proyecto de `ZeroTests` está registrada en D-012.

## Siguiente

El Advisor revisa y mergea PR #8. Después, sincronizar `main` y reconciliar la memoria del repo. No iniciar CER-M0-004 ni CER-M0-005 antes de ese cierre.
