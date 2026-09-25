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
Files: `.agent/DECISIONS.md`, `.agent/TASKS.md`, `.agent/STATE.md`, `docs/PERFORMANCE.md`.
Result: PASS — all six measurement cells and functional reconnect/cancellation/cleanup checks ran twice; Release build 0 warnings/errors; Core 39/39, Telemetry 7/7, Integration 0 with the documented ZeroTests allowance; `git diff --check` PASS. No product code, package, or permanent benchmark project added.
