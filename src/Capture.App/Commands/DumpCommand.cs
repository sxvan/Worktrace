using Capture.Core.Monitoring;
using Capture.Storage;

namespace Capture.App.Commands;

/// <summary>--dump [n]: prints the last n stored events and captures, including window titles.</summary>
internal static class DumpCommand
{
    private const int DefaultCount = 50;

    public static int Run(AppPaths paths, string[] args)
    {
        int count = args.Length > 1 && int.TryParse(args[1], out int n) ? n : DefaultCount;

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
}
