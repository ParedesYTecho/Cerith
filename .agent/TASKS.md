# Tasks

## CER-M2-001 — PresentMon displayed telemetry vertical slice

Status: DONE
Owner: Codex (single writer)
Priority: P0
Depends on: M1 — Hardware MVP
Branch: `codex/CER-M2-001-presentmon-display`
Base: `89ddd69c13affd76b3b6085a25753bb7b83b0a3f` (`main`, confirmed after fetch)

Scope: one explicit target PID; official PresentMon Service/API; average displayed FPS and displayed frame time; normalized snapshots through the existing MetricBus, Named Pipe v1 and WidgetHost. No other gaming metrics or M2 exit claim.

Result: official PresentMon 2.6.0 Service/SDK verified and installed; implementation and deterministic tests complete. Advisor PASS. The correction based on `c63e25110e4c18be3a6698e58b80cbbd4f411dba` made multiple-chain errors current-poll state, preserved the API-returned frame blob size for row stepping, copied the upstream MIT license to build/publish, and clarified displayed-FPS semantics. Release restore/build PASS (0 warnings/errors); Core 39/39, Telemetry 16/16, Integration 9/9 PASS; publish and output inspection PASS. A final-build same-PID, same-capture D3D11 run recovered `Fresh` after removing the second chain, with variable `Error/multiple_swap_chains`, `Unavailable` and `Fresh` samples across the transition. The instrumented rerun verified both chains continued calling `Present` but did not reproduce that telemetry sequence; exact counts and the per-poll API limitation are in `.agent/HANDOFF.md`. Loader SHA-256 remained unchanged; build and publish each contain loader plus license. Product PR #31 passed Build and Test and Repository Quality; CodeQL was SKIPPED under D-013. Approved product head `086db95e29124900854e29d2b70caa16b3f43889` was squash-merged to `main` as `9902cc19ff0f2911ab4ee0ece2337d12b1d9edcf`, with an identical integrated tree. M2 remains INCOMPLETE; CER-M2-002 is NEXT / NOT STARTED.

## CER-M1-004 — Hardware/provider diagnostics and M1 exit

Status: DONE
Owner: Codex (single writer)
Priority: P0
Depends on: CER-M1-003
Branch: `codex/CER-M1-004-hardware-diagnostics-m1-exit`

Scope: minimal sanitized hardware/provider diagnostics, real non-elevated runtime verification, and M1 exit audit with evidence for its criteria. No M2 work.

Result: Advisor PASS final for diagnostics and M1 exit. Both evidence gates passed without product-code changes: the same service/LHM 0.9.6 discovered four Storage when elevated and published coherent `storage.0` usage/total/free `Fresh`/`Measured` values through the existing pipe; without elevation it still discovered zero. A targeted console signal produced `TelemetryService stopped.` and process exit code 0. Release build and all canonical suites PASS. PR #29 passed Build and Test and Repository Quality (CodeQL SKIPPED under policy) and was squash-merged into `main` at `59ecb648a9f714376578fc16cdaa446571bd4874`. M1 DONE; M2 NEXT/NOT STARTED. Non-elevated Storage remains unverified on this host. No new dependency, metric ID, diagnostics protocol, ControlCenter feature or M2 work.

## CER-M1-003 — Storage and network telemetry baseline

Status: DONE (Storage runtime unverified: LHM discovered zero devices without elevation)
Owner: Codex (single writer)
Priority: P0
Depends on: CER-M1-002
Branch: `codex/CER-M1-003-storage-network`

Scope: probe local no elevado y mapping verificado de LibreHardwareMonitorLib 0.9.6 para almacenamiento y red; extender el único lifecycle hardware, métricas canónicas y demo compacta en WidgetHost. Sin diagnostics M1, paquetes nuevos ni framework de hotplug. M1 permanece INCOMPLETE.

Result: Advisor PASS final; PR #27 passed Build and Test and Repository Quality (CodeQL SKIPPED under current policy) and was squash-merged into `main` at `70c54cf692c8a8f33d6719a845a8124d0fd9a9fc`. Storage usage/total/free has LHM 0.9.6 source mapping audited but no positive runtime validation on this host: Windows saw four disks and LHM discovered zero Storage without elevation. Storage read/write throughput remains deferred. Network download/upload/usage had real `Fresh/Measured` traffic. `storage.N`/`network.N` lack guaranteed persistent physical identity; removed devices become unavailable and additions/reconnections may require restart. No general hotplug system or M1 diagnostics. M1 INCOMPLETE; hardware/provider diagnostics pending; CER-M1-004 NOT STARTED.

## CER-M1-002 — Multi-metric hardware baseline

Status: DONE
Owner: Codex (single writer)
Priority: P0
Depends on: CER-M1-001
Branch: `codex/CER-M1-002-multi-metric-hardware`

Scope: one LHM lifecycle for CPU/GPU, direct Windows RAM baseline, concurrent v1 Named Pipe sessions, minimal multi-metric WidgetHost, focused tests and canonical documentation. M1 remains incomplete.

Result: PR #25 received independent Advisor PASS, Build and Test PASS, Repository Quality PASS and CodeQL SKIPPED under current policy; squash-merged into `main` at `6f442a2ecdf92d4368dce30ad55572950e52ccb2`. Local non-elevated CPU/GPU and pre-change IPC probes completed; 11 metrics published concurrently on one CPU and one AMD GPU; Release build and Core 39/39, Telemetry 7/7, Integration 9/9 PASS. CPU/RAM/GPU/VRAM/fan baseline and concurrent Named Pipe are integrated. NVIDIA/Intel/multi-GPU remain physically unvalidated; `gpu.N` has no proven persistent physical identity. M1 remains incomplete. CER-M1-003 is not defined or claimed.

## CER-M1-001 — LibreHardwareMonitor CPU usage vertical slice

Status: DONE
Owner: Codex
Priority: P0
Depends on: M0 — Foundation
Branch: `feat/CER-M1-001-lhm-cpu-usage`

Scope: probe local no elevado de LibreHardwareMonitorLib 0.9.6; si confirma inequívocamente CPU Total, publicar `cpu.total.usage` por el MetricBus y Named Pipe existentes y mostrarlo en WidgetHost. Sin otras métricas ni arquitectura de providers.

Acceptance: Release build, tres suites, CI y demo local PASS; revisión independiente del Advisor PASS; PR integrado por squash. M1 sigue abierto y no se declara completa.

Resultado: PR #22 pasó review independiente del Advisor con PASS y se integró mediante squash en `b7e40447f48cd6afd2b015a94a49d825f28099f9`. Build and Test PASS, Repository Quality PASS, CodeQL SKIPPED según D-013. D-019 quedó integrada. `cpu.total.usage` es el único slice hardware implementado. No se probó hardware diverso ni se añadieron tests CI dependientes de hardware. La licencia exacta de Mono.Posix.NETStandard 1.0.0 continúa como deuda de provenance/third-party-notice previa al public release; no se conoce incompatibilidad.

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

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-004, CER-M0-005, CER-M0-006
Branch: `feat/CER-M0-007-named-pipe-bridge`

Scope: `Cerith.Core` wire contract; `Cerith.TelemetryService` local-only server; `Cerith.WidgetHost` client; integration tests; CI ZeroTests removal; compact architecture and decision updates.

Acceptance: versioned bounded snapshots over a Windows Named Pipe with current-user isolation and effective remote rejection; deterministic lifecycle, cancellation, reconnect and protocol tests; Release build and all test projects pass.

Resultado: PR #16 pasó review independiente del Advisor con PASS y se integró mediante squash en `main` (`192afc43632ea50fe92e6416e65431ce0bbe7da8`). D-017 y el bridge están integrados; SyntheticProvider → MetricBus → Named Pipe → WidgetHost client demostrado. Protocolo v1, máximo 64 KiB por frame, ACL del usuario actual y `PIPE_REJECT_REMOTE_CLIENTS` verificado con `GetNamedPipeInfo`. No se probó desde otra máquina ni otra cuenta. Core.Tests 39/39, Telemetry.Tests 7/7, Integration.Tests 8/8; todos sin tolerancia ZeroTests. Build and Test PASS; Repository Quality PASS; CodeQL SKIPPED según D-013. Sin paquetes ni proyectos nuevos.

## CER-M0-008 — Native visual pipeline

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-007
Branch: `feat/CER-M0-008-native-visual-pipeline`

Scope: TelemetryService publica `synthetic.counter` periódicamente; WidgetHost WinUI 3 unpackaged recibe snapshots por el cliente Named Pipe existente y muestra el valor cambiante. Sin widget engine ni supervisión de procesos.

Acceptance: ventana inmediata con estado Connecting, valor real cambiante y estado unavailable ante fallo; cierre y cancelación limpios; restore, build Release y tres suites PASS; demo manual reproducible.

Resultado: Advisor independiente PASS; PR #18 integró el slice por squash en `main` (`15646293886e8951779c45d4bf894c3017c0bc7c`). Build and Test y Repository Quality PASS; CodeQL SKIPPED según D-013. El linked source de `NamedPipeMetricClient.cs` en Integration.Tests queda aceptado solo para M0. Windows App SDK 2.5.1 queda aceptado para M0; la compatibilidad con la licencia GPL-3.0-or-later candidata debe cerrarse antes de fijar/publicar la licencia definitiva. Sin cambios de implementación tras la review.

## CER-M0-009 — M0 exit diagnostics and scope closure

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-008
Branch: `feat/CER-M0-009-m0-exit-diagnostics`

Scope: diagnóstico mínimo del ciclo de vida de TelemetryService y aclaración del alcance M0 de ControlCenter y diagnostics. Sin UI nueva, frameworks de logging ni diseño de M1.

Resultado: Advisor PASS; PR #20 se integró por squash en `main` (`40c0b82920a4b9ca797300bea16d8a1f506a97c7`). Build Release 0 warnings/errors; Core 39/39, Telemetry 7/7, Integration 8/8 PASS; CI Build and Test y Repository Quality PASS; CodeQL SKIPPED según D-013. Demo manual observó ready, stopped y fallo del segundo proceso sin mensaje ready. Sin cambios de WidgetHost, proyectos, paquetes ni abstracciones. M0 — Foundation queda DONE tras satisfacer sus requisitos documentados.
