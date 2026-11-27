# Publishes the app and its telemetry service into one folder: run Cerith.WidgetHost.exe from there.
param([string]$Output = (Join-Path $PSScriptRoot 'publish'))
$ErrorActionPreference = 'Stop'
foreach ($project in 'Cerith.TelemetryService', 'Cerith.WidgetHost') {
    dotnet publish (Join-Path $PSScriptRoot "src/$project/$project.csproj") -c Release -r win-x64 --self-contained false -o $Output
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}
