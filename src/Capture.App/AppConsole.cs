using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Capture.App;

/// <summary>Terminal handling for an application that has no console of its own.</summary>
internal static class AppConsole
{
    private const int AttachParentProcess = -1;

    /// <summary>Borrows the terminal the app was started from, if there is one.</summary>
    public static void AttachToParent() => AttachConsole(AttachParentProcess);

    /// <summary>Reports a startup problem: on the terminal if there is one, otherwise in a message box.</summary>
    public static int Fail(int exitCode, string message)
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

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();
}
