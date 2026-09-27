# Handoff — CER-M0-008 native visual pipeline

Fecha: 2026-09-27
Estado: CER-M0-008 DONE; review independiente del Advisor PASS y squash merge confirmado.
Rama canónica: `main`
`main` y `origin/main`: `15646293886e8951779c45d4bf894c3017c0bc7c` (verificadas tras `git fetch origin main` y fast-forward local; árbol limpio antes de reconciliar).
Rama de implementación: `feat/CER-M0-008-native-visual-pipeline`
PR de CER-M0-008: https://github.com/ParedesYTecho/Cerith/pull/18
SHA de squash: `15646293886e8951779c45d4bf894c3017c0bc7c`.

## Slice implementado

`TelemetryService.exe` registra `SyntheticProvider`, ejecuta el servidor Named Pipe y publica `synthetic.counter` cada segundo. `WidgetHost.exe` abre la ventana WinUI 3 antes de iniciar IPC, se suscribe por `NamedPipeMetricClient` y presenta los snapshots frescos como `synthetic.counter: <valor>`. El recorrido es `SyntheticProvider → MetricBus → Named Pipe → NamedPipeMetricClient → WidgetHost → TextBlock`. El nombre local del pipe vive en `MetricIpcProtocol`; WidgetHost no referencia SyntheticProvider ni TelemetryService.

WinUI se inicia unpackaged y framework-dependent con bootstrap automático. La lectura async conserva el contexto del DispatcherQueue de WinUI y actualiza TextBlock en el hilo UI. AppWindow.Closing cancela el token, espera la tarea de lectura y su `await using` para disponer el cliente, y entonces cierra la ventana. El handler de cierre observa las excepciones async. El fallo de conexión, un snapshot inesperado o EOF muestran unavailable. No hay reconnect automático.

## Archivos nuevos

- `src/Cerith.TelemetryService/Program.cs`: arranque, publicación periódica y cierre por Ctrl+C.
- `src/Cerith.WidgetHost/App.xaml`: punto de entrada XAML requerido por WinUI.
- `src/Cerith.WidgetHost/App.xaml.cs`: crea y activa la ventana.
- `src/Cerith.WidgetHost/MainWindow.xaml`: ventana y único TextBlock visible.
- `src/Cerith.WidgetHost/MainWindow.xaml.cs`: consumo IPC, estados de texto y cancelación al cerrar.

Modificados: los dos `.csproj` de producción (salidas ejecutables y WinUI), `MetricIpcProtocol.cs` (nombre del pipe), `Cerith.Integration.Tests.csproj` (compila el mismo archivo cliente sin cargar el bootstrapper del ejecutable UI), `docs/ARCHITECTURE.md`, `.agent/DECISIONS.md` (D-018) y memoria operativa. No se añadieron proyectos, modelos de widget, paquetes de test ni UI automation.

## Dependencia y deuda

Única dependencia directa nueva: `Microsoft.WindowsAppSDK` 2.5.1, stable de 2026-09-16, para WinUI 3; Microsoft Software License Terms. El proyecto es framework-dependent y requiere Windows App Runtime 2.5.1 instalado. El Advisor aceptó esta elección para M0. La compatibilidad de su licencia y transitivas con la dirección GPL-3.0-or-later provisional NO está resuelta: cerrarla antes de fijar/publicar la licencia definitiva. No optimizar las transitivas sin medir footprint real. El linked source del cliente en Integration.Tests fue aceptado como workaround limitado de M0; no convertirlo en patrón permanente ni abrir ahora un proyecto/abstracción.

## Reproducción local

Desde la raíz del repo, ejecutar `dotnet restore Cerith.sln` y `dotnet build Cerith.sln --configuration Release --no-restore`. En dos consolas distintas, iniciar primero `& .\src\Cerith.TelemetryService\bin\Release\net10.0-windows10.0.19041.0\Cerith.TelemetryService.exe` y después `& .\src\Cerith.WidgetHost\bin\Release\net10.0-windows10.0.19041.0\Cerith.WidgetHost.exe`. Cerrar WidgetHost con la X y TelemetryService con Ctrl+C.

En el demo local, UI Automation leyó primero `synthetic.counter: Connecting…`; luego `synthetic.counter: 83` y, en otra ejecución, `synthetic.counter: 38` seguido de `synthetic.counter: 40` dos segundos después. Tras ajustar el cierre, una nueva ejecución pasó de Connecting a `synthetic.counter: 3`; `CloseMainWindow()` cerró WidgetHost con exit code 0. Sin servidor, la ventana siguió abierta y mostró `synthetic.counter: unavailable`. Un evento Ctrl+C enviado al grupo de consola cerró TelemetryService con exit code 0. Son observaciones locales; no prueban otras cuentas o máquinas.

## Verificación de CER-M0-008

- `dotnet restore Cerith.sln`: PASS.
- `dotnet build Cerith.sln --configuration Release --no-restore`: PASS, 0 advertencias y 0 errores tras aislar la dependencia de WinUI de Integration.Tests.
- `dotnet test tests/Cerith.Core.Tests/Cerith.Core.Tests.csproj --configuration Release --no-build --verbosity normal`: 39/39 PASS.
- `dotnet test tests/Cerith.Telemetry.Tests/Cerith.Telemetry.Tests.csproj --configuration Release --no-build --verbosity normal`: 7/7 PASS.
- `dotnet test tests/Cerith.Integration.Tests/Cerith.Integration.Tests.csproj --configuration Release --no-build --verbosity normal`: 8/8 PASS.

PR #18: Build and Test PASS, Repository Quality PASS, CodeQL SKIPPED según D-013; Advisor independiente PASS (lifecycle/cancellation, UI thread, TelemetryService lifecycle y scope). No se añadieron correcciones de implementación tras la review. Graphify code-only se actualizó durante la implementación; su label drift queda fuera de esta task. La reconciliación modifica solo `.agent/STATE.md`, `.agent/HANDOFF.md`, `.agent/TASKS.md` y `.agent/WORKLOG.md`. No reclamar CER-M0-009 ni otra task en esta sesión. Dependabot #3/#4 permanece separado.
