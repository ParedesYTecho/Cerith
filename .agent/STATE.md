# Estado actual

Proyecto: Cerith
Nombre: provisional
Hito: M0 — Foundation
Rama canónica: `main`
Estado: CER-M0-001 completada y mergeada. CER-M0-002 está implementada en `feat/CER-M0-002-metric-contracts`, pendiente de review del Advisor; todavía no está mergeada.

## Listo

- Esqueleto de solución y referencias entre proyectos conforme a `docs/ARCHITECTURE.md`.
- Contratos fundamentales de métricas y 17 pruebas unitarias reales en la branch de CER-M0-002.
- No se han iniciado CER-M0-003 ni tareas posteriores.

## Verificación de CER-M0-002

- Base `main`: `f42f9351cf39441b7a3406d6ee55eebed5ad20f6`.
- Commit de implementación: `f0b318d92052378b206328dad6257ce17b0fcf78`.
- `dotnet build Cerith.sln --configuration Release`: PASS, 0 advertencias y 0 errores.
- `Cerith.Core.Tests`: PASS, 17/17.
- Telemetry.Tests e Integration.Tests siguen vacíos; cada uno conserva una tolerancia temporal individual.
- Una ejecución de Core filtrada a cero pruebas devuelve MTP `ZeroTests` (8) y falla sin tolerancia.
- Repository Quality local: PASS; CodeQL no está disponible para el repositorio privado según D-013.

La política temporal por proyecto de `ZeroTests` está registrada en D-012.

## Siguiente

Review del Advisor para CER-M0-002; no iniciar CER-M0-003 antes de cerrar esta task.
