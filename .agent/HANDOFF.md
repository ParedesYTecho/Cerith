# Handoff — CER-M0-006 IPC prototype

Fecha: 2026-09-26
Estado: CER-M0-006 DONE; PR #14 pasó review independiente del Advisor con PASS y se mergeó mediante squash en `dc6c051f0734fc5eb8a301a16bda671d2ba94221`. No iniciar aún la implementación IPC/WidgetHost; no hay una task reclamada.
Branch: docs/reconcile-CER-M0-006-post-merge
Base: main / origin/main dc6c051f0734fc5eb8a301a16bda671d2ba94221
HEAD SHA del commit de benchmark y decisión: f233106af2d9b7ac2ee79cd4e63e46ea476ecc97
PR: https://github.com/ParedesYTecho/Cerith/pull/14

## Candidatos y decisión

Probados: Windows Named Pipes, TCP loopback en 127.0.0.1 y AF_UNIX. D-016 selecciona Named Pipes para la primera integración por su menor carga de discovery y lifecycle, endpoint sin puerto/ruta, aislamiento CurrentUserOnly y cleanup por handle. AF_UNIX fue más rápido, pero requiere ACL del directorio y limpieza/recuperación de endpoint. TCP loopback requiere entregar el puerto efímero o gestionar un puerto fijo; no autentica procesos locales.

Condición para la implementación siguiente: mantener el requisito local-only y demostrar el rechazo de clientes remotos. Named Pipes admiten conexiones remotas; `CurrentUserOnly` por sí solo no demuestra origen local ni satisface el requisito. Cross-account no se probó con una segunda cuenta.

## Harness y comando

Harness C# temporal, solo SDK/BCL, fuera del repo en el directorio temp del sistema. El proceso padre lanzó procesos servidor y cliente separados. Los tres usaron el mismo payload determinista, framing de prefijo uint32 big-endian y request/echo; sin JSON ni serialización Cerith. Dos ejecuciones completas; por celda, 200 warmups y 5000 RTT medidos sobre conexión establecida. Las funciones server/client del harness usaron el parámetro de modo y transport.

Comandos ejecutados:

- dotnet build <SYSTEM_TEMP>/Cerith.IpcSpike.csproj --configuration Release
- dotnet <SYSTEM_TEMP>/bin/Release/net10.0-windows10.0.19041.0/Cerith.IpcSpike.dll

El harness temporal y sus outputs se eliminaron; no hay harness ni binarios del spike tracked.

## Resultados completos

Entorno: Windows 11 Pro 10.0.26200 build 26200; SDK .NET 10.0.401, runtime 10.0.12. RTT y p95 en microsegundos; CPU client/server/combined en milisegundos por 5000 RTT; throughput en RTT/s. Valores en cada celda: ejecución 1 / ejecución 2.

| Payload | Transport | Median RTT | p95 RTT | CPU client/server/combined | RTT/s |
| --- | --- | ---: | ---: | ---: | ---: |
| 256 B | Named Pipes | 80.900 / 77.900 | 127.900 / 121.400 | 906.250/734.375/1640.625 · 593.750/812.500/1406.250 | 11480 / 11945 |
| 256 B | TCP loopback | 80.600 / 79.300 | 119.500 / 114.700 | 390.625/546.875/937.500 · 578.125/468.750/1046.875 | 11634 / 11955 |
| 256 B | AF_UNIX | 44.100 / 44.900 | 81.300 / 82.700 | 421.875/296.875/718.750 · 343.750/343.750/687.500 | 21953 / 21491 |
| 4096 B | Named Pipes | 79.000 / 80.400 | 125.000 / 123.400 | 687.500/640.625/1328.125 · 671.875/796.875/1468.750 | 11727 / 11568 |
| 4096 B | TCP loopback | 78.500 / 85.700 | 127.700 / 124.600 | 421.875/640.625/1062.500 · 437.500/671.875/1109.375 | 11486 / 11008 |
| 4096 B | AF_UNIX | 45.200 / 45.600 | 82.200 / 81.800 | 421.875/296.875/718.750 · 328.125/437.500/765.625 | 21835 / 21948 |

CPU viene de Process.TotalProcessorTime en cada proceso durante la sección medida; combinado es client + server. Los números incluyen runtime async y overhead del harness; no son una garantía de producto. Metodología y limitaciones están en docs/PERFORMANCE.md.

## Seguridad y comportamiento

- Named Pipes: async byte mode, CurrentUserOnly en servidor/cliente, endpoint local "." sin port discovery. Cleanup/reconnect PASS.
- TCP: listener ligado solo a 127.0.0.1, puerto efímero comunicado por stdout del padre, sin handshake/auth. Cleanup/reconnect PASS.
- AF_UNIX: path de 70 caracteres, cinco ACL rules heredadas del directorio temp, cero explícitas, sin allow a Everyone/Authenticated Users/Builtin Users; el endpoint se eliminó al cerrar. Cleanup/reconnect PASS.
- Para los tres: dos disconnect/reconnect funcionales, cancelación de read async a 400 ms sin colgar, endpoint/handle liberado. PASS en ambas ejecuciones.

## Archivos finales

.agent/DECISIONS.md; .agent/HANDOFF.md; .agent/STATE.md; .agent/TASKS.md; .agent/WORKLOG.md; docs/PERFORMANCE.md. Sin archivos de producto, paquetes o proyectos permanentes.

## Verificación

- Release build: PASS, cero warnings/errores.
- Core: PASS 39/39.
- Telemetry: PASS 7/7.
- Integration: PASS, 0 tests con la excepción ZeroTests existente.
- git diff --check: PASS.
- Repository Quality filename scan: PASS (61 archivos tracked).
- CI remoto del PR #14: Build and Test PASS; Repository Quality PASS; CodeQL SKIPPED según D-013.

PR #14: review independiente del Advisor PASS; merge mediante squash en `dc6c051f0734fc5eb8a301a16bda671d2ba94221`. D-016 y el baseline medido están integrados en `.agent/DECISIONS.md` y `docs/PERFORMANCE.md`. CI remoto: Build and Test PASS; Repository Quality PASS; CodeQL SKIPPED según D-013. Dependabot #3/#4 sigue separado. Graphify label drift continúa pendiente como tooling, no como task de producto.
