using System.Diagnostics;
using System.Threading.Channels;
using Capture.Core.Rules;
using Capture.Core.Monitoring;
using Capture.Core.Pipeline;
using Capture.Core.Triggers;
using Capture.Storage;
using Capture.Windows;
using Serilog;
using ActivityEvent = Capture.Core.Monitoring.ActivityEvent;

namespace Capture.App;

/// <summary>What the tray icon shows.</summary>
public enum CaptureState
{
    /// <summary>Active, and an capturable window is being captured.</summary>
    Capturing,

    /// <summary>No input for a while.</summary>
    Idle,

    /// <summary>Paused by the user.</summary>
    Paused,

    /// <summary>Nothing to capture: no capturable window is visible, or the session is locked or asleep.</summary>
    NotCapturing,
}

/// <param name="PausedUntil">End of a timed pause; null if not paused or paused until resumed.</param>
public sealed record CaptureStatus(CaptureState State, DateTimeOffset? PausedUntil);

/// <summary>
/// The single consumer of monitor signals: stores events, drives the trigger engine, captures on
/// its decisions and hands the result to the <see cref="CapturePipeline"/>. Runs off the monitor
/// thread, and leaves encoding to the pipeline thread, so event handling is never held up.
/// </summary>
public sealed class CaptureCoordinator(
    Channel<MonitorSignal> signals,
    ActivityTracker tracker,
    CaptureTriggerEngine triggers,
    CaptureDatabase database,
    ScreenCapturer capturer,
    CapturePipeline pipeline,
    PasswordFieldDetector? passwordFields,
    TimeProvider time)
{
    private bool _lastCaptureFoundNothing;
    private CaptureStatus _status = new(CaptureState.Capturing, null);

    /// <summary>Raised on the coordinator's thread whenever the status changes.</summary>
    public event Action<CaptureStatus>? StatusChanged;

    public CaptureStatus Status => _status;

    /// <summary>Pauses recording. Safe to call from any thread.</summary>
    /// <param name="duration">Null pauses until <see cref="Resume"/> is called.</param>
    public void Pause(TimeSpan? duration)
    {
        DateTimeOffset now = time.GetUtcNow();
        signals.Writer.TryWrite(new PauseRequested(now, duration is { } d ? now + d : null));
    }

    /// <summary>Ends a pause. Safe to call from any thread.</summary>
    public void Resume() => signals.Writer.TryWrite(new ResumeRequested(time.GetUtcNow()));

    public async Task RunAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            MonitorSignal? signal = await WaitForSignalOrDueTimeAsync(shutdown);
            if (signal is not null)
            {
                Handle(signal);
            }

            // A timed pause that has run out ends here.
            DateTimeOffset now = time.GetUtcNow();
            if (tracker.PausedUntil is { } until && now >= until)
            {
                Handle(new ResumeRequested(now));
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
                    CaptureReporter.Failed(decision, "Capture", ex);
                }
            }

            PublishStatus();
        }
    }

    private async Task CaptureAsync(CaptureDecision decision, CancellationToken shutdown)
    {
        // Take no capture while a password is being typed into a captured application. Other
        // applications are never captured anyway, so they are not queried.
        if (passwordFields is not null && tracker.CurrentWindow?.Mode == WindowMode.Capture)
        {
            FocusCheck focus = await passwordFields.CheckFocusedElementAsync();
            if (focus != FocusCheck.NotPassword)
            {
                string why = focus == FocusCheck.Password ? "password field has focus" : "could not check for a password field";
                CaptureReporter.Skipped(decision, why);
                return;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        CaptureResult result = await capturer.CaptureAsync(decision.Timestamp, shutdown);

        switch (result.Outcome)
        {
            case CaptureOutcome.NoCapturableWindow:
                if (!_lastCaptureFoundNothing)
                {
                    _lastCaptureFoundNothing = true;
                    database.InsertEvent(new ActivityEvent(decision.Timestamp, EventType.Other));
                }
                CaptureReporter.NothingToCapture(decision);
                break;

            case CaptureOutcome.Discarded:
                CaptureReporter.Discarded(decision, result.Reason);
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
            CaptureReporter.Event(e);

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

    private void PublishStatus()
    {
        CaptureState state =
            tracker.IsPaused ? CaptureState.Paused
            : tracker.IsLockedOrAsleep ? CaptureState.NotCapturing
            : tracker.IsIdle ? CaptureState.Idle
            : _lastCaptureFoundNothing ? CaptureState.NotCapturing
            : CaptureState.Capturing;

        DateTimeOffset? until = tracker.PausedUntil is { } u && u != DateTimeOffset.MaxValue ? u : null;
        var status = new CaptureStatus(state, until);
        if (status != _status)
        {
            _status = status;
            try
            {
                StatusChanged?.Invoke(status);
            }
            catch (Exception ex)
            {
                Log.Error("Status handler failed: {ErrorType}", ex.GetType().Name);
            }
        }
    }

    /// <summary>Returns the next signal, or null once the next due time (capture or end of pause) has passed.</summary>
    private async Task<MonitorSignal?> WaitForSignalOrDueTimeAsync(CancellationToken shutdown)
    {
        if (signals.Reader.TryRead(out MonitorSignal? ready))
        {
            return ready;
        }

        DateTimeOffset? due = triggers.NextDue;
        if (tracker.PausedUntil is { } pauseEnd && pauseEnd != DateTimeOffset.MaxValue && (due is null || pauseEnd < due))
        {
            due = pauseEnd;
        }

        TimeSpan? wait = due - time.GetUtcNow();
        if (wait <= TimeSpan.Zero)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        if (wait is { } delay)
        {
            // CancelAfter cannot take more than about 49 days.
            timeout.CancelAfter(delay < TimeSpan.FromDays(1) ? delay : TimeSpan.FromDays(1));
        }

        try
        {
            return await signals.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null; // due time reached, or shutting down (the caller's loop condition handles that)
        }
    }
}
