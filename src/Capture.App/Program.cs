// TimeCapture: a tray application without a main window.
//
//   TimeCapture                    run in the tray
//   TimeCapture --dump [n]         print the last n stored events (default 50), including window titles
//   TimeCapture --redaction-test   check secret redaction on this machine with made-up secrets
//
// Started from a terminal, it also prints what it does there.

using Capture.App.Commands;
using Capture.Core.Config;

namespace Capture.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppConsole.AttachToParent();

        AppPaths paths;
        CaptureConfig config;
        try
        {
            (paths, config) = AppPaths.Load();
        }
        catch (Exception ex)
        {
            return AppConsole.Fail(2, $"Could not load config.json: {ex.Message}");
        }

        return args.FirstOrDefault() switch
        {
            "--dump" => DumpCommand.Run(paths, args),
            "--redaction-test" => RedactionTestCommand.Run(args),
            _ => CaptureHost.Run(paths, config),
        };
    }
}
