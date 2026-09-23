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
