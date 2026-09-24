# Handoff — CER-M0-004 MetricBus

Fecha: 2026-09-25
Estado: implementada en `feat/CER-M0-004-metric-bus`; pendiente de review independiente y merge por el Advisor.
Base verificada: `origin/main` = `95643d2e08d4149f1260776bdbe6bc7882d9fdef`, árbol limpio antes del claim.
PR: pendiente de creación.

## Cambio

- `src/Cerith.Core/MetricBus.cs`: fan-out exacto por `MetricId`, suscripciones async independientes, capacidad explícita, `BoundedChannelFullMode.Wait`, cancelación y disposal. Sin cache, replay, Registry ni dependencias nuevas.
- `tests/Cerith.Core.Tests/MetricBusTests.cs`: routing, orden, ausencia de replay, backpressure, cancelación y cierre observables.
- `docs/METRICS.md` y `.agent/DECISIONS.md`: contrato canónico y D-015.
- `.agent/TASKS.md`, `.agent/STATE.md`, `.agent/WORKLOG.md`: estado operativo de la task.

Los dos archivos nuevos separan el comportamiento del bus de los contratos previos y sus pruebas de los tests existentes; solo los proyectos Core y Core.Tests los usan. La única abstracción interna es la subscription privada que vincula ID y Channel. Sin paquetes ni proyectos nuevos.

## Verificación

- PASS — `dotnet build Cerith.sln --configuration Release`: 0 advertencias, 0 errores.
- PASS — `dotnet test tests/Cerith.Core.Tests/Cerith.Core.Tests.csproj --configuration Release --no-build`: 39/39.
- PASS — `git diff --check`.
- PASS — Graphify code-only actualizado a 286 nodos y 463 aristas; actualización semántica documental pendiente.
- CI remoto: pendiente de PR.

## Siguiente

Advisor: review independiente del PR y merge solo con PASS. No iniciar CER-M0-005 en esta branch. Dependabot #3/#4 permanece separado.
