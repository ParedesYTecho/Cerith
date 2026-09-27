# Handoff — post-merge CER-M1-001

Status: CER-M1-001 DONE. PR #22 recibió PASS del Advisor y se integró por squash en `b7e40447f48cd6afd2b015a94a49d825f28099f9`. M0 — Foundation sigue DONE; M1 — Hardware MVP continúa como milestone actual y no está completo. La siguiente task M1 queda sin definir/sin reclamar hasta que la defina el Advisor.

## Base, rama y probe previo

- Base de implementación verificada: `ebe504b3fd2b1c994da2bff1b62968f3229dd543`.
- PR #23 se integró primero en `94de88be841968be18fd6d3225bd184044347af6`; luego PR #22 se integró por squash en `b7e40447f48cd6afd2b015a94a49d825f28099f9`. Ambos están en el `main` actualizado; no hubo conflicto ni se integró `main` en la feature branch.
- Rama de implementación: `feat/CER-M1-001-lhm-cpu-usage`. Owner único de escritura: Codex.
- Probe fuera del repo, con `LibreHardwareMonitorLib 0.9.6`, `Computer.IsCpuEnabled = true`, sin elevación (`Elevated=False`): se observó un único hardware `Cpu` y exactamente un sensor `SensorType=Load`, `Name=CPU Total`, con `Value` presente. El Identifier se inspeccionó para descartar confusión con los sensores por core y `CPU Core Max`, pero no se persiste. No se capturaron seriales, paths de dispositivo ni inventario bruto.
- Selector de producto: hardware `HardwareType.Cpu` + sensor `SensorType.Load` + nombre ordinal exacto `CPU Total`; más de un match falla explícitamente. No usa Identifier como MetricId ni heurísticas de fallback.

## Implementación

`CpuUsageProvider` en TelemetryService abre LHM con solo CPU habilitada, localiza el sensor, refresca la CPU cada segundo, normaliza y publica por `MetricBus`; `Computer.Close()` se llama al disponer. Se mantienen `NamedPipeMetricServer`, protocolo y `NamedPipeMetricClient` existentes. `SyntheticProvider` permanece para foundation/tests, pero deja de publicarse en el runtime M1. WidgetHost solo se vincula a `cpu.total.usage`, muestra un decimal y conserva Connecting/unavailable.

Descriptor: `cpu.total.usage`, `CPU Total Usage`, `percentage`, `double`, `%`, sin datos de provider. Una lectura finita dentro de 0–100 produce `Fresh`, `Measured`, `Source=LibreHardwareMonitor` y `ObservedAt` tomado tras el refresh. `Value=null` produce `Unavailable`/`Unknown` sin valor. Fallo de refresh produce `Error`/`Unknown` con `cpu_refresh_failed`; NaN, Infinity o porcentaje fuera de 0–100 producen `Error`/`Unknown` con `invalid_percentage`. No se publica cero sustituto, no hay clamp y no se publica `exception.Message`.

Archivo nuevo: `src/Cerith.TelemetryService/CpuUsageProvider.cs` posee únicamente el ciclo de vida LHM y la normalización de esta métrica; Program lo usa ahora. No existe otro archivo con esa responsabilidad. Su coste es una clase concreta acoplada solo a TelemetryService.

Archivos modificados en el slice inicial: `src/Cerith.TelemetryService/{Cerith.TelemetryService.csproj,Program.cs}`, `src/Cerith.WidgetHost/{MainWindow.xaml,MainWindow.xaml.cs}`, `docs/{ARCHITECTURE.md,METRICS.md}` y memoria operativa. La revisión posterior añadió D-019 en `.agent/DECISIONS.md` para registrar la dependencia runtime significativa requerida por `docs/DEPENDENCY_POLICY.md`; no cambió la arquitectura del bus/IPC ni se adoptó una regla general de providers.

## Dependencia y licencias

Única dependencia directa nueva: `LibreHardwareMonitorLib 0.9.6` en TelemetryService, [MPL-2.0](https://www.nuget.org/packages/LibreHardwareMonitorLib/0.9.6). El BCL no expone el sensor LHM verificado; usar la librería exigida evita código de adquisición de hardware propio. Core y WidgetHost no la referencian. El paquete trae bibliotecas transitivas y binarios nativos, con coste de tamaño y superficie de mantenimiento aunque solo se active CPU; restore no informó vulnerabilidades. No se vendorizó código ni se agregó otro paquete directo.

Grafo transitivo resuelto por `dotnet list src/Cerith.TelemetryService/Cerith.TelemetryService.csproj package --include-transitive` para `net10.0-windows10.0.19041.0`:

| Paquete | Versión | Licencia observada |
| --- | --- | --- |
| BlackSharp.Core | 1.0.7 | MPL-2.0 (nuspec) |
| DiskInfoToolkit | 1.1.2 | MPL-2.0 (nuspec) |
| HidSharp | 2.6.4 | Apache-2.0 (LICENSE.txt incluido en el paquete) |
| Mono.Posix.NETStandard | 1.0.0 | **Sin SPDX ni LICENSE/NOTICE en el artefacto local.** El `.nuspec` solo declara `licenseUrl`; la licencia exacta de esta versión queda indeterminada. No se encontró evidencia de licencia incompatible/restrictiva. |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 (nuspec) |
| System.CodeDom | 10.0.2 | MIT (nuspec) |
| System.IO.FileSystem.AccessControl | 5.0.0 | MIT (nuspec) |
| System.IO.Ports | 10.0.3 | MIT (nuspec) |
| System.Management | 10.0.2 | MIT (nuspec) |

También se resolvieron 17 paquetes MIT `runtime.*.runtime.native.System.IO.Ports` versión `10.0.3`: `runtime.android-{arm,arm64,x64,x86}`, `runtime.linux-{arm,arm64,bionic-arm64,bionic-x64,musl-arm,musl-arm64,musl-x64,x64}`, `runtime.maccatalyst-{arm64,x64}`, `runtime.native`, `runtime.osx-{arm64,x64}` (todos con sufijo `.runtime.native.System.IO.Ports`, salvo `runtime.native.System.IO.Ports`). Son entradas del grafo NuGet, no afirmación de que todas se carguen en Windows.

Audit exacto de `Mono.Posix.NETStandard 1.0.0` en caché local: `.nuspec` con `id=Mono.Posix.NETStandard`, `version=1.0.0`, `authors/owners=Microsoft`, `requireLicenseAcceptance=true`, `licenseUrl=https://go.microsoft.com/fwlink/?linkid=869050` y `projectUrl=https://go.microsoft.com/fwlink/?linkid=869051`; sin `<license>`, SPDX ni `<repository>`. La carpeta contiene el `.nupkg` (SHA-256 `FC5EB593B318FDFBB615C7D6EC29328EE52BA0AC256005CF40555E0ECD509276`), `.nupkg.sha512`, `.nupkg.metadata`, `.signature.p7s` y DLLs/archivos nativos para net40, netstandard2.0, Linux, macOS y Windows. El `.nupkg` tiene 26 entradas: metadata/relaciones, `.nuspec`, assemblies por TFM/RID, helpers nativos por RID y firma; no incluye LICENSE/NOTICE. `.nupkg.metadata` registra `source=https://api.nuget.org/v3/index.json` y `contentHash`, coincidente con `project.assets.json`. El SHA-512 de los bytes del archivo coincide con `.nupkg.sha512` y difiere de ese `contentHash`; `dotnet nuget verify --all` pasó y mostró firma Repository de NuGet.org. No se atribuye causa a la diferencia de hashes ni se infiere una licencia de ella.

**Resultado aceptado por el Advisor:** no hay incompatibilidad conocida demostrada para este transitivo; queda una ambigüedad de provenance/third-party-notice, incluida su licencia/SPDX exacta, para resolver o documentar en el audit de dependencias previo al public release. No se añadió un sistema general de THIRD-PARTY-NOTICES.

## Verificación local

- `dotnet build Cerith.sln --configuration Release`: PASS, 0 warnings/errors.
- `dotnet test tests/Cerith.Core.Tests/Cerith.Core.Tests.csproj --configuration Release --no-build`: PASS 39/39.
- `dotnet test tests/Cerith.Telemetry.Tests/Cerith.Telemetry.Tests.csproj --configuration Release --no-build`: PASS 7/7.
- `dotnet test tests/Cerith.Integration.Tests/Cerith.Integration.Tests.csproj --configuration Release --no-build`: PASS 8/8.
- `git diff --check`: PASS.

Demo sin elevación: (1) `dotnet run --project src/Cerith.TelemetryService/Cerith.TelemetryService.csproj --configuration Release --no-build`; (2) verificar `TelemetryService ready: pipe Cerith.Metrics.M0; cpu.total.usage Fresh`; (3) iniciar `src/Cerith.WidgetHost/bin/Release/net10.0-windows10.0.19041.0/Cerith.WidgetHost.exe`; (4) leer el TextBlock de la ventana; (5) cerrar WidgetHost desde la ventana; (6) Ctrl+C en el servicio y observar `TelemetryService stopped.`. UI Automation observó `cpu.total.usage: 13.9 %`, después `15.1 %` y `11.5 %`. No hizo falta carga artificial. Como WidgetHost únicamente recibe snapshots por NamedPipeMetricClient y TelemetryService únicamente publica este valor desde LHM por MetricBus, la lectura visible recorrió el pipeline solicitado. El cierre de WidgetHost y el mensaje de parada se observaron. El wrapper PTY devolvió exit code 1 tras Ctrl+C, por lo que el exit code del ejecutable no se confirmó por separado.

## Revisión y pendientes

Zero-AI-slop: una clase concreta nueva, sin interfaces/managers/factories/DI/scheduler genérico; una métrica, CPU como único grupo LHM, sin cambios a Core, protocolo IPC, linked-source workaround ni otras áreas. No hay tests de hardware CI; la selección del sensor se verificó manualmente en esta máquina. No se ha validado hardware distinto ni ausencia persistente de sensor. Un porcentaje inválido se rechaza explícitamente, sin modificarlo.

Diff stat de implementación: 11 archivos, 176 inserciones y 35 eliminaciones. Commit de implementación `7c9e43bf5ab9ff11a347a7ce00be044e33da80e2`; corrección de provenance `3f3f3b550dff93f65d98aa6ffdc696839a07d06c`. PR [#22](https://github.com/ParedesYTecho/Cerith/pull/22): MERGED por squash en `b7e40447f48cd6afd2b015a94a49d825f28099f9`. Advisor PASS; Build and Test PASS; Repository Quality PASS; CodeQL SKIPPED según D-013. Limitaciones: no se ha probado hardware diverso, CI no prueba sensores reales y solo `cpu.total.usage` está implementada. D-019 está integrada; M1 no está completa. La deuda Mono.Posix de provenance/notice continúa antes del public release, sin incompatibilidad conocida.
