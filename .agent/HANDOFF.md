# Handoff — CER-M0-009 M0 exit diagnostics

Fecha: 2026-09-27
Estado: READY FOR ADVISOR REVIEW; M0 no está declarado DONE.
Rama: `feat/CER-M0-009-m0-exit-diagnostics`, base `main` = `origin/main` = `4a5381c06cb99479e537197848eb15ccf1dba370` al iniciar, árbol limpio. Commit de implementación: `7bbd97d00a370d794bc3e3c2e9cb23076288c5bb`. PR: https://github.com/ParedesYTecho/Cerith/pull/20 (abierto; checks en curso al redactar).

## Resultado

`TelemetryService` escribe `TelemetryService ready: pipe Cerith.Metrics.M0; metric synthetic.counter` una vez, después de que el servidor haya creado el pipe, y `TelemetryService stopped.` tras cierre normal. Si el pipe falla al arrancar, se propaga el error sin anunciar ready. No se registran snapshots.

`docs/ROADMAP.md` define ControlCenter shell como el proyecto existente y su dirección de dependencias; una UI funcional espera una capability concreta. El baseline diagnostics M0 es ready/pipe/métrica y cierre normal en TelemetryService, más Connecting/valor/unavailable existentes en WidgetHost. Health rico de hardware/providers sigue en M1. El exit M0 continúa siendo el pipeline sintético visible, con tests y baseline IPC medido de D-016. README y roadmap son compatibles.

Archivos modificados: `src/Cerith.TelemetryService/Program.cs`, `docs/ROADMAP.md`, `.agent/TASKS.md`, `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/WORKLOG.md`. Archivos, proyectos, dependencias y abstracciones nuevos: ninguno. WidgetHost sin cambios. Sin desviaciones de scope.

## Verificación

- `dotnet build Cerith.sln --configuration Release`: PASS, 0 warnings/errors.
- `dotnet test tests/Cerith.Core.Tests/Cerith.Core.Tests.csproj --configuration Release --no-build --verbosity normal`: PASS 39/39.
- `dotnet test tests/Cerith.Telemetry.Tests/Cerith.Telemetry.Tests.csproj --configuration Release --no-build --verbosity normal`: PASS 7/7.
- `dotnet test tests/Cerith.Integration.Tests/Cerith.Integration.Tests.csproj --configuration Release --no-build --verbosity normal`: PASS 8/8.
- Demo manual: primer proceso anunció ready; Ctrl+C produjo stopped. Un segundo proceso concurrente falló por pipe ocupado sin anunciar ready. El shell PTY devolvió 1 al recibir Ctrl+C; no se confirmó por separado el exit code del ejecutable.
- UI de WidgetHost: sin demo nuevo; los tres estados están en `MainWindow.xaml(.cs)` y habían sido observados en CER-M0-008.
- `git diff --check` y chequeo de higiene de nombres sensibles configurado en CI: PASS.

Los requisitos documentados de M0 parecen satisfechos tras esta task según código, pruebas, demo previo de UI y baseline de IPC existente; el Advisor debe decidir su aceptación y si M0 puede declararse DONE. No mergear antes de esa review. Dependabot #3/#4, Graphify label drift y licencia siguen fuera de scope.
