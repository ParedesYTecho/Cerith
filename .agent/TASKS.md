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

Status: IN_PROGRESS
Owner: Codex
Priority: P0
Depends on: CER-M0-001

Acceptance:

- MetricId;
- MetricDescriptor;
- MetricSnapshot;
- MetricState;
- MetricConfidence;
- meaningful unit tests.
- Al añadir la primera prueba real, retirar la tolerancia global `--ignore-exit-code 8`: CI debe fallar si un proyecto con tests reales descubre o ejecuta cero inesperadamente. Solo los proyectos intencionadamente vacíos pueden tolerar `ZeroTests` de forma temporal y acotada; retirar esa tolerancia al añadir su primer test. Elegir la solución más simple, sin tests dummy.

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
