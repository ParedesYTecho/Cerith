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

Status: TODO
Owner: unclaimed
Priority: P0
Depends on: CER-M0-002

## CER-M0-004 — MetricBus

Status: TODO
Owner: unclaimed
Priority: P0
Depends on: CER-M0-002

Acceptance:

- multiple subscribers;
- cancellation;
- bounded memory;
- disposal;
- tests.

## CER-M0-005 — SyntheticProvider

Status: TODO
Owner: unclaimed
Priority: P0
Depends on: CER-M0-003, CER-M0-004

## CER-M0-006 — IPC prototype

Status: TODO
Owner: unclaimed
Priority: P0

Acceptance:

- compare realistic candidate mechanisms;
- measure latency/CPU/complexity;
- record decision;
- do not overbuild.
