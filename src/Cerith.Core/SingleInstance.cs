#nullable enable

using System;
using System.Runtime.Versioning;
using System.Threading;

namespace Cerith.Core;

/// <summary>
/// One process per user session for a name. The holder is told when someone signals that name:
/// the app brings its window forward, the service stops.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SingleInstance : IDisposable
{
    public const string AppName = @"Local\Cerith.App";
    public const string ServiceName = @"Local\Cerith.TelemetryService";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle signal, Action onSignal)
    {
        _mutex = mutex;
        _signal = signal;
        _wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => onSignal(), null, Timeout.Infinite, false);
    }

    /// <summary>Returns null when another process already holds the name.</summary>
    public static SingleInstance? TryAcquire(string name, Action onSignal)
    {
        // The event is created first, so a running instance can always be signalled.
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName(name));
        // The mutex is never owned: its existence is the claim, so any thread may release it.
        var mutex = new Mutex(false, name, out var createdNew);
        if (createdNew) return new SingleInstance(mutex, signal, onSignal);
        mutex.Dispose();
        signal.Dispose();
        return null;
    }

    public static bool IsRunning(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    public static bool Signal(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(SignalName(name), out var signal)) return false;
        using (signal) return signal.Set();
    }

    private static string SignalName(string name) => name + ".Signal";

    public void Dispose()
    {
        // Wait for a callback already running, so it cannot touch state the caller disposes next.
        using var done = new ManualResetEvent(false);
        if (_wait.Unregister(done)) done.WaitOne();
        _signal.Dispose();
        _mutex.Dispose();
    }
}
