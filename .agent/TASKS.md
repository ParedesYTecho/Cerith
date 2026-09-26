# Tasks

## CER-M0-001 — Repository and solution skeleton

Status: DONE
Owner: Codex
Priority: P0
Resultado: solución compilada; el runner de pruebas está configurado y el resumen confirma cero casos, intencionadamente. La revisión de seguridad y Graphify se completaron.

Scope:

```text
repository root
src/
tests/
.github/
tools/
```

Acceptance:

- `Cerith.sln`;
- projects:
  - `Cerith.Core`
  - `Cerith.TelemetryService`
  - `Cerith.WidgetHost`
  - `Cerith.ControlCenter`
  - `Cerith.Core.Tests`
  - `Cerith.Telemetry.Tests`
  - `Cerith.Integration.Tests`
- dependency direction matches `docs/ARCHITECTURE.md`;
- build succeeds;
- no product features;
- no unnecessary NuGet dependencies;
- security/quality config remains intact;
- staged files reviewed before first commit.

## CER-M0-002 — Metric contracts

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-001

Resultado: contratos y 17 pruebas reales completados; review del Advisor aprobada en PR #6 y merge mediante squash en `main` (`e895e1666d376a22c15a31d6b809c5a6b716307d`). Build and Test y Repository Quality pasaron; CodeQL se omitió según D-013. Los PR Dependabot #3 y #4 siguen siendo mantenimiento separado.

Acceptance:

- MetricId;
- MetricDescriptor;
- MetricSnapshot;
- MetricState;
- MetricConfidence;
- meaningful unit tests.
- CI ejecuta `Cerith.Core.Tests` sin tolerar `ZeroTests`; solo los proyectos vacíos toleran temporalmente el código 8 en invocaciones propias, y pierden esa excepción al incorporar su primer test real. Sin tests dummy.

## CER-M0-003 — MetricRegistry

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-002
Resultado: implementación completada; PR #8 pasó review del Advisor con PASS y se integró mediante squash en `main` (`00e8a9f1d35d9d96b754298a55ff4cfb416bcc71`). Build and Test y Repository Quality success; CodeQL skipped según D-013.

## CER-M0-004 — MetricBus

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-002
Resultado: PR #10 pasó review independiente del Advisor con PASS y se integró mediante squash en `main` (`bd305ae1b2975c8d3a81125688ec21639ab90817`). Build and Test y Repository Quality: success; CodeQL: skipped según D-013. D-015 y el contrato de `docs/METRICS.md` quedaron integrados. Dependabot #3/#4 permanece separado.

Acceptance:

- multiple subscribers;
- cancellation;
- bounded memory;
- disposal;
- tests.

## CER-M0-005 — SyntheticProvider

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-003, CER-M0-004
Resultado: PR #12 pasó review independiente del Advisor con PASS y se integró mediante squash en `main` (`729075dcfcef1ae849b9339a96b438d234e7ff37`). Build and Test y Repository Quality: success; CodeQL: skipped según D-013.

## CER-M0-006 — IPC prototype

Status: DONE
Owner: Codex
Priority: P0
Resultado: benchmark real en Windows comparó Named Pipes, TCP loopback y AF_UNIX con dos runs, 5000 RTT medidos por candidato/payload y comprobaciones funcionales. D-016 y el baseline de `docs/PERFORMANCE.md` quedaron integrados. PR #14 pasó review independiente del Advisor con PASS y se mergeó mediante squash en `dc6c051f0734fc5eb8a301a16bda671d2ba94221`. Named Pipes es el transporte elegido para la primera implementación; esta debe conservar local-only y demostrar rechazo de clientes remotos, porque `CurrentUserOnly` por sí solo no basta. No hay una task IPC/WidgetHost reclamada.

Acceptance:

- compare realistic candidate mechanisms;
- measure latency/CPU/complexity;
- record decision;
- do not overbuild.

## CER-M0-007 — Named Pipe snapshot bridge

Status: DONE (implementation; independent review pending)
Owner: Codex (this branch)
Priority: P0
Depends on: CER-M0-004, CER-M0-005, CER-M0-006
Branch: `feat/CER-M0-007-named-pipe-bridge`

Scope: `Cerith.Core` wire contract; `Cerith.TelemetryService` local-only server; `Cerith.WidgetHost` client; integration tests; CI ZeroTests removal; compact architecture and decision updates.

Acceptance: versioned bounded snapshots over a Windows Named Pipe with current-user isolation and effective remote rejection; deterministic lifecycle, cancellation, reconnect and protocol tests; Release build and all test projects pass.

Resultado: bridge y pruebas reales completados; `GetNamedPipeInfo` comprobó `PIPE_REJECT_REMOTE_CLIENTS` frente a un control sin el flag. Sin nuevos paquetes ni proyectos. PR y CI remoto pendientes de publicación.
