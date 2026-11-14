// TimeCapture: a tray application without a main window. It takes no arguments; everything is
// set in config.json. Started from a terminal, it also prints what it does there.

using Capture.Core.Config;

namespace Capture.App;

internal static class Program
{
    [STAThread]
    private static int Main()
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

        return CaptureHost.Run(paths, config);
    }
}
