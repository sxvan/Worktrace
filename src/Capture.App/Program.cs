// TimeCapture host. Console app until the tray icon arrives in M5.
//
//   TimeCapture              run the monitor until Ctrl+C
//   TimeCapture --dump [n]   print the last n stored events (default 50), including window titles

using System.Threading.Channels;
using Capture.App;
using Capture.Core.Allowlist;
using Capture.Core.Config;
using Capture.Core.Monitoring;
using Capture.Core.Pipeline;
using Capture.Core.Triggers;
using Capture.Storage;
using Capture.Windows;
using Serilog;

AppPaths paths;
CaptureConfig config;
try
{
    (paths, config) = AppPaths.Load();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not load config.json: {ex.Message}");
    return 2;
}

if (args.Length > 0 && args[0] == "--dump")
{
    int count = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 50;
    using var readDb = new CaptureDatabase(paths.DatabaseFile);
    var lines = readDb.ReadLastEvents(count)
        .Select(e => (e.Timestamp, Text: $"{e.Type.ToDbName(),-12}  {e.ProcessName,-20}  {e.WindowTitle}"))
        .Concat(readDb.ReadLastCaptureDecisions(count).Select(d => (d.Timestamp, Text: $">> capture    {d.Trigger}")))
        .OrderBy(line => line.Timestamp)
        .TakeLast(count);
    foreach (var (timestamp, text) in lines)
    {
        Console.WriteLine($"{timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {text}");
    }
    return 0;
}

using var mutex = new Mutex(initiallyOwned: true, @"Local\TimeCapture.SingleInstance", out bool isFirstInstance);
if (!isFirstInstance)
{
    Console.Error.WriteLine("TimeCapture is already running.");
    return 1;
}

// Logs never contain window titles or screen content: only event types and technical errors.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(paths.LogsFolder, "timecapture-.log"),
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 5 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 10)
    .CreateLogger();

try
{
    Log.Information("Starting. Storage root: {Root}. Allowlist rules: {RuleCount}", paths.Root, config.Allowlist.Count);

    using var database = new CaptureDatabase(paths.DatabaseFile);
    var tracker = new ActivityTracker(TimeSpan.FromSeconds(config.IdleTimeoutSeconds));
    var allowlist = new AllowlistMatcher(config.Allowlist);

    // The monitor thread only enqueues; database writes happen on the consumer below.
    var signals = Channel.CreateUnbounded<MonitorSignal>(new UnboundedChannelOptions { SingleReader = true });

    using var monitor = new WindowActivityMonitor(
        allowlist,
        TimeSpan.FromMilliseconds(config.InputPollIntervalMs),
        signal => signals.Writer.TryWrite(signal),
        error => Log.Error("Monitor error: {ErrorType} 0x{HResult:X8}", error.GetType().Name, error.HResult));

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        shutdown.Cancel();
    };

    monitor.Start();
    Console.WriteLine($"Monitoring. Database: {paths.DatabaseFile}");
    Console.WriteLine("Press Ctrl+C to stop.");

    var capturer = new ScreenCapturer(allowlist);
    await capturer.InitializeAsync();
    if (!capturer.IsBorderless)
    {
        Log.Warning("Borderless capture was denied; captured windows will show a border on screen");
        Console.WriteLine("Note: borderless capture was denied; captured windows will show a border.");
    }

    var coordinator = new CaptureCoordinator(
        tracker,
        new CaptureTriggerEngine(TriggerSettings.FromConfig(config)),
        database,
        capturer,
        new PassThroughRedactor(),
        new FileImageStore(paths.Root, config.ImageQuality),
        TimeProvider.System);
    Console.WriteLine($"Captures: {paths.CapturesFolder}");
    await coordinator.RunAsync(signals.Reader, shutdown.Token);

    Log.Information("Stopping");
    return 0;
}
catch (Exception ex)
{
    // Exception messages can in theory contain data, so only type and code go to the log.
    Log.Fatal("Fatal error: {ErrorType} 0x{HResult:X8}", ex.GetType().Name, ex.HResult);
    Console.Error.WriteLine($"Fatal error: {ex.GetType().Name}: {ex.Message}");
    return 3;
}
finally
{
    Log.CloseAndFlush();
}
