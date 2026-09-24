# Handoff — CER-M0-005 SyntheticProvider

Fecha: 2026-09-25
Estado: implementación enviada en PR #12 contra `main`; abierta y sin merge, a la espera de review independiente del Advisor.
Branch: `feat/CER-M0-005-synthetic-provider`
Base verificada: `e79be08b342a8123748a98bf4da05723728f535b`
Commit de implementación: `11575ded86d94fb3567cccf0f6c91990b2930f48`
PR: https://github.com/ParedesYTecho/Cerith/pull/12

## Cambio

- `src/Cerith.TelemetryService/SyntheticProvider.cs`: descriptor único `synthetic.counter`, registro first-wins y publicaciones Int64 `0, 1, 2, ...` con `MetricBus` y `TimeProvider`. Usa `MetricConfidence.Unknown` porque el valor generado no mide ni estima telemetría real.
- `tests/Cerith.Telemetry.Tests/SyntheticProviderTests.cs`: siete pruebas de contrato/comportamiento, incluidas secuencia, timestamp controlado, ausencia de replay, cancelación y dependencias nulas.
- `.github/workflows/build.yml`: Telemetry.Tests ya corre sin tolerancia ZeroTests; Integration.Tests mantiene la excepción temporal.
- `.agent/TASKS.md`: CER-M0-005 en IN REVIEW; M0-006 permanece TODO/unclaimed.

No se implementaron loop, timer, hosted service, scheduling, IPC, WidgetHost ni abstracciones genéricas.

## Verificación

- PASS — `dotnet build Cerith.sln --configuration Release`: 0 advertencias y 0 errores.
- PASS — `Cerith.Core.Tests`: 39/39 sin tolerancia ZeroTests.
- PASS — `Cerith.Telemetry.Tests`: 7/7 sin tolerancia ZeroTests.
- PASS — `Cerith.Integration.Tests`: cero pruebas, salida 0 con la excepción temporal existente.
- PASS — `git diff --cached --check` antes del commit.
- PASS — chequeo Repository Quality de nombres de artefactos privados: 61 archivos tracked, sin coincidencias.
- PASS — CI del PR #12: Build and Test y Repository Quality; CodeQL SKIPPED según D-013.
- PASS — Graphify code-only: 317 nodos y 546 aristas. Reportó 64 comunidades frente a 62 etiquetas guardadas; etiquetas pendientes de refresh semántico.

## Siguiente

Esperar la review independiente; merge únicamente con PASS. No iniciar CER-M0-006 ni integración WidgetHost. Dependabot #3/#4 sigue separado.
