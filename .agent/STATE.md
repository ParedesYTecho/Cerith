# Estado actual

Proyecto: Cerith
Nombre: provisional
Hito: M0 — Foundation
Rama canónica: `main`
Estado: CER-M0-001 a CER-M0-005 completadas y mergeadas en `main`. CER-M0-005 pasó review independiente del Advisor con PASS y PR #12 se integró mediante squash en `729075dcfcef1ae849b9339a96b438d234e7ff37`. CER-M0-006 sigue TODO/unclaimed; no hay task de integración WidgetHost reclamada.

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

`SyntheticProvider` y `synthetic.counter` están integrados en `main`. PR #12 pasó review independiente del Advisor con PASS y se mergeó mediante squash en `729075dcfcef1ae849b9339a96b438d234e7ff37`. Build and Test: success; Repository Quality: success; CodeQL: skipped según D-013. `Core.Tests` y `Telemetry.Tests` se ejecutan sin tolerancia ZeroTests; solo `Integration.Tests` conserva la excepción temporal. La verificación local fue Release build PASS (0 warnings/errores), Core 39/39 y Telemetry 7/7; Integration 0 tests con tolerancia. Graphify code-only quedó en 317 nodos y 546 aristas; la desincronización de etiquetas (64 comunidades, 62 etiquetas) queda como tooling pendiente, sin task de producto.

## Siguiente

CER-M0-006 sigue TODO/unclaimed. No existe task de integración WidgetHost reclamada. Dependabot #3/#4 permanece separado.
