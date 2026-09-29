#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal sealed class PresentMonTelemetryProvider : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly MetricBus _bus;
    private readonly int? _processId;
    private readonly Func<int, IPresentMonCapture> _openCapture;
    private readonly MetricDescriptor _fps = new(new MetricId("game.fps.display"), "Displayed FPS",
        "rate", MetricValueKind.Double, unit: "FPS");
    private readonly MetricDescriptor _frameTime = new(new MetricId("game.frametime.display"), "Displayed Frame Time",
        "duration", MetricValueKind.Double, unit: "ms");
    private readonly MetricDescriptor _p1 = new(new MetricId("game.fps.display.p01"), "Displayed FPS P1 (10 s)",
        "rate", MetricValueKind.Double,
        description: "PresentMon PM_STAT_PERCENTILE_01 over a requested trailing 10-second window: reciprocal of the linearly interpolated 99th percentile of display-change intervals, including zero-valued samples for non-displayed frames. Warm-up begins with the first positive displayed-change observation and resets after more than two seconds without recent evidence. PresentMon retains at most 4096 frame samples per swap chain, so actual history can be shorter.",
        unit: "FPS");
    private IPresentMonCapture? _capture;
    private PresentMonReading _lastReading = new(MetricState.Unavailable);
    private DateTimeOffset _retryAt;
    private bool _targetStopped;
    private bool _disposed;

    public PresentMonTelemetryProvider(MetricRegistry registry, MetricBus bus, int? processId)
        : this(registry, bus, processId, static pid => new PresentMonCapture(pid))
    {
    }

    internal PresentMonTelemetryProvider(MetricRegistry registry, MetricBus bus, int? processId,
        Func<int, IPresentMonCapture> openCapture)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _openCapture = openCapture ?? throw new ArgumentNullException(nameof(openCapture));
        if (processId is <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        _processId = processId;
        if (!registry.TryRegister(_fps) || !registry.TryRegister(_frameTime) || !registry.TryRegister(_p1))
            throw new InvalidOperationException("PresentMon metric registration failed.");
    }

    public async ValueTask PublishNextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_processId is null || _targetStopped)
        {
            _lastReading = new PresentMonReading(MetricState.Stopped, P1State: MetricState.Stopped);
        }
        else
        {
            ReadNext(cancellationToken);
        }

        await PublishAsync(_fps, _lastReading.State, _lastReading.Fps, _lastReading.ErrorCode, cancellationToken)
            .ConfigureAwait(false);
        await PublishAsync(_frameTime, _lastReading.State, _lastReading.FrameTimeMilliseconds, _lastReading.ErrorCode,
            cancellationToken).ConfigureAwait(false);
        await PublishAsync(_p1, _lastReading.P1State, _lastReading.P1Fps, _lastReading.P1ErrorCode, cancellationToken)
            .ConfigureAwait(false);
    }

    private void ReadNext(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_capture is null && DateTimeOffset.UtcNow >= _retryAt)
        {
            try { _capture = _openCapture(_processId!.Value); }
            catch (PresentMonCaptureException error)
            {
                RecordFailure(error.State, error.ErrorCode);
                return;
            }
            catch (DllNotFoundException) { RecordFailure(MetricState.Error, "presentmon_loader_missing"); return; }
            catch (BadImageFormatException) { RecordFailure(MetricState.Error, "presentmon_architecture"); return; }
            catch (EntryPointNotFoundException) { RecordFailure(MetricState.Error, "presentmon_loader_invalid"); return; }
            catch (Exception) { RecordFailure(MetricState.Error, "presentmon_runtime_failure"); return; }
        }

        if (_capture is null) return;
        var captureFailure = false;
        try { _lastReading = Normalize(_capture.Poll()); }
        catch (PresentMonCaptureException error)
        {
            captureFailure = true;
            RecordFailure(error.State, error.ErrorCode);
        }
        catch (DllNotFoundException) { captureFailure = true; RecordFailure(MetricState.Error, "presentmon_loader_missing"); }
        catch (BadImageFormatException) { captureFailure = true; RecordFailure(MetricState.Error, "presentmon_architecture"); }
        catch (EntryPointNotFoundException) { captureFailure = true; RecordFailure(MetricState.Error, "presentmon_loader_invalid"); }
        catch (Exception) { captureFailure = true; RecordFailure(MetricState.Error, "presentmon_runtime_failure"); }

        if (_lastReading.State == MetricState.Stopped)
        {
            _targetStopped = true;
            CloseCapture();
        }
        else if (captureFailure ||
                 (_lastReading.State == MetricState.Error &&
                  _lastReading.ErrorCode is not ("multiple_swap_chains" or "invalid_reading")))
        {
            CloseCapture();
            _retryAt = DateTimeOffset.UtcNow + RetryDelay;
        }
    }

    private void RecordFailure(MetricState state, string? errorCode)
    {
        var code = state == MetricState.Error ? errorCode ?? "presentmon_api_failure" : null;
        _lastReading = new PresentMonReading(state, ErrorCode: code, P1State: state, P1ErrorCode: code);
        if (state is MetricState.Error or MetricState.Unavailable)
            _retryAt = DateTimeOffset.UtcNow + RetryDelay;
        if (state == MetricState.Stopped) _targetStopped = true;
    }

    private static PresentMonReading Normalize(PresentMonReading reading)
    {
        var averages = NormalizeMetric(reading.State, reading.Fps,
            IsFinitePositive(reading.Fps) && IsFinitePositive(reading.FrameTimeMilliseconds), reading.ErrorCode);
        var p1 = NormalizeMetric(reading.P1State, reading.P1Fps,
            IsFinitePositive(reading.P1Fps), reading.P1ErrorCode);
        return new PresentMonReading(averages.State, averages.Value,
            averages.State == MetricState.Fresh ? reading.FrameTimeMilliseconds : null,
            averages.ErrorCode, p1.State, p1.Value, p1.ErrorCode);
    }

    private static bool IsFinitePositive(double? value) =>
        value is double number && double.IsFinite(number) && number > 0;

    private static (MetricState State, double? Value, string? ErrorCode) NormalizeMetric(
        MetricState state, double? value, bool validFreshValue, string? errorCode)
    {
        if (state == MetricState.Fresh && !validFreshValue)
        {
            state = MetricState.Error;
            errorCode = "invalid_reading";
        }
        if (state != MetricState.Fresh)
        {
            value = null;
            if (state == MetricState.Error) errorCode ??= "presentmon_api_failure";
            else if (state is not (MetricState.Unavailable or MetricState.Stopped))
            {
                state = MetricState.Error;
                errorCode = "presentmon_invalid_state";
            }
            else errorCode = null;
        }
        return (state, value, errorCode);
    }

    private async ValueTask PublishAsync(MetricDescriptor descriptor, MetricState state, double? value,
        string? errorCode, CancellationToken cancellationToken)
    {
        var hasValue = state == MetricState.Fresh && value.HasValue;
        var snapshot = new MetricSnapshot(descriptor.Id,
            hasValue ? MetricValue.From(value!.Value) : null,
            DateTimeOffset.UtcNow,
            state,
            hasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
            source: "PresentMon",
            errorCode: state == MetricState.Error ? errorCode : null);
        await _bus.PublishAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    private void CloseCapture()
    {
        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseCapture();
    }
}
