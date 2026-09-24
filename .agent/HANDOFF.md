# Handoff post-merge — CER-M0-005 SyntheticProvider

Fecha: 2026-09-25
Estado: DONE; PR #12 pasó review independiente del Advisor con PASS y se mergeó mediante squash en `main`.
Merge SHA: `729075dcfcef1ae849b9339a96b438d234e7ff37`
PR: https://github.com/ParedesYTecho/Cerith/pull/12

## Cambio

- `SyntheticProvider` y el descriptor `synthetic.counter` están integrados en `Cerith.TelemetryService`; registra con `MetricRegistry` y publica snapshots por `MetricBus` usando `TimeProvider`.
- `Core.Tests` y `Telemetry.Tests` se ejecutan sin tolerancia ZeroTests. Solo `Integration.Tests`, mientras permanezca vacío, conserva la excepción temporal.
- No se implementaron scheduling, IPC ni integración WidgetHost.

## Verificación

- PASS — CI Build and Test.
- PASS — CI Repository Quality.
- SKIPPED — CodeQL según D-013.
- PASS — Release build local sin warnings/errores; Core 39/39 y Telemetry 7/7.
- PASS — Integration.Tests conserva temporalmente ZeroTests; ejecución local: 0 pruebas.
- PASS — `git diff --check` en la reconciliación post-merge.
- Graphify code-only: 317 nodos y 546 aristas; 64 comunidades y 62 etiquetas guardadas. La desincronización queda como tooling pendiente, sin abrir task de producto.

## Siguiente

CER-M0-006 permanece TODO/unclaimed y no hay task de integración WidgetHost reclamada. Dependabot #3/#4 sigue separado.
