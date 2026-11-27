#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.WidgetHost;

/// <summary>Keeps one TelemetryService running while the app is open.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ServiceSupervisor(string servicePath)
{
    private const string PathOption = "--service-path=";
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StableUptime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(5);

    internal static string ResolvePath(IEnumerable<string> arguments, string appDirectory) =>
        arguments.FirstOrDefault(argument => argument.StartsWith(PathOption, StringComparison.OrdinalIgnoreCase))
            ?[PathOption.Length..] is { Length: > 0 } path
            ? Path.GetFullPath(path)
            : Path.Combine(appDirectory, "Cerith.TelemetryService.exe");

    internal static TimeSpan Delay(int failures) => failures <= 0 ? TimeSpan.Zero
        : TimeSpan.FromSeconds(Math.Min(Math.Pow(2, failures), MaximumDelay.TotalSeconds));

    internal static int NextFailures(int failures, TimeSpan uptime) => uptime >= StableUptime ? 0 : failures + 1;

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        Stopwatch? sinceStart = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!SingleInstance.IsRunning(SingleInstance.ServiceName))
            {
                if (sinceStart is not null) failures = NextFailures(failures, sinceStart.Elapsed);
                await Task.Delay(Delay(failures), cancellationToken).ConfigureAwait(false);
                if (!SingleInstance.IsRunning(SingleInstance.ServiceName))
                {
                    sinceStart = Stopwatch.StartNew();
                    Start();
                }
            }
            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Start()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(servicePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(servicePath)!
            });
        }
        // A failed start counts as a quick exit: the loop retries with a longer delay.
        catch (Exception error) when (error is Win32Exception or IOException) { Debug.WriteLine(error); }
    }
}
