# Third-party components

Components that a published Cerith folder contains besides Cerith itself. Each keeps its own licence; the package of each version is the authoritative text.

| Component | Licence | Source |
| --- | --- | --- |
| LibreHardwareMonitorLib 0.9.6 | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| BlackSharp.Core 1.0.7, DiskInfoToolkit 1.1.2, RAMSPDToolkit-NDD 1.4.2 (via LibreHardwareMonitorLib) | MPL-2.0 | https://www.nuget.org/packages/BlackSharp.Core, https://www.nuget.org/packages/DiskInfoToolkit, https://www.nuget.org/packages/RAMSPDToolkit-NDD |
| HidSharp 2.6.4 | Apache-2.0 | https://www.nuget.org/packages/HidSharp |
| System.CodeDom, System.IO.Ports, System.Management, System.IO.FileSystem.AccessControl, System.Numerics.Tensors | MIT | https://github.com/dotnet/runtime |
| PresentMon API2 loader 2.6.0 | MIT (`PresentMonAPI2Loader.LICENSE.txt` ships beside it) | https://github.com/GameTechDev/PresentMon |
| Windows App SDK, WinUI, WebView2, Windows ML, ONNX Runtime, DirectML, Windows SDK projections | Microsoft licence terms in each NuGet package (see the permission in `LICENSE-EXCEPTION.md`) | https://github.com/microsoft/WindowsAppSDK |

MPL-2.0 allows combination with GPL-3.0 unless a file is marked "Incompatible With Secondary Licenses"; that has not been audited file by file.

Not shipped: `Mono.Posix.NETStandard`, a Linux-only dependency of LibreHardwareMonitor whose package declares no clear licence (D-035).

This inventory is not yet the complete notice bundle that a binary release needs (full licence texts and copyright lines); that is tracked in `.agent/TASKS.md`.
