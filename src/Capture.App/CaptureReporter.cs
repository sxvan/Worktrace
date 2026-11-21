using Capture.Core.Monitoring;
using Capture.Core.Triggers;
using Serilog;
using ActivityEvent = Capture.Core.Monitoring.ActivityEvent;

namespace Capture.App;

/// <summary>
/// The one place that reports what the app does: a line in the log file and, when started from a
/// terminal, a line there.
///
/// The log never contains window titles, process names or screen content, only event types,
/// counts, timings and technical errors. Exception messages can in theory contain data, so only
/// the type and the error code are logged. The terminal additionally shows process names.
/// </summary>
internal static class CaptureReporter
{
    public static void Event(ActivityEvent e)
    {
        Log.Information("Event {Type}", e.Type.ToDbName());
        Console.WriteLine($"{e.Timestamp.ToLocalTime():HH:mm:ss}  {e.Type.ToDbName(),-12}  {e.ProcessName}");
    }

    public static void Skipped(CaptureDecision decision, string reason)
    {
        Log.Information("Capture ({Trigger}) skipped: {Reason}", decision.Trigger.ToDbName(), reason);
        Console.WriteLine($"{Prefix(decision)} -> skipped ({reason})");
    }

    public static void NothingToCapture(CaptureDecision decision)
    {
        Log.Information("Capture ({Trigger}): no capturable window visible", decision.Trigger.ToDbName());
        Console.WriteLine($"{Prefix(decision)} -> nothing to capture (no capturable window visible)");
    }

    public static void Discarded(CaptureDecision decision, string? reason)
    {
        Log.Warning("Capture ({Trigger}) discarded: {Reason}", decision.Trigger.ToDbName(), reason);
        Console.WriteLine($"{Prefix(decision)} -> discarded ({reason})");
    }

    /// <param name="stage">What failed, for the log: "Capture" or "Capture processing".</param>
    public static void Failed(CaptureDecision decision, string stage, Exception ex)
    {
        Log.Error("{Stage} failed: {ErrorType} 0x{HResult:X8}", stage, ex.GetType().Name, ex.HResult);
        Console.WriteLine($"{Prefix(decision)} -> failed ({ex.GetType().Name})");
    }

    public static void DroppedBecauseQueueIsFull(CaptureDecision decision)
    {
        Log.Warning("Capture dropped: encoding queue is full");
        Console.WriteLine($"{Prefix(decision)} -> dropped (encoding is behind)");
    }

    public static void Stored(CaptureDecision decision, int windows, int saved, long captureMs, long processMs)
    {
        int unchanged = windows - saved;
        Log.Information("Capture ({Trigger}): {Windows} window(s), {Saved} saved, {Duplicates} unchanged, capture {CaptureMs} ms, processing {ProcessMs} ms",
            decision.Trigger.ToDbName(), windows, saved, unchanged, captureMs, processMs);
        Console.WriteLine($"{Prefix(decision)} -> {windows} window(s): {saved} saved, {unchanged} unchanged (capture {captureMs} ms, process {processMs} ms)");
    }

    private static string Prefix(CaptureDecision decision) =>
        $"{decision.Timestamp.ToLocalTime():HH:mm:ss}  >> capture   {decision.Trigger.ToDbName(),-13}";
}
