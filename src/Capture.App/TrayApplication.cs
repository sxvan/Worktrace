using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Serilog;

namespace Capture.App;

/// <summary>
/// The tray icon and its menu. There is no main window. Runs on the UI thread; status updates
/// arrive from the coordinator's thread and are marshalled over.
/// </summary>
public sealed class TrayApplication : ApplicationContext
{
    private readonly CaptureCoordinator _coordinator;
    private readonly AppPaths _paths;
    private readonly NotifyIcon _notifyIcon;
    private readonly Control _marshal = new();
    private readonly Dictionary<CaptureState, Icon> _icons;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _resumeItem;
    private readonly ToolStripMenuItem _autostartItem;

    public TrayApplication(CaptureCoordinator coordinator, AppPaths paths)
    {
        _coordinator = coordinator;
        _paths = paths;
        _marshal.CreateControl(); // gives the UI thread a handle to marshal onto

        _icons = new Dictionary<CaptureState, Icon>
        {
            [CaptureState.Capturing] = DrawIcon(Color.FromArgb(220, 50, 47), filled: true, pauseBars: false),
            [CaptureState.Idle] = DrawIcon(Color.FromArgb(140, 140, 140), filled: true, pauseBars: false),
            [CaptureState.Paused] = DrawIcon(Color.FromArgb(230, 150, 30), filled: true, pauseBars: true),
            [CaptureState.NotCapturing] = DrawIcon(Color.FromArgb(140, 140, 140), filled: false, pauseBars: false),
        };

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _resumeItem = new ToolStripMenuItem("Resume", null, (_, _) => _coordinator.Resume());
        _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pause for 15 minutes", null, (_, _) => _coordinator.Pause(TimeSpan.FromMinutes(15)));
        menu.Items.Add("Pause for 1 hour", null, (_, _) => _coordinator.Pause(TimeSpan.FromHours(1)));
        menu.Items.Add("Pause until resumed", null, (_, _) => _coordinator.Pause(null));
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open captures folder", null, (_, _) => Open(EnsureFolder(_paths.CapturesFolder)));
        menu.Items.Add("Open config file", null, (_, _) => Open(_paths.ConfigFile));
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => _autostartItem.Checked = SafeAutostartEnabled();

        _notifyIcon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        Show(_coordinator.Status);

        _coordinator.StatusChanged += OnStatusChanged;
    }

    private void OnStatusChanged(CaptureStatus status)
    {
        // Called on the coordinator's thread.
        try
        {
            if (_marshal.IsHandleCreated && !_marshal.IsDisposed)
            {
                _marshal.BeginInvoke(() => Show(status));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Shutting down.
        }
    }

    private void Show(CaptureStatus status)
    {
        string text = status.State switch
        {
            CaptureState.Capturing => "Capturing",
            CaptureState.Idle => "Idle (no input)",
            CaptureState.Paused => status.PausedUntil is { } until
                ? $"Paused until {until.ToLocalTime():HH:mm}"
                : "Paused until resumed",
            _ => "Not capturing (no allowed window visible)",
        };

        _notifyIcon.Icon = _icons[status.State];
        _notifyIcon.Text = $"TimeCapture: {text}"; // tooltip, limited to 127 characters
        _statusItem.Text = text;
        _resumeItem.Enabled = status.State == CaptureState.Paused;
    }

    private void ToggleAutostart()
    {
        try
        {
            Autostart.Set(!Autostart.IsEnabled);
        }
        catch (Exception ex)
        {
            Log.Error("Could not change autostart: {ErrorType} 0x{HResult:X8}", ex.GetType().Name, ex.HResult);
            MessageBox.Show("Could not change the autostart setting.", "TimeCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool SafeAutostartEnabled()
    {
        try
        {
            return Autostart.IsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string EnsureFolder(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Could not open a path from the tray menu: {ErrorType} 0x{HResult:X8}", ex.GetType().Name, ex.HResult);
        }
    }

    protected override void ExitThreadCore()
    {
        _coordinator.StatusChanged -= OnStatusChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _marshal.Dispose();
        foreach (Icon icon in _icons.Values)
        {
            DestroyIcon(icon.Handle);
            icon.Dispose();
        }
        base.ExitThreadCore();
    }

    /// <summary>A round status dot: filled or hollow, optionally with pause bars.</summary>
    private static Icon DrawIcon(Color color, bool filled, bool pauseBars)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            if (filled)
            {
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, 3, 3, 26, 26);
            }
            else
            {
                using var pen = new Pen(color, 4);
                g.DrawEllipse(pen, 5, 5, 22, 22);
            }
            if (pauseBars)
            {
                g.FillRectangle(Brushes.White, 11, 10, 4, 12);
                g.FillRectangle(Brushes.White, 17, 10, 4, 12);
            }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
