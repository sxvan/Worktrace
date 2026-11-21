// timecapture-cli: hands what TimeCapture recorded, and the day's Claude Code sessions, to a
// model (Claude Code).
//
//   timecapture-cli export [--day yyyy-MM-dd] [--root DIR]
//   timecapture-cli sessions [--day yyyy-MM-dd] [--transcripts DIR]
//   timecapture-cli screenshot <id> | --at "yyyy-MM-dd HH:mm[:ss]" [--out DIR] [--root DIR]
//
// export prints what TimeCapture recorded for the day: visits with (scrubbed) window titles and
// the ids of the screenshots taken during each. It contains no screen content.
//
// sessions summarizes the day's Claude Code transcripts (active times, folder, branch, prompts)
// and names each transcript file, for reading one session in full when the summary is not enough.
//
// screenshot decrypts one screenshot in memory, recognizes its text with PaddleOCR, applies the
// redaction rules to text and image, and shows the result to the user. Only when the user clicks
// "Share" does anything of it leave this program: the redacted image is written to a file and
// the redacted text with its positions is printed. There is no option to skip the review.
//
// Read-only: it never changes the database or the captures.

using System.Drawing.Imaging;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows.Forms;
using Capture.Core.Config;
using Capture.Core.Export;
using Capture.Core.Redaction;
using Capture.Storage;

namespace Capture.Cli;

internal static class Program
{
    private const string Usage = """
        Usage:
          timecapture-cli export [--day yyyy-MM-dd] [--root DIR]
          timecapture-cli sessions [--day yyyy-MM-dd] [--transcripts DIR]
          timecapture-cli screenshot <id> | --at "yyyy-MM-dd HH:mm[:ss]" [--out DIR] [--root DIR]

        export      What TimeCapture recorded for the day: visits (window titles, scrubbed) and
                    screenshot ids per visit. No screen content. Default: today.
        sessions    The day's Claude Code sessions: active times, folder, branch, prompts typed,
                    and the transcript file of each. Default: today.
        screenshot  Recognizes the text of one screenshot, redacts text and image, and asks the
                    user in a window whether to share them. Only if the user agrees, writes the
                    redacted image (default folder %TEMP%\timecapture-shared) and prints its path
                    and the redacted text with positions. Exit code 3: the user declined.
                    --at picks the focused window captured closest to that time (within 5 minutes).

        The storage root comes from --root, else TIMECAPTURE_ROOT, else config.json next to this
        program, else %LOCALAPPDATA%\TimeCapture. Claude Code transcripts are read from
        %USERPROFILE%\.claude\projects unless --transcripts names another folder.
        """;

    private const int ExitDeclined = 3;

    /// <summary>Visits stop this long after the last record when TimeCapture is not running.</summary>
    private static readonly TimeSpan DataEndGrace = TimeSpan.FromMinutes(2);

    /// <summary>How far from the requested time --at may pick a screenshot.</summary>
    private static readonly TimeSpan AtTolerance = TimeSpan.FromMinutes(5);

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }

            var options = Options.Parse(args.Skip(1));
            return args[0] switch
            {
                "export" => Export(options),
                "sessions" => Sessions(options),
                "screenshot" => Screenshot(options),
                _ => Fail($"Unknown command \"{args[0]}\".\n\n{Usage}"),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail($"{ex.Message}\n\n{Usage}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Export(Options options)
    {
        if (DayRange(options) is not var (day, from, to))
        {
            return Fail("That day lies in the future.");
        }
        DateTimeOffset now = DateTimeOffset.Now;

        using var reader = new CaptureReader(DatabasePath(options));

        // While TimeCapture runs, the last state lasts until now. Otherwise it ends shortly after
        // the last record, instead of running on through the time the app was not recording.
        DateTimeOffset? dataEnd = null;
        if (!(IsTimeCaptureRunning() && to >= now.AddMinutes(-1)))
        {
            dataEnd = reader.LastRecordBefore(to) is { } last ? last + DataEndGrace : from;
        }

        IReadOnlyList<Visit> visits = VisitBuilder.DropShortSwitches(
            VisitBuilder.Build(reader.ReadEvents(from, to), from, to, dataEnd), DayExport.MinimumSwitch);
        IReadOnlyList<ScreenshotRef> screenshots = reader.ReadFocusedScreenshots(from, to);

        Console.Write(DayExport.ToText(day, visits, screenshots, TimeZoneInfo.Local));
        return 0;
    }

    private static int Sessions(Options options)
    {
        if (DayRange(options) is not var (day, from, to))
        {
            return Fail("That day lies in the future.");
        }
        Console.Write(SessionsExport.ToText(day, ReadSessions(TranscriptFolder(options), from, to), TimeZoneInfo.Local));
        return 0;
    }

    /// <summary>The day of --day (default today) as a local time range, cut off at now; null for a day in the future.</summary>
    private static (DateOnly Day, DateTimeOffset From, DateTimeOffset To)? DayRange(Options options)
    {
        DateOnly day = options.Day();
        DateTimeOffset from = Local(day.ToDateTime(TimeOnly.MinValue));
        DateTimeOffset to = Local(day.AddDays(1).ToDateTime(TimeOnly.MinValue));
        DateTimeOffset now = DateTimeOffset.Now;
        if (from > now)
        {
            return null;
        }
        return (day, from, to > now ? now : to);
    }

    private static int Screenshot(Options options)
    {
        string root = Root(options);
        using var reader = new CaptureReader(Path.Combine(root, "capture.db"));

        StoredScreenshot? screenshot;
        if (options.Get("at") is { } at)
        {
            if (options.Positional.Count > 0)
            {
                throw new ArgumentException("Give either an id or --at, not both.");
            }
            DateTimeOffset time = Options.ParseLocal(at);
            screenshot = reader.FindScreenshotNear(time, AtTolerance);
            if (screenshot is null)
            {
                return Fail($"No screenshot of a focused window within {AtTolerance.TotalMinutes:0} minutes of {at}.");
            }
        }
        else
        {
            if (options.Positional.Count != 1
                || !long.TryParse(options.Positional[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            {
                throw new ArgumentException("screenshot needs exactly one id, or --at.");
            }
            screenshot = reader.FindScreenshot(id);
            if (screenshot is null)
            {
                return Fail($"Window {id} has no screenshot (unknown id, images not stored, or removed by retention).");
            }
        }

        // From here until the user's answer, the screen content exists only in this process.
        byte[] redactedPng;
        RedactedScreenText text;
        byte[] original = reader.ReadUnredactedImage(root, screenshot, new DpapiKeyProtector());
        try
        {
            Console.Error.WriteLine("Recognizing text…");
            IReadOnlyList<OcrLine> ocr;
            using (var recognizer = new PaddleTextRecognizer())
            {
                ocr = recognizer.Recognize(original);
            }
            text = new ScreenTextRedactor(Scrubber()).Redact(ocr);
            redactedPng = BlackOut(original, text.HiddenRegions);
        }
        finally
        {
            Array.Clear(original);
        }

        Console.Error.WriteLine("Waiting for the user to review the screenshot…");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        if (!ScreenshotReview.Ask(redactedPng, screenshot, text))
        {
            Console.WriteLine(
                $"The user did not share screenshot {screenshot.WindowId}. Do not ask for this one again. "
                + "You may ask for a screenshot from a different moment if it would answer the question, or ask the user.");
            return ExitDeclined;
        }

        string folder = Path.GetFullPath(options.Get("out") ?? Path.Combine(Path.GetTempPath(), "timecapture-shared"));
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"screenshot-{screenshot.WindowId}.png"));
        File.WriteAllBytes(target, redactedPng);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Screenshot {screenshot.WindowId}, {screenshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}, {screenshot.ProcessName} \"{screenshot.WindowTitle}\"{(screenshot.IsFocused ? "" : " (not focused)")}"));
        Console.Write(ReuseNote(screenshot));
        Console.WriteLine("Shared by the user after review. Redacted parts are black in the image and [redacted] in the text.");
        Console.WriteLine($"Image: {target}");
        Console.WriteLine("Text (left,top widthxheight in image pixels, then the line):");
        Console.Write(ScreenshotReview.TextListing(text).Replace("\r\n", "\n"));
        return 0;
    }

    /// <summary>
    /// For an image reused from an earlier capture of the window (the window counted as unchanged),
    /// a line that says when it was taken and under which title; empty otherwise.
    /// </summary>
    internal static string ReuseNote(StoredScreenshot screenshot)
    {
        if (!screenshot.IsReused)
        {
            return "";
        }
        string taken = screenshot.ImageTimestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return screenshot.ImageWindowTitle == screenshot.WindowTitle
            ? $"Note: the window counted as unchanged, so this image is from an earlier capture at {taken}.\n"
            : $"Note: the window counted as unchanged, so this image is from an earlier capture at {taken}, "
              + $"when the title was \"{screenshot.ImageWindowTitle}\".\n";
    }

    /// <summary>The image with the regions painted black, as PNG.</summary>
    private static byte[] BlackOut(byte[] png, IReadOnlyList<Capture.Core.Geometry.RectInt> regions)
    {
        using var input = new MemoryStream(png);
        using var loaded = new Bitmap(input);
        // A copy that does not depend on the stream, in a format GDI+ can draw on.
        using var bitmap = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImage(loaded, 0, 0, loaded.Width, loaded.Height);
            foreach (Capture.Core.Geometry.RectInt region in regions)
            {
                graphics.FillRectangle(Brushes.Black, region.Left, region.Top, region.Width, region.Height);
            }
        }
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    /// <summary>The Claude Code sessions with entries in the range, from all projects' transcripts.</summary>
    private static List<AgentSession> ReadSessions(string folder, DateTimeOffset from, DateTimeOffset to)
    {
        var sessions = new List<AgentSession>();
        if (!Directory.Exists(folder))
        {
            return sessions;
        }

        string temp = Path.GetFullPath(Path.GetTempPath());
        foreach (string project in Directory.EnumerateDirectories(folder))
        {
            foreach (string file in Directory.EnumerateFiles(project, "*.jsonl"))
            {
                // A file last written before the range has no entries in it.
                if (File.GetLastWriteTimeUtc(file) < from.UtcDateTime)
                {
                    continue;
                }
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var lines = new StreamReader(stream);
                    if (TranscriptReader.Read(ReadLines(lines), from, to) is { } session
                        // Helper sessions some tools start in the temp folder (titles and the like) are not work.
                        && !(session.WorkingDirectory is { } cwd && Path.GetFullPath(cwd).StartsWith(temp, StringComparison.OrdinalIgnoreCase)))
                    {
                        sessions.Add(session with { TranscriptPath = Path.GetFullPath(file) });
                    }
                }
                catch (IOException)
                {
                    // A transcript that cannot be read is left out.
                }
            }
        }
        return sessions;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>Scrubs the way TimeCapture does, following stripUrlQueries in config.json.</summary>
    private static TextScrubber Scrubber() =>
        Config() is { StripUrlQueries: true } ? new TextScrubber([new HeuristicSecretDetector()], stripUrlQueries: true) : TextScrubber.Default;

    private static CaptureConfig? Config()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config.json");
        return File.Exists(path) ? ConfigLoader.Load(path) : null;
    }

    private static string DatabasePath(Options options) => Path.Combine(Root(options), "capture.db");

    private static string Root(Options options)
    {
        if (options.Get("root") is { } root)
        {
            return Environment.ExpandEnvironmentVariables(root);
        }
        if (Environment.GetEnvironmentVariable("TIMECAPTURE_ROOT") is { Length: > 0 } fromEnvironment)
        {
            return Environment.ExpandEnvironmentVariables(fromEnvironment);
        }
        return Config() is { } config ? ConfigLoader.ResolveRoot(config) : ConfigLoader.DefaultRoot;
    }

    private static string TranscriptFolder(Options options) =>
        options.Get("transcripts") is { } folder
            ? Environment.ExpandEnvironmentVariables(folder)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private static bool IsTimeCaptureRunning()
    {
        if (Mutex.TryOpenExisting(@"Local\TimeCapture.SingleInstance", out Mutex? mutex))
        {
            mutex.Dispose();
            return true;
        }
        return false;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static DateTimeOffset Local(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(value));

    private sealed class Options
    {
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Positional { get; } = [];

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            using IEnumerator<string> e = args.GetEnumerator();
            while (e.MoveNext())
            {
                if (e.Current.StartsWith("--", StringComparison.Ordinal))
                {
                    string name = e.Current[2..];
                    if (!e.MoveNext())
                    {
                        throw new ArgumentException($"--{name} needs a value.");
                    }
                    options._named[name] = e.Current;
                }
                else
                {
                    options.Positional.Add(e.Current);
                }
            }
            return options;
        }

        public string? Get(string name) => _named.GetValueOrDefault(name);

        public DateOnly Day() =>
            Get("day") is not { } day ? DateOnly.FromDateTime(DateTime.Now)
            : DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed) ? parsed
            : throw new ArgumentException("--day must look like 2026-11-20.");

        public static DateTimeOffset ParseLocal(string value) =>
            DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
                ? Local(parsed)
                : throw new ArgumentException($"\"{value}\" is not a time like 2026-11-20 13:30.");
    }
}
