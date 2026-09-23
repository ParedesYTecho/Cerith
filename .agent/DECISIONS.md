# Decisions

## D-001 — Clean start

Cerith starts in a clean repository.

WidgetOSD is legacy reference only.

## D-002 — Native Windows-first

Primary direction: C#/.NET/WinUI/Windows APIs.

## D-003 — Universal metric architecture

Widgets bind to Metric IDs, never directly to providers.

## D-004 — AI quotas start from zero

Legacy quota logic is not ported.

## D-005 — Truthful states

Availability/freshness, confidence, unlimited and error semantics remain distinct. State and confidence are orthogonal; see D-014.

## D-006 — Development workflow separate from product

Graphify, model routing and handoffs are development infrastructure.

## D-007 — Open-source direction

Cerith should be genuinely open source.

License remains provisional until dependency review.

## D-008 — Native translation of UI references

Web UI references inspire design; production runtime remains native.

## D-009 — Public-ready from first commit

No private information is permitted in committed history merely because the repository starts private.

## D-010 — Zero AI slop

Generated code receives normal engineering review.

Every new file, dependency and abstraction must justify its existence.

## D-011 — Test framework

Los proyectos de pruebas usan `MSTest.Sdk` con Microsoft.Testing.Platform y perfil de extensiones `None`. Su licencia MIT es compatible y queda limitada a pruebas; cobertura e informes se añadirán si el flujo los necesita.

## D-012 — Ejecución de pruebas en .NET 10

`global.json` selecciona Microsoft.Testing.Platform (MTP) para `dotnet test`, requerido por `MSTest.Sdk` con .NET 10.

MTP devuelve `ZeroTests` (código 8) cuando un proyecto no descubre ni ejecuta pruebas. La tolerancia global usada durante CER-M0-001 fue retirada al añadirse las primeras pruebas reales.

`Cerith.Core.Tests` se ejecuta sin tolerancia. Solo `Cerith.Telemetry.Tests` y `Cerith.Integration.Tests`, mientras sigan vacíos, pueden ignorar el código 8 en sus invocaciones individuales. Retirar cada excepción al añadir su primer test real; no añadir tests dummy.

## D-013 — CodeQL según visibilidad

El repositorio privado actual no dispone de code scanning. CodeQL se omite mientras sea privado y se ejecuta automáticamente cuando `github.event.repository.visibility == 'public'`. El job conserva solo los permisos necesarios: `actions: read`, `contents: read` y `security-events: write`.

## D-014 — Contrato fundamental de métricas

`MetricId` conserva texto no vacío exacto con igualdad ordinal case-sensitive y sin gramática cerrada. `MetricState` (freshness/disponibilidad) y `MetricConfidence` (unknown/measured/estimated) son dimensiones ortogonales. Los snapshots usan `MetricValue` tipado y `DateTimeOffset ObservedAt`; la edad se deriva de una hora de referencia. `MetricDescriptor` es provider-independent; source y error code sanitizado pertenecen al snapshot.
