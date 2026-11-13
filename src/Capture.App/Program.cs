// TimeCapture host. Console app until the tray icon arrives in M5.
//
//   TimeCapture              run the monitor until Ctrl+C
//   TimeCapture --dump [n]   print the last n stored events (default 50), including window titles
//   TimeCapture --redaction-test   check secret redaction on this machine with made-up secrets

using System.Threading.Channels;
using Capture.App;
using Capture.Core.Allowlist;
using Capture.Core.Config;
using Capture.Core.Monitoring;
using Capture.Core.Pipeline;
using Capture.Core.Redaction;
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
        .Concat(readDb.ReadLastCaptures(count).Select(c =>
            (c.Timestamp, Text: $">> capture    {c.Trigger,-13} {c.Windows} window(s), {c.SavedImages} saved")))
        .OrderBy(line => line.Timestamp)
        .TakeLast(count);
    foreach (var (timestamp, text) in lines)
    {
        Console.WriteLine($"{timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {text}");
    }
    return 0;
}

if (args.Length > 1 && args[0] == "--detect-lines")
{
    // Evaluation helper: runs the secret detector on each line of a text file and prints 1 or 0 per line.
    var lineDetector = new HeuristicSecretDetector();
    foreach (string textLine in File.ReadLines(args[1]))
    {
        var words = new List<RecognizedWord>();
        int column = 0;
        foreach (string word in textLine.Split(' '))
        {
            if (word.Length > 0)
            {
                words.Add(new RecognizedWord(word, new Capture.Core.Geometry.RectInt(column * 10, 0, (column + word.Length) * 10, 20)));
            }
            column += word.Length + 1;
        }
        Console.WriteLine(lineDetector.FindSecrets([new RecognizedLine(words)]).Count > 0 ? "1" : "0");
    }
    return 0;
}

if (args.Length > 0 && args[0] == "--redaction-test")
{
    double testScale = args.Length > 1 && double.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : WindowsOcrRecognizer.DefaultScale;
    bool verbose = args.Contains("--verbose");
    WindowsOcrRecognizer? testRecognizer = WindowsOcrRecognizer.TryCreate(testScale);
    if (testRecognizer is null)
    {
        Console.Error.WriteLine("Windows has no OCR language installed.");
        return 4;
    }
    RedactionSelfTest.Report report = await RedactionSelfTest.RunAsync(testRecognizer, [new HeuristicSecretDetector()]);
    foreach (var group in report.Cases.GroupBy(c => (c.Theme, c.FontSize)))
    {
        Console.WriteLine($"{group.Key.Theme} theme, {group.Key.FontSize} pt: {group.Count(c => c.Found)} of {group.Count()} fake secrets found");
        foreach (RedactionSelfTest.Case missed in group.Where(c => !c.Found && verbose))
        {
            Console.WriteLine($"  MISSED  {missed.Line}");
            Console.WriteLine($"  read as {missed.RecognizedAs}");
        }
    }
    Console.WriteLine($"False positives on harmless lines: {report.FalsePositives}");
    Console.WriteLine($"Text recognition and detection: {report.MillisecondsPerImage} ms per {report.Width}x{report.Height} image ({testRecognizer.LanguageTag}, enlarged {testScale}x)");
    return report.Cases.All(c => c.Found) ? 0 : 5;
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
    Log.Information("Starting. Storage root: {Root}. Allowlist rules: {RuleCount}", paths.Root, config.Rules.Count);

    using var database = new CaptureDatabase(paths.DatabaseFile);
    var tracker = new ActivityTracker(TimeSpan.FromSeconds(config.IdleTimeoutSeconds));
    var allowlist = new AllowlistMatcher(config.Rules);

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

    // Retention: on startup and once per day.
    var retention = new RetentionCleaner(paths.Root, database, config.RetentionDays);
    using var retentionTimer = new Timer(_ =>
    {
        try
        {
            int removed = retention.Run(DateOnly.FromDateTime(DateTime.Now));
            if (removed > 0)
            {
                Log.Information("Retention removed {Folders} day folder(s)", removed);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Retention failed: {ErrorType} 0x{HResult:X8}", ex.GetType().Name, ex.HResult);
        }
    }, null, TimeSpan.Zero, TimeSpan.FromDays(1));

    IRedactor redactor;
    if (config.RedactSecrets)
    {
        WindowsOcrRecognizer? recognizer = WindowsOcrRecognizer.TryCreate();
        if (recognizer is null)
        {
            // Without text recognition nothing can be checked, so nothing may be saved.
            Console.Error.WriteLine("Secret redaction is on, but Windows has no OCR language installed. Install one (Settings > Time & language > Language) or set redactSecrets to false.");
            Log.Fatal("No OCR language installed; refusing to capture");
            return 4;
        }
        redactor = new TextRedactor(recognizer, [new HeuristicSecretDetector()]);
        Console.WriteLine($"Secret redaction: on (text recognition language {recognizer.LanguageTag})");
    }
    else
    {
        redactor = new PassThroughRedactor();
        Log.Warning("Secret redaction is switched off in the config");
        Console.WriteLine("Secret redaction: OFF (redactSecrets is false)");
    }

    using var pipeline = new CapturePipeline(
        redactor,
        new FileImageStore(paths.Root, config.ImageQuality, Math.Clamp(config.ImageEncoderSpeed, 0, 9), config.MaxImageDimension),
        database,
        config.DedupeThreshold);

    var coordinator = new CaptureCoordinator(
        tracker,
        new CaptureTriggerEngine(TriggerSettings.FromConfig(config)),
        database,
        capturer,
        pipeline,
        config.SkipPasswordFields ? new PasswordFieldDetector() : null,
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
