# Handoff post-merge — CER-M0-004 MetricBus

Fecha: 2026-09-25
Estado: DONE y mergeada en `main` mediante squash tras review independiente del Advisor con PASS.
Commit de merge: `bd305ae1b2975c8d3a81125688ec21639ab90817`.
PR: https://github.com/ParedesYTecho/Cerith/pull/10

## Cambio

- `src/Cerith.Core/MetricBus.cs`: fan-out exacto por `MetricId`, suscripciones async independientes, capacidad explícita, `BoundedChannelFullMode.Wait`, cancelación y disposal. Sin cache, replay, Registry ni dependencias nuevas.
- `tests/Cerith.Core.Tests/MetricBusTests.cs`: routing, orden, ausencia de replay, backpressure, cancelación y cierre observables.
- `docs/METRICS.md` y `.agent/DECISIONS.md`: contrato canónico y D-015.
- `.agent/TASKS.md`, `.agent/STATE.md`, `.agent/WORKLOG.md`: estado operativo reconciliado tras el merge.

Los dos archivos nuevos separan el comportamiento del bus de los contratos previos y sus pruebas de los tests existentes; solo los proyectos Core y Core.Tests los usan. La única abstracción interna es la subscription privada que vincula ID y Channel. Sin paquetes ni proyectos nuevos.

## Verificación

- PASS — `dotnet build Cerith.sln --configuration Release`: 0 advertencias, 0 errores.
- PASS — `dotnet test tests/Cerith.Core.Tests/Cerith.Core.Tests.csproj --configuration Release --no-build`: 39/39.
- PASS — `git diff --check`.
- PASS — Graphify code-only actualizado a 286 nodos y 463 aristas; actualización semántica documental pendiente.
- PASS — review independiente del Advisor en PR #10.
- PASS — GitHub Actions Build and Test y Repository Quality; CodeQL SKIPPED según D-013.
- PASS — `main` y `origin/main` en `bd305ae1b2975c8d3a81125688ec21639ab90817`; árbol limpio al iniciar la reconciliación.
- PASS — `git diff --check` en la reconciliación.

## Siguiente

CER-M0-005 y CER-M0-006 siguen `TODO` / `unclaimed`; no se inician aquí. Dependabot #3/#4 permanece separado.
