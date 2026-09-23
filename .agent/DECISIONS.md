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

Missing, stale, estimated, unlimited and error states remain distinct.

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

`--ignore-exit-code 8` en CI es una excepción temporal de CER-M0-001: existen proyectos de pruebas, pero todavía no hay pruebas ejecutables. El comando directo `dotnet test Cerith.sln` termina con exit code 1 porque MTP devuelve `ZeroTests` (código 8); no se descubre ni ejecuta ninguna prueba. Esto no representa pruebas fallidas: no se ejecutó ninguna.

La excepción DEBE retirarse en el mismo cambio que añada la primera prueba ejecutable real. Desde entonces, cero pruebas debe volver a hacer fallar CI. Cuando sea apropiado, usar `--minimum-expected-tests 1` para hacer explícita esa garantía.
