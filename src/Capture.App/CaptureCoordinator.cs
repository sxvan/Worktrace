using System.Diagnostics;
using System.Threading.Channels;
using Capture.Core.Monitoring;
using Capture.Core.Pipeline;
using Capture.Core.Triggers;
using Capture.Storage;
using Capture.Windows;
using Serilog;
using ActivityEvent = Capture.Core.Monitoring.ActivityEvent;

namespace Capture.App;

/// <summary>
/// The single consumer of monitor signals: stores events, drives the trigger engine, captures on
/// its decisions and hands the result to the <see cref="CapturePipeline"/>. Runs off the monitor
/// thread, and leaves encoding to the pipeline thread, so event handling is never held up.
/// </summary>
public sealed class CaptureCoordinator(
    ActivityTracker tracker,
    CaptureTriggerEngine triggers,
    CaptureDatabase database,
    ScreenCapturer capturer,
    CapturePipeline pipeline,
    TimeProvider time)
{
    private bool _lastCaptureFoundNothing;

    public async Task RunAsync(ChannelReader<MonitorSignal> signals, CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            MonitorSignal? signal = await WaitForSignalOrDueTimeAsync(signals, shutdown);
            if (signal is not null)
            {
                Handle(signal);
            }

            if (triggers.Poll(time.GetUtcNow()) is { } decision)
            {
                try
                {
                    await CaptureAsync(decision, shutdown);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    // Fail closed: whatever went wrong, this capture is gone and nothing was saved.
                    Log.Error("Capture failed: {ErrorType} 0x{HResult:X8}", ex.GetType().Name, ex.HResult);
                    Console.WriteLine($"{CapturePipeline.Prefix(decision)} -> failed ({ex.GetType().Name})");
                }
            }
        }
    }

    private async Task CaptureAsync(CaptureDecision decision, CancellationToken shutdown)
    {
        // Later milestone: skip here while the focused element is a password field (UI Automation IsPassword).

        var stopwatch = Stopwatch.StartNew();
        CaptureResult result = await capturer.CaptureAsync(decision.Timestamp, shutdown);

        switch (result.Outcome)
        {
            case CaptureOutcome.NoAllowedWindow:
                if (!_lastCaptureFoundNothing)
                {
                    _lastCaptureFoundNothing = true;
                    database.InsertEvent(new ActivityEvent(decision.Timestamp, EventType.Other));
                }
                Log.Information("Capture ({Trigger}): no allowed window visible", decision.Trigger.ToDbName());
                Console.WriteLine($"{CapturePipeline.Prefix(decision)} -> nothing to capture (no allowed window visible)");
                break;

            case CaptureOutcome.Discarded:
                Log.Warning("Capture ({Trigger}) discarded: {Reason}", decision.Trigger.ToDbName(), result.Reason);
                Console.WriteLine($"{CapturePipeline.Prefix(decision)} -> discarded ({result.Reason})");
                break;

            case CaptureOutcome.Captured:
                _lastCaptureFoundNothing = false;
                pipeline.Enqueue(decision, result, stopwatch.ElapsedMilliseconds);
                break;
        }
    }

    private void Handle(MonitorSignal signal)
    {
        foreach (ActivityEvent e in tracker.Process(signal))
        {
            database.InsertEvent(e);
            Log.Information("Event {Type}", e.Type.ToDbName());
            Console.WriteLine($"{e.Timestamp.ToLocalTime():HH:mm:ss}  {e.Type.ToDbName(),-12}  {e.ProcessName}");

            if (e.Type is EventType.Foreground or EventType.TitleChange or EventType.Other)
            {
                triggers.OnWindowChanged(signal.Timestamp);
            }
        }

        triggers.SetSuspended(signal.Timestamp, tracker.IsSuspended);

        if (signal is InputTick tick)
        {
            triggers.OnInputTick(tick.Timestamp, tick.IdleFor);
        }
    }

    /// <summary>Returns the next signal, or null once the trigger engine's next due time has passed.</summary>
    private async Task<MonitorSignal?> WaitForSignalOrDueTimeAsync(ChannelReader<MonitorSignal> signals, CancellationToken shutdown)
    {
        if (signals.TryRead(out MonitorSignal? ready))
        {
            return ready;
        }

        TimeSpan? wait = triggers.NextDue - time.GetUtcNow();
        if (wait <= TimeSpan.Zero)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        if (wait is { } delay)
        {
            timeout.CancelAfter(delay);
        }

        try
        {
            return await signals.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null; // due time reached, or shutting down (the caller's loop condition handles that)
        }
    }
}
