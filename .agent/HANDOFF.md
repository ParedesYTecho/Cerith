# Entrega

De: Codex
Para: siguiente agente
Fecha: 2026-09-23
Tarea: CER-M0-001 — Repository and solution skeleton

## Objetivo

Crear la solución y los siete proyectos acordados, validar las referencias y el build, sin implementar funciones de producto.

## Completado

- Rama de trabajo: `feat/CER-M0-001-bootstrap`.
- `Cerith.sln` incluye cuatro proyectos de producto y tres proyectos de pruebas.
- `Cerith.Core` usa `net10.0`; los proyectos restantes usan `net10.0-windows10.0.19041.0`.
- TelemetryService, WidgetHost y ControlCenter referencian Core.
- Las pruebas usan MSTest SDK 4.4.1 con Microsoft.Testing.Platform y perfil `None`.
- Instalado .NET SDK 10.0.401 tras autorización del usuario. Graphify 0.9.57 ya estaba instalado.
- No se añadió código de producto, provider ni paquete de runtime.
- CER-M0-001 está marcada como `DONE`; CER-M0-002 sigue `TODO` y sin reclamar.

## Archivos cambiados

- `Cerith.sln`, `global.json`, `.github/workflows/build.yml`.
- `src/Cerith.Core/Cerith.Core.csproj`.
- `src/Cerith.TelemetryService/Cerith.TelemetryService.csproj`.
- `src/Cerith.WidgetHost/Cerith.WidgetHost.csproj`.
- `src/Cerith.ControlCenter/Cerith.ControlCenter.csproj`.
- `tests/Cerith.Core.Tests/Cerith.Core.Tests.csproj`.
- `tests/Cerith.Telemetry.Tests/Cerith.Telemetry.Tests.csproj`.
- `tests/Cerith.Integration.Tests/Cerith.Integration.Tests.csproj`.
- `.agent/DECISIONS.md`, `.agent/STATE.md`, `.agent/TASKS.md`, `.agent/WORKLOG.md`, `.agent/HANDOFF.md`.

## Verificación

- OK: `dotnet build Cerith.sln`; exit 0, cero advertencias y cero errores.
- LIMITACIÓN: `dotnet test Cerith.sln` directo termina con exit 1; MTP devuelve `ZeroTests` (código 8) en los tres proyectos, total 0. No se descubrieron ni ejecutaron pruebas; esto no son pruebas fallidas.
- OK CON EXCEPCIÓN TEMPORAL: el comando de pruebas de CI termina con exit 0 únicamente porque incluye `--ignore-exit-code 8` para este esqueleto sin pruebas. D-012 y CER-M0-002 registran cuándo retirarlo.
- PASS: solución, XML de proyectos, rutas y referencias validados.
- PASS: `git diff --check` y escaneo de archivos nuevos/modificados para secretos y rutas personales.
- PASS: Graphify detectó 46 archivos (9 código, 37 documentos, 0 sensibles omitidos) y generó `graphify-out/GRAPH_REPORT.md`, `graphify-out/graph.html` y `graphify-out/graph.json`. Son salidas ignoradas por Git.
- Graphify: sin endpoints huérfanos, endpoints ausentes ni self-loops. La vista no dirigida colapsa una pareja entre `docs_architecture_core_contracts` y `docs_architecture_telemetry_service`: `implements` y `shares_data_with`. El informe contiene los conteos actuales y señala nodos débilmente conectados propios del esqueleto.
- El runner de agentes no expone metadatos `usage`; el informe indica que el coste real de tokens no está disponible.

## Decisiones

- D-011: MSTest SDK 4.4.1 con perfil `None`, limitado a pruebas.
- D-012: política temporal de `ZeroTests`; la retirada está ligada a la primera prueba ejecutable en CER-M0-002 (detalle en `DECISIONS.md`).
- No se añadieron pruebas vacías ni código de producto.

## Bloqueos

Ninguno.

## No rehacer

- No recrear la solución ni cambiar sus referencias sin evidencia de un fallo.
- No iniciar CER-M0-002 ni añadir providers o telemetría durante esta tarea.

## Siguiente tarea (sin reclamar)

CER-M0-002 — Metric contracts sigue `TODO` y `unclaimed`; no se ha iniciado.
