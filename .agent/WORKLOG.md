# Worklog

Keep entries compact.

Template:

```text
## YYYY-MM-DD HH:MM — agent — task

Event: start | decision | discovery | test | handoff
Summary: ...
Files: ...
Result: ...
```

---

## 2026-09-23 — bootstrap — CER-M0-BOOTSTRAP

Event: decision
Summary: Cerith starts from a clean repository with public-ready security and zero-AI-slop rules.
Result: Initial documentation/starter pack prepared.

## 2026-09-23 — Codex — CER-M0-001

Event: handoff
Summary: Se creó `Cerith.sln`, los siete proyectos definidos y sus referencias; los proyectos de pruebas usan MSTest.
Files: `Cerith.sln`, `src/`, `tests/`, `.agent/DECISIONS.md`, `.agent/STATE.md`, `.agent/TASKS.md`, `.agent/HANDOFF.md`.
Result: PASS — XML, rutas, referencias y configuración de solución; BLOCKED — `dotnet build` y `dotnet test` no arrancan sin SDK .NET 10. La instalación temporal fue rechazada por la política automática.

## 2026-09-23 — Codex — CER-M0-001

Event: verificación
Summary: Instalado .NET SDK 10.0.401 tras autorización del usuario; `global.json` habilita Microsoft.Testing.Platform y CI tolera únicamente el código ZeroTests del esqueleto vacío.
Files: `global.json`, `.github/workflows/build.yml`, `.agent/DECISIONS.md`.
Result: PASS — build Release, 0 advertencias/errores; comando de pruebas ejecutado, 0 casos (no hay comportamiento de producto que probar todavía).

## 2026-09-23 — Codex — CER-M0-001

Event: handoff
Summary: Se cerró el esqueleto y se actualizó Graphify con 46 archivos fuente; revisión de contenido sensible y diff limpias.
Files: `Cerith.sln`, `global.json`, `.github/workflows/build.yml`, `src/`, `tests/`, `.agent/`, `graphify-out/` (ignorado).
Result: PASS — informe, HTML y JSON generados; sin endpoints huérfanos ni self-loops. Una pareja de relaciones se colapsa en la vista no dirigida. El uso real de tokens no está disponible en los resultados de agentes.

## 2026-09-23 — Codex — CER-M0-001

Event: corrección y verificación
Summary: D-012 limita la tolerancia de ZeroTests al bootstrap y CER-M0-002 obliga a retirarla al añadir la primera prueba ejecutable.
Files: `.agent/DECISIONS.md`, `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md`.
Result: OK — build exit 0, 0 advertencias/errores; `dotnet test` directo exit 1 con ZeroTests y 0 pruebas; comando de CI exit 0 únicamente por `--ignore-exit-code 8`; `git diff --check` sin errores.

## 2026-09-23 — Codex — CER-M0-001 / PR #1

Event: corrección de review
Summary: D-012 y CER-M0-002 exigen detección de cero tests por proyecto cuando existan pruebas reales; CodeQL queda condicionado a visibilidad pública, con acciones v4 y permisos mínimos.
Files: `.agent/DECISIONS.md`, `.agent/TASKS.md`, `.agent/HANDOFF.md`, `.agent/WORKLOG.md`, `.github/workflows/codeql.yml`.
Result: build Debug/Release exit 0 sin advertencias; MTP directo exit 1 (ZeroTests en 3 proyectos); test CI exit 0 por excepción temporal; CodeQL anterior falló por code scanning privado no disponible y acceso al workflow run.

## 2026-09-23 — Codex — reconciliación post-merge

Event: handoff
Summary: PR #1 se integró mediante squash en `main`; STATE y HANDOFF reflejan el estado posterior y DEVELOPMENT_WORKFLOW define el ciclo de sesión.
Files: `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/WORKLOG.md`, `DEVELOPMENT_WORKFLOW.md`.
Result: Build and Test y Repository Quality success; CodeQL skipped por repositorio privado. En esa reconciliación previa, CER-M0-002 aún no había sido reclamada; se completó posteriormente en PR #6.

## 2026-09-23 — Codex — CER-M0-002

Event: implementación
Summary: contratos tipados de métricas, invariantes y pruebas unitarias; D-014 separa estado/confianza y ZeroTests queda acotado a proyectos vacíos.
Files: `src/Cerith.Core/`, `tests/Cerith.Core.Tests/`, `.github/workflows/build.yml`, `docs/METRICS.md`, `.agent/DECISIONS.md`, `.agent/TASKS.md`, `.agent/STATE.md`, `.agent/HANDOFF.md`.
Result: PASS — build Release sin warnings/errores; 17/17 pruebas Core; tolerancias individuales de Telemetry/Integration verificadas; cero tests en Core falla con código 8; diff e higiene privada limpios. Graphify code-only actualizado a 222 nodos; reextracción semántica de docs pendiente por falta de clave LLM. CI del PR #6: Build and Test y Repository Quality success; CodeQL skipped según D-013.

## 2026-09-23 — Codex — CER-M0-002 post-merge

Event: handoff
Summary: PR #6 pasó review del Advisor y se mergeó mediante squash en `main`; memoria operativa reconciliada y CER-M0-003 permanece sin reclamar.
Files: `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md`.
Result: PASS — `main` se sincronizó en `e895e1666d376a22c15a31d6b809c5a6b716307d`; Build and Test y Repository Quality success; CodeQL skipped según D-013; `git diff --check` limpio. Los PR Dependabot #3/#4 continúan separados.

## 2026-09-23 — Codex — CER-M0-003

Event: implementación y merge
Summary: `MetricRegistry` thread-safe con registro first-wins, lookup y snapshots read-only; tests de contrato; documentación canónica actualizada. PR #8 pasó review del Advisor con PASS y se integró mediante squash.
Files: `src/Cerith.Core/MetricRegistry.cs`, `tests/Cerith.Core.Tests/MetricRegistryTests.cs`, `docs/METRICS.md`, `.agent/TASKS.md`, `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/WORKLOG.md`.
Result: PASS — build Release sin warnings/errores; 25/25 Core tests; Build and Test y Repository Quality success; CodeQL skipped según D-013. Graphify code-only: 248 nodos/330 aristas. La reextracción semántica documental continúa pendiente según el handoff previo.

## 2026-09-23 — Codex — CER-M0-003 post-merge

Event: reconciliación
Summary: `main` se sincronizó con `origin/main` en `00e8a9f1d35d9d96b754298a55ff4cfb416bcc71`; STATE, HANDOFF y TASKS reflejan el merge. M0-004 y M0-005 siguen TODO/unclaimed; Dependabot #3/#4 continúa separado.
Files: `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md`.
Result: PASS — `main` exacta y limpia al inicio; `git diff --check` sin errores.

## 2026-09-25 — Codex — CER-M0-004

Event: implementación y handoff
Summary: `MetricBus` concreto con fan-out exacto, Channels acotados por subscriber, backpressure, cancelación y disposal; D-015 y documentación canónica actualizadas. Base `main` verificada en `95643d2e08d4149f1260776bdbe6bc7882d9fdef`.
Files: `src/Cerith.Core/MetricBus.cs`, `tests/Cerith.Core.Tests/MetricBusTests.cs`, `docs/METRICS.md`, `.agent/DECISIONS.md`, `.agent/TASKS.md`, `.agent/STATE.md`, `.agent/HANDOFF.md`.
Result: PASS — Release build sin warnings/errores; Core 39/39; `git diff --check`; Graphify code-only 286 nodos/463 aristas; CI remoto del PR #10 Build and Test y Repository Quality PASS. CodeQL SKIPPED según D-013; review del Advisor pendiente.

## 2026-09-25 — Codex — CER-M0-004 post-merge

Event: reconciliación
Summary: PR #10 pasó review independiente del Advisor con PASS y se integró mediante squash en `bd305ae1b2975c8d3a81125688ec21639ab90817`; CER-M0-004 queda DONE. D-015 y el contrato MetricBus están en `main`.
Files: `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md`.
Result: PASS — Build and Test y Repository Quality success; CodeQL skipped según D-013; `main` sincronizada con `origin/main`; working tree limpio al iniciar; `git diff --check` limpio. CER-M0-005 y CER-M0-006 continúan TODO/unclaimed; Dependabot #3/#4 sigue separado.

## 2026-09-25 — Codex — CER-M0-005

Event: implementación y PR
Summary: `SyntheticProvider` determinista publica únicamente `synthetic.counter` con MetricRegistry, MetricBus y TimeProvider. Confidence `Unknown` deja claro que el valor no mide ni estima telemetría real. Siete pruebas reales; Telemetry.Tests perdió la tolerancia ZeroTests y solo Integration.Tests conserva la excepción temporal. Commit de implementación `11575ded86d94fb3567cccf0f6c91990b2930f48`; PR #12 abierto, sin merge.
Files: `src/Cerith.TelemetryService/SyntheticProvider.cs`, `tests/Cerith.Telemetry.Tests/SyntheticProviderTests.cs`, `.github/workflows/build.yml`, `.agent/TASKS.md`, `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/WORKLOG.md`.
Result: PASS — Release build 0 warnings/errors; Core 39/39; Telemetry 7/7 sin tolerancia; Integration 0 tests con tolerancia temporal; `git diff --check`; chequeo de nombres sensibles del workflow sin coincidencias. CI de PR #12: Build and Test PASS, Repository Quality PASS, CodeQL SKIPPED según D-013. Graphify code-only: 317 nodos y 546 aristas; reportó etiquetas desfasadas (64 comunidades, 62 etiquetas guardadas). No iniciar CER-M0-006; esperar review independiente y merge solo con PASS.

## 2026-09-25 — Codex — CER-M0-005 post-merge

Event: reconciliación
Summary: `main` se sincronizó con `origin/main` en `729075dcfcef1ae849b9339a96b438d234e7ff37`. PR #12 pasó review independiente del Advisor con PASS y se integró mediante squash; CER-M0-005 queda DONE. `SyntheticProvider` y `synthetic.counter` están en `main`.
Files: `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md`.
Result: PASS — `main` exacta en el SHA de merge y working tree limpio antes de la reconciliación; `git diff --check`. CI: Build and Test success, Repository Quality success, CodeQL skipped según D-013. Core.Tests y Telemetry.Tests sin tolerancia ZeroTests; solo Integration.Tests conserva temporalmente su excepción. CER-M0-006 sigue TODO/unclaimed; no hay task WidgetHost reclamada; Dependabot #3/#4 sigue separado. Graphify label drift queda como tooling pendiente, sin task de producto.

## 2026-09-25 — Codex — CER-M0-006 IPC prototype

Event: measured decision spike
Summary: Windows BCL-only child-process harness compared Named Pipes, TCP loopback, and AF_UNIX twice at 256/4096-byte payloads. D-016 selects Named Pipes; the next implementation must prove remote-client rejection.
Files: `.agent/DECISIONS.md`, `.agent/HANDOFF.md`, `.agent/TASKS.md`, `.agent/STATE.md`, `.agent/WORKLOG.md`, `docs/PERFORMANCE.md`.
Result: PASS — all six measurement cells and functional reconnect/cancellation/cleanup checks ran twice; Release build 0 warnings/errors; Core 39/39, Telemetry 7/7, Integration 0 with the documented ZeroTests allowance; `git diff --check` PASS. PR #14: Build and Test PASS, Repository Quality PASS, CodeQL SKIPPED según D-013. No product code, package, or permanent benchmark project added.

## 2026-09-26 — Codex — CER-M0-006 post-merge reconciliation

Event: reconciliación
Summary: PR #14 pasó review independiente del Advisor con PASS y se integró mediante squash en `dc6c051f0734fc5eb8a301a16bda671d2ba94221`; CER-M0-006 queda DONE. D-016 y el baseline están integrados; Named Pipes es el transporte elegido para la primera implementación.
Files: `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md`.
Result: PASS — `main` y `origin/main` coincidían en el merge SHA y la working tree estaba limpia antes de reconciliar. La implementación futura debe mantener local-only y probar rechazo de clientes remotos; `CurrentUserOnly` no basta. Build and Test PASS; Repository Quality PASS; CodeQL SKIPPED según D-013. No hay task IPC/WidgetHost reclamada; Dependabot #3/#4 sigue separado; Graphify label drift es tooling pendiente, no una task de producto.

## 2026-09-26 — Codex — CER-M0-007 Named Pipe snapshot bridge

Event: implementación y verificación local
Summary: Named Pipe Win32 local-only con DACL del usuario actual y `PIPE_REJECT_REMOTE_CLIENTS`; protocolo v1 acotado en Core; cliente WidgetHost y tests de integración reales. D-017 registrada; tolerancia ZeroTests de Integration.Tests retirada.
Files: `MetricIpcProtocol.cs`, `NamedPipeMetricServer.cs`, `NamedPipeMetricClient.cs`, `NamedPipeMetricTests.cs`, workflow, arquitectura y memoria operativa.
Result: PASS — Release build 0 warnings/errors; Core 39/39, Telemetry 7/7, Integration 8/8. `GetNamedPipeInfo` verificó bit `0x8` frente a control `0x0`; Graphify code-only 407 nodos/845 aristas. PR #16 y CI Build and Test/Repository Quality PASS; CodeQL SKIPPED según D-013.

## 2026-09-26 — Codex — CER-M0-007 post-merge reconciliation

Event: reconciliación post-merge
Summary: PR #16 pasó review independiente del Advisor con PASS y se integró mediante squash. D-017 y el bridge Named Pipe están integrados; CER-M0-007 queda DONE. No iniciar CER-M0-008.
Files: `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md`.
Result: PASS — `main` local y `origin/main` verificadas en `192afc43632ea50fe92e6416e65431ce0bbe7da8`; working tree limpia antes de editar memoria. Solo memoria operativa modificada. PR #16 CI: Build and Test PASS, Repository Quality PASS, CodeQL SKIPPED según D-013. `git diff --check` PASS.

## 2026-09-27 — Codex — CER-M0-008 claim

Event: preflight y reclamación
Summary: `main` y `origin/main` verificadas en `9041314c32f42b9a026a09491fb91900eb6ef19c` con árbol limpio; creada `feat/CER-M0-008-native-visual-pipeline` y reclamada CER-M0-008. Sin cambios de producto ni merge.
Files: `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md`.
Result: PASS — preflight de Git; Graphify consultado para navegación. Build/tests: NOT RUN (solo memoria operativa).

## 2026-09-27 — Codex — CER-M0-008 native visual pipeline

Event: implementación y verificación local
Summary: TelemetryService ejecutable publica `synthetic.counter` cada segundo; WidgetHost WinUI 3 unpackaged/framework-dependent muestra snapshots del cliente Named Pipe existente. D-018 registra la dependencia Microsoft.WindowsAppSDK 2.5.1 y el alcance M0. Integration.Tests compila el mismo archivo cliente sin cargar el bootstrapper del ejecutable UI.
Files: `Program.cs`, `App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `MetricIpcProtocol.cs`, tres `.csproj`, `docs/ARCHITECTURE.md`, `.agent/DECISIONS.md` y memoria operativa.
Result: PASS — restore; Release build 0 warnings/errors; Core 39/39, Telemetry 7/7, Integration 8/8; UI Automation observó Connecting y valor cambiante; sin servidor mostró unavailable; WidgetHost cerró con exit 0 y TelemetryService respondió a Ctrl+C con exit 0. Graphify code-only actualizado; label drift existente no regenerado. Review independiente pendiente; sin merge.

PR #18: https://github.com/ParedesYTecho/Cerith/pull/18 — commit de implementación `94bd93f3b8d7037cae9015123ea1404f569de574`; pendiente de checks y review independiente del Advisor. No merge.

## 2026-09-27 — Codex — CER-M0-008 post-merge

Event: reconciliación post-merge
Summary: Advisor independiente PASS sin correcciones de implementación. PR #18 pasó Build and Test y Repository Quality (CodeQL SKIPPED según D-013) y se integró por squash en `15646293886e8951779c45d4bf894c3017c0bc7c`. `main` local y `origin/main` coincidieron en ese SHA; working tree limpio antes de editar. CER-M0-008 queda DONE.
Files: `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md`.
Result: PASS — la licencia GPL candidata aún requiere revisión de compatibilidad con Windows App SDK antes de fijar/publicar la licencia; linked source aceptado solo para M0. Sin próxima task reclamada; Dependabot y Graphify label drift separados.

## 2026-09-27 — Codex — CER-M0-009 M0 exit diagnostics

Event: implementación y verificación local; READY FOR ADVISOR REVIEW.
Summary: `Program.cs` anuncia disponibilidad del pipe `Cerith.Metrics.M0` y `synthetic.counter`, y el cierre normal. `docs/ROADMAP.md` delimita ControlCenter shell y diagnostics de M0 frente a M1. WidgetHost no cambió.
Files: `src/Cerith.TelemetryService/Program.cs`, `docs/ROADMAP.md`, `.agent/TASKS.md`, `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/WORKLOG.md`.
Result: PASS — `main` y `origin/main` coincidían en `4a5381c06cb99479e537197848eb15ccf1dba370` con árbol limpio; Graphify consultado. Release build 0 warnings/errors; Core 39/39, Telemetry 7/7, Integration 8/8; consola mostró ready y stopped; segundo proceso falló por pipe ocupado sin mostrar ready. Sin proyectos, dependencias ni abstracciones nuevos. M0 no declarado DONE; review independiente pendiente.

## 2026-09-27 — Codex — CER-M0-009 post-merge reconciliation

Event: cierre de M0 y reconciliación operativa.
Summary: Advisor independiente PASS; PR #20 se integró por squash. CER-M0-009 y M0 — Foundation quedan DONE. M1 — Hardware MVP es el siguiente milestone, sin task definida ni reclamada. Se conservan explícitamente las deudas de licencia Windows App SDK y linked source de Integration.Tests.
Files: `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md`.
Result: PASS — squash `40c0b82920a4b9ca797300bea16d8a1f506a97c7`; `HEAD == main == origin/main` en ese SHA y árbol limpio tras actualizar `main`. PR #20 CI Build and Test PASS, Repository Quality PASS, CodeQL SKIPPED según D-013. `git diff --check` PASS. Reconciliation commit `aaad32d`; PR #21 abierto. No se tocó código, tests, Dependabot #3/#4 ni Graphify label drift.
## 2026-09-27 — Codex — CER-M1-001 CPU total usage

Event: probe local sin elevación, implementación y verificación del vertical slice; READY FOR ADVISOR REVIEW.
Summary: LibreHardwareMonitorLib 0.9.6 expuso un único CPU/Load/CPU Total con valor presente. TelemetryService publica `cpu.total.usage` medido por el bus y pipe existentes; WidgetHost lo muestra sin depender de LHM. Se auditó el grafo NuGet; la licencia exacta del artefacto Mono.Posix.NETStandard 1.0.0 queda por confirmar antes del merge.
Result: Release build 0 warnings/errors; Core 39/39, Telemetry 7/7, Integration 8/8 PASS. Demo UI observó 13.9 %, 15.1 % y 11.5 %; cierre de WidgetHost y mensaje de parada del servicio observados. CI y revisión del Advisor pendientes.
