# Entrega — CER-M0-002

Fecha: 2026-09-23
Estado: implementación terminada en `feat/CER-M0-002-metric-contracts`; lista para review del Advisor. Sin push ni merge.

Base `main`: `f42f9351cf39441b7a3406d6ee55eebed5ad20f6`.
Commit de implementación: `f0b318d92052378b206328dad6257ce17b0fcf78`.

## Implementado

- `MetricId` conserva texto exacto con igualdad case-sensitive; `MetricValue` discrimina double, long, decimal, bool, string, DateTimeOffset y TimeSpan.
- `MetricSnapshot` valida valor/estado/confianza y conserva timestamp, provenance y error code sanitizado. `MetricState` y `MetricConfidence` son ortogonales; la edad deriva de `ObservedAt`.
- `MetricDescriptor` es provider-independent. No hay registry, bus, providers, UI, IPC, dependencias nuevas ni refactors fuera de scope.
- 17 pruebas unitarias de contratos e invariantes.

## Archivos

Añadidos: `src/Cerith.Core/MetricId.cs` (identidad), `src/Cerith.Core/MetricValue.cs` (valor discriminado), `src/Cerith.Core/MetricDescriptor.cs` (descriptor semántico), `src/Cerith.Core/MetricSnapshot.cs` (estados, confidence y validación) y `tests/Cerith.Core.Tests/MetricContractTests.cs` (pruebas contractuales).

Modificados: `.agent/DECISIONS.md` (D-005, D-012, D-014), `.agent/TASKS.md`, `.agent/WORKLOG.md`, `.agent/STATE.md`, `.github/workflows/build.yml` y `docs/METRICS.md`.

## Verificación

- PASS — `dotnet build Cerith.sln --configuration Release`, 0 warnings/errores.
- PASS — `dotnet test tests/Cerith.Core.Tests/Cerith.Core.Tests.csproj --configuration Release --no-build --verbosity normal`, 17/17 sin tolerancia.
- PASS — Telemetry e Integration, con sus invocaciones individuales `-- --ignore-exit-code 8`; 0 tests en cada proyecto vacío.
- PASS — Core filtrado deliberadamente a 0 tests falla con MTP `ZeroTests` (8), sin tolerancia.
- PASS — `git diff --check` y el hygiene check de artefactos privados configurado en Repository Quality.
- PASS parcial — `graphify update .` actualizó el grafo de código a 222 nodos; una consulta confirmó los símbolos nuevos. La reextracción semántica documental quedó pendiente porque Graphify no encuentra una clave LLM.
- Workflow de CI actualizado; GitHub Actions no se ejecutó porque la branch no se publicó. CodeQL sigue omitido en repositorios privados según D-013.

## Decisiones y límites

D-014 registra identidad ordinal, estado/confianza ortogonales, edad derivada, valor tipado y descriptor provider-independent. `MetricValueKind` solo discrimina las alternativas necesarias; no se añadieron interfaces, factories ni dependencias.

Riesgo restante: Telemetry.Tests e Integration.Tests permanecen vacíos y conservan excepciones temporales individuales hasta que reciban tests reales. El grafo mantiene contenido semántico documental anterior y labels de comunidad no reetiquetados con LLM; su actualización completa requiere configurar un backend LLM. Los PR Dependabot #3/#4 no se tocaron. No iniciar CER-M0-003 antes de completar review y merge de esta task.
