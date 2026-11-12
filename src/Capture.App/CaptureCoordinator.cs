using System.Threading.Channels;
using Capture.Core.Monitoring;
using Capture.Core.Triggers;
using Capture.Storage;
using Serilog;

namespace Capture.App;

/// <summary>
/// The single consumer of monitor signals: stores events, drives the trigger engine and acts on
/// its decisions. Runs off the monitor thread so slow work never blocks event handling.
/// </summary>
public sealed class CaptureCoordinator(
    ActivityTracker tracker,
    CaptureTriggerEngine triggers,
    CaptureDatabase database,
    TimeProvider time)
{
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
                // M2: only record the decision. M3/M4 capture here.
                database.InsertCaptureDecision(decision);
                Log.Information("Would capture ({Trigger})", decision.Trigger.ToDbName());
                Console.WriteLine($"{decision.Timestamp.ToLocalTime():HH:mm:ss}  >> capture   {decision.Trigger.ToDbName()}");
            }
        }
    }

    private void Handle(MonitorSignal signal)
    {
        foreach (ActivityEvent e in tracker.Process(signal))
        {
            database.InsertEvent(e);
            Log.Information("Event {Type}", e.Type.ToDbName());
            Console.WriteLine($"{e.Timestamp.ToLocalTime():HH:mm:ss}  {e.Type.ToDbName(),-12}  {e.ProcessName}");

            if (e.Type is EventType.Foreground or EventType.TitleChange or EventType.NonWork)
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
