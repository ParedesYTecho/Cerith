#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cerith.TelemetryService;

internal readonly record struct ForegroundSample(int ProcessId, string ProcessName, bool CoversMonitor);

internal readonly record struct GameTarget(int? ProcessId, bool Probation);

/// <summary>
/// Chooses which process to measure (D-034): a foreground window covering its whole monitor, kept only if it then
/// presents frames. The target survives losing focus and is released when it exits or another game replaces it.
/// </summary>
internal sealed class GameTargetSelector
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Proof = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProbationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    // Full-screen applications that present frames and are not games.
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "LockApp", "ApplicationFrameHost",
        "dwm", "msedge", "msedgewebview2", "chrome", "firefox", "brave", "opera", "vivaldi", "steamwebhelper", "vlc",
        "mpv", "mpc-hc64", "PotPlayerMini64", "Microsoft.Media.Player", "Video.UI", "mstsc", "POWERPNT",
        "Cerith.WidgetHost", "Cerith.TelemetryService"
    };

    private readonly Dictionary<int, TimeSpan> _rejectedUntil = [];
    private int? _target;
    private int? _candidate;
    private bool _proven;
    private TimeSpan _candidateSince;
    private TimeSpan _probationSince;
    private TimeSpan? _freshSince;

    /// <param name="targetAlive">False once the current target exited or could not be identified.</param>
    /// <param name="fresh">Whether the last reading of the current target showed displayed frames.</param>
    internal GameTarget Next(ForegroundSample? sample, bool targetAlive, bool fresh, TimeSpan now)
    {
        if (_target is int target)
        {
            if (!targetAlive) Release(target, now);
            else if (!_proven)
            {
                _freshSince = fresh ? _freshSince ?? now : null;
                if (now - _freshSince >= Proof) _proven = true;
                else if (now - _probationSince >= ProbationTimeout) Release(target, now);
            }
        }

        int? candidate = sample is { CoversMonitor: true } seen && seen.ProcessId != _target &&
            !NotGames.Contains(seen.ProcessName) &&
            !(_rejectedUntil.TryGetValue(seen.ProcessId, out var until) && now < until)
            ? seen.ProcessId : null;
        if (candidate != _candidate)
        {
            _candidate = candidate;
            _candidateSince = now;
        }
        if (candidate is not null && now - _candidateSince >= Dwell)
        {
            _target = candidate;
            _candidate = null;
            _proven = false;
            _probationSince = now;
            _freshSince = null;
        }

        return new GameTarget(_target, _target is not null && !_proven);
    }

    private void Release(int target, TimeSpan now)
    {
        if (!_proven) _rejectedUntil[target] = now + Cooldown;
        _target = null;
        _proven = false;
    }
}

/// <summary>Reads the foreground window's process and whether it covers its monitor. No title or path is read.</summary>
internal sealed class ForegroundProbe
{
    private static readonly IntPtr PerMonitorAwareV2 = -4;
    private int _processId;
    private string? _processName;

    internal ForegroundSample? TryRead()
    {
        // Without per-monitor awareness Windows would report scaled coordinates on mixed-DPI desktops.
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
                return null;
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetWindowRect(window, out var bounds) ||
                !GetMonitorInfoW(MonitorFromWindow(window, 2), ref monitor) || !bounds.Equals(monitor.Monitor))
                return null;

            if (processId != _processId || _processName is null)
            {
                using var process = Process.GetProcessById(processId);
                _processName = process.ProcessName;
                _processId = processId;
            }
            return new ForegroundSample(processId, _processName, true);
        }
        // The process ended between the two calls.
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return null; }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
}
