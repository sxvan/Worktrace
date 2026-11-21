using System.Threading.Channels;
using System.Windows.Forms;
using Capture.Core.Config;
using Capture.Core.Monitoring;
using Capture.Core.Pipeline;
using Capture.Core.Redaction;
using Capture.Core.Rules;
using Capture.Core.Triggers;
using Capture.Storage;
using Capture.Windows;
using Serilog;
using Timer = System.Threading.Timer;

namespace Capture.App;

/// <summary>The normal mode of the app: builds all parts, runs the tray icon and takes everything down again.</summary>
internal static class CaptureHost
{
    public static int Run(AppPaths paths, CaptureConfig config)
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\TimeCapture.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            return AppConsole.Fail(1, "TimeCapture is already running. Look for its icon in the notification area.");
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
            return RunTray(paths, config);
        }
        catch (Exception ex)
        {
            // Exception messages can in theory contain data, so only type and code go to the log.
            Log.Fatal("Fatal error: {ErrorType} 0x{HResult:X8}", ex.GetType().Name, ex.HResult);
            return AppConsole.Fail(3, $"TimeCapture stopped with an error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int RunTray(AppPaths paths, CaptureConfig config)
    {
        Log.Information("Starting. Storage root: {Root}. Rules: {RuleCount}", paths.Root, config.Rules.Count);
        if (config.Rules.Count == 0)
        {
            Log.Warning("config.json has no rules; nothing will be recorded");
            Console.WriteLine($"Note: no rules in {paths.ConfigFile}; nothing will be recorded until you add some.");
        }

        IRedactor? redactor = CreateRedactor(config);
        if (redactor is null)
        {
            // Without text recognition nothing can be checked, so nothing may be saved.
            Log.Fatal("No OCR language installed; refusing to capture");
            return AppConsole.Fail(4, "Secret redaction is on, but Windows has no OCR language installed. "
                                      + "Install one (Settings > Time & language > Language) or set redactSecrets to false.");
        }

        if (config.StoreText && !config.RedactSecrets)
        {
            Log.Warning("storeText needs redactSecrets; no text will be stored");
            Console.WriteLine("Note: storeText needs redactSecrets; no recognized text will be stored.");
        }

        using var database = new CaptureDatabase(paths.DatabaseFile, new TextScrubber([new HeuristicSecretDetector()], config.StripUrlQueries));
        var tracker = new ActivityTracker(TimeSpan.FromSeconds(config.IdleTimeoutSeconds));
        var rules = new WindowRuleMatcher(config.Rules);

        // The monitor thread only enqueues; everything else happens on the coordinator.
        var signals = Channel.CreateUnbounded<MonitorSignal>(new UnboundedChannelOptions { SingleReader = true });

        using var monitor = new WindowActivityMonitor(
            rules,
            TimeSpan.FromMilliseconds(config.InputPollIntervalMs),
            signal => signals.Writer.TryWrite(signal),
            error => Log.Error("Monitor error: {ErrorType} 0x{HResult:X8}", error.GetType().Name, error.HResult));
        monitor.Start();

        var capturer = new ScreenCapturer(rules);
        Task.Run(capturer.InitializeAsync).GetAwaiter().GetResult();
        if (!capturer.IsBorderless)
        {
            Log.Warning("Borderless capture was denied; captured windows will show a border on screen");
            Console.WriteLine("Note: borderless capture was denied; captured windows will show a border.");
        }

        using Timer retentionTimer = StartRetention(paths, database, config.RetentionDays, config.TextRetentionDays);

        using var pipeline = new CapturePipeline(
            redactor,
            new FileImageStore(paths.Root, config.ImageQuality, Math.Clamp(config.ImageEncoderSpeed, 0, 9), config.MaxImageDimension),
            database,
            config.DedupeThreshold,
            config.StoreImages,
            config.StoreText && config.RedactSecrets);

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

    /// <summary>The redactor the config asks for, or null if redaction is on but cannot work on this machine.</summary>
    private static IRedactor? CreateRedactor(CaptureConfig config)
    {
        if (!config.RedactSecrets)
        {
            Log.Warning("Secret redaction is switched off in the config");
            Console.WriteLine("Secret redaction: OFF (redactSecrets is false)");
            return new PassThroughRedactor();
        }

        WindowsOcrRecognizer? recognizer = WindowsOcrRecognizer.TryCreate();
        if (recognizer is null)
        {
            return null;
        }

        Console.WriteLine($"Secret redaction: on (text recognition language {recognizer.LanguageTag})");
        return new TextRedactor(recognizer, [new HeuristicSecretDetector()], config.StripUrlQueries);
    }

    /// <summary>Retention runs on startup and once per day.</summary>
    private static Timer StartRetention(AppPaths paths, CaptureDatabase database, int retentionDays, int textRetentionDays)
    {
        var retention = new RetentionCleaner(paths.Root, database, retentionDays, textRetentionDays);
        return new Timer(_ =>
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
    }
}
