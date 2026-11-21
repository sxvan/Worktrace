// timecapture-cli: reads what TimeCapture recorded, for building a timesheet. Read-only: it never
// changes the database or the captures.
//
//   timecapture-cli export [--day yyyy-MM-dd | --from "yyyy-MM-dd HH:mm" --to "yyyy-MM-dd HH:mm"]
//                          [--layer 1|2] [--format text|json] [--max-lines N] [--root DIR]
//   timecapture-cli image <id> [--out DIR] [--root DIR]
//
// export prints titles (layer 1) and recognized text (layer 2), both with secrets replaced.
// image writes one screenshot as PNG (layer 3) and prints its path. Keep the two apart in tool
// permissions: export can be allowed, image should ask every time.

using System.Globalization;
using Capture.Core.Config;
using Capture.Core.Export;
using Capture.Core.Redaction;
using Capture.Storage;

namespace Capture.Cli;

internal static class Program
{
    private const string Usage = """
        Usage:
          timecapture-cli export [--day yyyy-MM-dd | --from "yyyy-MM-dd HH:mm" --to "yyyy-MM-dd HH:mm"]
                                 [--layer 1|2] [--format text|json] [--max-lines N] [--root DIR]
          timecapture-cli image <id> [--out DIR] [--root DIR]

        export  Visits with window titles (layer 1) and recognized text (layer 2), secrets replaced.
                Default: today, layer 2, text format. Times are local.
        image   Writes the screenshot of a captured window (an id from "images:" in the export) as
                PNG and prints its path. Default folder: %TEMP%\timecapture-images.

        The storage root comes from --root, else TIMECAPTURE_ROOT, else config.json next to this
        program, else %LOCALAPPDATA%\TimeCapture.
        """;

    /// <summary>Visits stop this long after the last record when TimeCapture is not running.</summary>
    private static readonly TimeSpan DataEndGrace = TimeSpan.FromMinutes(2);

    private static int Main(string[] args)
    {
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
                "image" => Image(options),
                _ => Fail($"Unknown command \"{args[0]}\".\n\n{Usage}"),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail($"{ex.Message}\n\n{Usage}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Export(Options options)
    {
        (DateTimeOffset from, DateTimeOffset to) = options.Range();
        DateTimeOffset now = DateTimeOffset.Now;
        if (to > now)
        {
            to = now;
        }
        if (to <= from)
        {
            return Fail("The range lies in the future.");
        }

        ExportLayer layer = options.Get("layer") switch
        {
            null or "2" => ExportLayer.Text,
            "1" => ExportLayer.Titles,
            var other => throw new ArgumentException($"--layer must be 1 or 2, not \"{other}\". Images are fetched one by one with the image command."),
        };
        var exportOptions = new ExportOptions(layer, MaxLinesPerVisit: options.GetInt("max-lines", 12));

        using var reader = new CaptureReader(DatabasePath(options));

        // While TimeCapture runs, the last state lasts until now. Otherwise it ends shortly after
        // the last record, instead of running on through the time the app was not recording.
        DateTimeOffset? dataEnd = null;
        if (!(IsTimeCaptureRunning() && to >= now.AddMinutes(-1)))
        {
            dataEnd = reader.LastRecordBefore(to) is { } last ? last + DataEndGrace : from;
        }

        IReadOnlyList<Visit> visits = VisitBuilder.Build(reader.ReadEvents(from, to), from, to, dataEnd);
        IReadOnlyList<TextSample> text = layer >= ExportLayer.Text ? reader.ReadFocusedText(from, to) : [];
        IReadOnlyList<ExportVisit> export = ExportFormatter.Assemble(
            visits, text, reader.ReadFocusedImages(from, to), exportOptions, Scrubber());

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Write(options.Get("format") switch
        {
            null or "text" => ExportFormatter.ToText(export, from, to, exportOptions, TimeZoneInfo.Local),
            "json" => ExportFormatter.ToJson(export, from, to, exportOptions, TimeZoneInfo.Local) + Environment.NewLine,
            var other => throw new ArgumentException($"--format must be text or json, not \"{other}\"."),
        });
        return 0;
    }

    private static int Image(Options options)
    {
        if (options.Positional.Count != 1 || !long.TryParse(options.Positional[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id))
        {
            throw new ArgumentException("image needs exactly one id.");
        }

        string root = Root(options);
        using var reader = new CaptureReader(Path.Combine(root, "capture.db"));
        if (reader.FindImage(id) is not { } image)
        {
            return Fail($"Window {id} has no image (unknown id, images not stored, or removed by retention).");
        }

        // Only files inside the captures folder are ever read.
        string source = Path.GetFullPath(Path.Combine(root, image.RelativePath));
        string captures = Path.GetFullPath(Path.Combine(root, FileImageStore.CapturesFolderName)) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(captures, StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
        {
            return Fail($"The image file of window {id} is missing.");
        }

        string folder = options.Get("out") ?? Path.Combine(Path.GetTempPath(), "timecapture-images");
        string target = Path.GetFullPath(Path.Combine(folder, $"window-{id}.png"));
        FileImageStore.ConvertToPng(source, target);

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine(target);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{image.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss} {image.ProcessName} \"{Scrubber().Scrub(image.WindowTitle)}\""));
        Console.WriteLine("Secrets the text recognition found are blacked out; anything it missed is still visible. Delete the file when done.");
        return 0;
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

        public int GetInt(string name, int fallback) =>
            Get(name) is not { } value ? fallback
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) ? result
            : throw new ArgumentException($"--{name} must be a number.");

        /// <summary>--day, or --from and --to, in local time. Default: today.</summary>
        public (DateTimeOffset From, DateTimeOffset To) Range()
        {
            string? day = Get("day"), from = Get("from"), to = Get("to");
            if (day is not null && (from is not null || to is not null))
            {
                throw new ArgumentException("Use either --day or --from/--to.");
            }

            if (from is not null || to is not null)
            {
                DateTimeOffset start = ParseLocal(from ?? throw new ArgumentException("--to needs --from."));
                DateTimeOffset end = to is null ? DateTimeOffset.Now : ParseLocal(to);
                return (start, end);
            }

            DateOnly date = day is null
                ? DateOnly.FromDateTime(DateTime.Now)
                : DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed)
                    ? parsed
                    : throw new ArgumentException("--day must look like 2026-11-20.");
            return (Local(date.ToDateTime(TimeOnly.MinValue)), Local(date.AddDays(1).ToDateTime(TimeOnly.MinValue)));
        }

        private static DateTimeOffset ParseLocal(string value) =>
            DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
                ? Local(parsed)
                : throw new ArgumentException($"\"{value}\" is not a time like 2026-11-20 13:30.");

        private static DateTimeOffset Local(DateTime value) =>
            new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(value));
    }
}
