// TimeCapture: a tray application without a main window.
//
//   TimeCapture                    run in the tray
//   TimeCapture --dump [n]         print the last n stored events (default 50), including window titles
//   TimeCapture --redaction-test   check secret redaction on this machine with made-up secrets
//
// Started from a terminal, it also prints what it does there.

using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Forms;
using Capture.Core.Allowlist;
using Capture.Core.Config;
using Capture.Core.Monitoring;
using Capture.Core.Pipeline;
using Capture.Core.Redaction;
using Capture.Core.Triggers;
using Capture.Storage;
using Capture.Windows;
using Serilog;
using Timer = System.Threading.Timer;

namespace Capture.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A windowless application has no console of its own; borrow the terminal's if there is one.
        AttachConsole(AttachParentProcess);

        AppPaths paths;
        CaptureConfig config;
        try
        {
            (paths, config) = AppPaths.Load();
        }
        catch (Exception ex)
        {
            return Fail(2, $"Could not load config.json: {ex.Message}");
        }

        if (args.Length > 0 && args[0] == "--dump")
        {
            return Dump(paths, args.Length > 1 && int.TryParse(args[1], out int n) ? n : 50);
        }
        if (args.Length > 0 && args[0] == "--redaction-test")
        {
            return RedactionTest(args);
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\TimeCapture.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            return Fail(1, "TimeCapture is already running. Look for its icon in the notification area.");
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
            return Run(paths, config);
        }
        catch (Exception ex)
        {
            // Exception messages can in theory contain data, so only type and code go to the log.
            Log.Fatal("Fatal error: {ErrorType} 0x{HResult:X8}", ex.GetType().Name, ex.HResult);
            return Fail(3, $"TimeCapture stopped with an error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int Run(AppPaths paths, CaptureConfig config)
    {
        Log.Information("Starting. Storage root: {Root}. Rules: {RuleCount}", paths.Root, config.Rules.Count);

        IRedactor redactor;
        if (config.RedactSecrets)
        {
            WindowsOcrRecognizer? recognizer = WindowsOcrRecognizer.TryCreate();
            if (recognizer is null)
            {
                // Without text recognition nothing can be checked, so nothing may be saved.
                Log.Fatal("No OCR language installed; refusing to capture");
                return Fail(4, "Secret redaction is on, but Windows has no OCR language installed. "
                               + "Install one (Settings > Time & language > Language) or set redactSecrets to false.");
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

        using var database = new CaptureDatabase(paths.DatabaseFile);
        var tracker = new ActivityTracker(TimeSpan.FromSeconds(config.IdleTimeoutSeconds));
        var allowlist = new AllowlistMatcher(config.Rules);

        // The monitor thread only enqueues; everything else happens on the coordinator.
        var signals = Channel.CreateUnbounded<MonitorSignal>(new UnboundedChannelOptions { SingleReader = true });

        using var monitor = new WindowActivityMonitor(
            allowlist,
            TimeSpan.FromMilliseconds(config.InputPollIntervalMs),
            signal => signals.Writer.TryWrite(signal),
            error => Log.Error("Monitor error: {ErrorType} 0x{HResult:X8}", error.GetType().Name, error.HResult));
        monitor.Start();

        var capturer = new ScreenCapturer(allowlist);
        Task.Run(capturer.InitializeAsync).GetAwaiter().GetResult();
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

        using var pipeline = new CapturePipeline(
            redactor,
            new FileImageStore(paths.Root, config.ImageQuality, Math.Clamp(config.ImageEncoderSpeed, 0, 9), config.MaxImageDimension),
            database,
            config.DedupeThreshold);

        var coordinator = new CaptureCoordinator(
            signals,
            tracker,
            new CaptureTriggerEngine(TriggerSettings.FromConfig(config)),
            database,
            capturer,
            pipeline,
            config.SkipPasswordFields ? new PasswordFieldDetector() : null,
            TimeProvider.System);

        using var shutdown = new CancellationTokenSource();
        Console.WriteLine($"Running in the notification area. Captures: {paths.CapturesFolder}");
        Task coordinatorTask = Task.Run(() => coordinator.RunAsync(shutdown.Token));

        // If the coordinator dies, the app must not sit in the tray pretending to work.
        coordinatorTask.ContinueWith(task =>
        {
            Log.Fatal("Coordinator stopped: {ErrorType}", task.Exception?.InnerException?.GetType().Name);
            Application.Exit();
        }, TaskContinuationOptions.OnlyOnFaulted);

        Application.EnableVisualStyles();
        using (var tray = new TrayApplication(coordinator, paths))
        {
            Application.Run(tray);
        }

        shutdown.Cancel();
        try
        {
            coordinatorTask.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // Already logged above.
        }

        Log.Information("Stopping");
        return 0;
    }

    private static int Dump(AppPaths paths, int count)
    {
        using var database = new CaptureDatabase(paths.DatabaseFile);
        var lines = database.ReadLastEvents(count)
            .Select(e => (e.Timestamp, Text: $"{e.Type.ToDbName(),-12}  {e.ProcessName,-20}  {e.WindowTitle}"))
            .Concat(database.ReadLastCaptures(count).Select(c =>
                (c.Timestamp, Text: $">> capture    {c.Trigger,-13} {c.Windows} window(s), {c.SavedImages} saved")))
            .OrderBy(line => line.Timestamp)
            .TakeLast(count);
        foreach (var (timestamp, text) in lines)
        {
            Console.WriteLine($"{timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {text}");
        }
        return 0;
    }

    private static int RedactionTest(string[] args)
    {
        double scale = args.Length > 1 && double.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out double s)
            ? s
            : WindowsOcrRecognizer.DefaultScale;
        bool verbose = args.Contains("--verbose");

        WindowsOcrRecognizer? recognizer = WindowsOcrRecognizer.TryCreate(scale);
        if (recognizer is null)
        {
            return Fail(4, "Windows has no OCR language installed.");
        }

        RedactionSelfTest.Report report = Task
            .Run(() => RedactionSelfTest.RunAsync(recognizer, [new HeuristicSecretDetector()]))
            .GetAwaiter().GetResult();

        foreach (var group in report.Cases.GroupBy(c => (c.Theme, c.FontSize)))
        {
            string bySide = string.Join(", ", group.GroupBy(c => c.Side)
                .Select(side => $"{side.Key} side {side.Count(c => c.Covered)}/{side.Count(c => c.Found)}"));
            Console.WriteLine($"{group.Key.Theme} theme, {group.Key.FontSize} pt: {group.Count(c => c.Found)} of {group.Count()} fake secrets found; box covers the secret: {bySide}");
            foreach (RedactionSelfTest.Case problem in group.Where(c => !c.Covered && verbose))
            {
                Console.WriteLine($"  {(problem.Found ? "MISPLACED" : "MISSED   ")} ({problem.Side}) {problem.Line}");
                Console.WriteLine($"  read as   {problem.RecognizedAs}");
            }
        }
        Console.WriteLine($"False positives on harmless lines: {report.FalsePositives}");
        Console.WriteLine($"Text recognition and detection: {report.MillisecondsPerImage} ms per {report.Width}x{report.Height} image ({recognizer.LanguageTag}, enlarged {scale}x)");
        return report.Cases.All(c => c.Covered) ? 0 : 5;
    }

    /// <summary>Reports a startup problem: on the terminal if there is one, otherwise in a message box.</summary>
    private static int Fail(int exitCode, string message)
    {
        if (GetConsoleWindow() != 0)
        {
            Console.Error.WriteLine(message);
        }
        else
        {
            MessageBox.Show(message, "TimeCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return exitCode;
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();
}
