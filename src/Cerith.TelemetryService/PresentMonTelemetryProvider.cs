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
        if (!registry.TryRegister(_fps) || !registry.TryRegister(_frameTime))
            throw new InvalidOperationException("PresentMon metric registration failed.");
    }

    public async ValueTask PublishNextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_processId is null || _targetStopped)
        {
            _lastReading = new PresentMonReading(MetricState.Stopped);
        }
        else
        {
            ReadNext(cancellationToken);
        }

        await PublishAsync(_fps, _lastReading.Fps, cancellationToken).ConfigureAwait(false);
        await PublishAsync(_frameTime, _lastReading.FrameTimeMilliseconds, cancellationToken).ConfigureAwait(false);
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
        _lastReading = new PresentMonReading(state, ErrorCode: state == MetricState.Error
            ? errorCode ?? "presentmon_api_failure"
            : null);
        if (state is MetricState.Error or MetricState.Unavailable)
            _retryAt = DateTimeOffset.UtcNow + RetryDelay;
        if (state == MetricState.Stopped) _targetStopped = true;
    }

    private static PresentMonReading Normalize(PresentMonReading reading)
    {
        if (reading.State == MetricState.Fresh)
        {
            if (reading.Fps is not double fps || reading.FrameTimeMilliseconds is not double frameTime ||
                !double.IsFinite(fps) || !double.IsFinite(frameTime) || fps <= 0 || frameTime <= 0)
                return new PresentMonReading(MetricState.Error, ErrorCode: "invalid_reading");
            return reading with { ErrorCode = null };
        }

        return reading.State switch
        {
            MetricState.Unavailable => new PresentMonReading(MetricState.Unavailable),
            MetricState.Stopped => new PresentMonReading(MetricState.Stopped),
            MetricState.Error => new PresentMonReading(MetricState.Error,
                ErrorCode: reading.ErrorCode ?? "presentmon_api_failure"),
            _ => new PresentMonReading(MetricState.Error, ErrorCode: "presentmon_invalid_state")
        };
    }

    private async ValueTask PublishAsync(MetricDescriptor descriptor, double? value,
        CancellationToken cancellationToken)
    {
        var hasValue = _lastReading.State == MetricState.Fresh && value.HasValue;
        var snapshot = new MetricSnapshot(descriptor.Id,
            hasValue ? MetricValue.From(value!.Value) : null,
            DateTimeOffset.UtcNow,
            _lastReading.State,
            hasValue ? MetricConfidence.Measured : MetricConfidence.Unknown,
            source: "PresentMon",
            errorCode: _lastReading.State == MetricState.Error ? _lastReading.ErrorCode : null);
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
