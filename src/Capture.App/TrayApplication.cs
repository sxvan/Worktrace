using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Capture.Core.Config;
using Capture.Core.Rules;
using Capture.Storage;
using Serilog;

namespace Capture.App;

/// <summary>
/// The tray icon and its menu. There is no main window. Runs on the UI thread; status updates
/// arrive from the coordinator's thread and are marshalled over.
/// </summary>
public sealed class TrayApplication : ApplicationContext
{
    private readonly CaptureCoordinator _coordinator;
    private readonly CapturePipeline _pipeline;
    private readonly CaptureConfig _config;
    private readonly AppPaths _paths;
    private readonly FileImageStore _images;
    private readonly NotifyIcon _notifyIcon;
    private readonly Control _marshal = new();
    private readonly Dictionary<CaptureState, Icon> _icons;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _resumeItem;
    private readonly ToolStripMenuItem _autostartItem;

    public TrayApplication(
        CaptureCoordinator coordinator, CapturePipeline pipeline, CaptureConfig config, AppPaths paths, FileImageStore images)
    {
        _coordinator = coordinator;
        _pipeline = pipeline;
        _config = config;
        _paths = paths;
        _images = images;
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
        menu.Items.Add($"Delete last {DeleteRecentMinutes} minutes…", null, (_, _) => DeleteRecent());
        // The screenshots are encrypted, so the folder itself is no use to look at: export a day instead.
        menu.Items.Add("Export day…", null, (_, _) => ExportDay());
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
            _ => "Not capturing (no capturable window visible)",
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

    private int DeleteRecentMinutes => Math.Max(1, _config.DeleteRecentMinutes);

    /// <summary>After asking, deletes what deleteRecentMode names from the last few minutes.</summary>
    private void DeleteRecent()
    {
        int minutes = DeleteRecentMinutes;
        if (WindowRuleMatcher.ParseMode(_config.DeleteRecentMode) is not RuleLevel level)
        {
            MessageBox.Show($"deleteRecentMode \"{_config.DeleteRecentMode}\" in the config is not one of capture, logWithTitles or log.",
                "TimeCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string what = level switch
        {
            RuleLevel.Capture => "screenshots",
            RuleLevel.LogWithTitles => "screenshots and window titles",
            _ => "screenshots and all activity",
        };
        if (MessageBox.Show($"Delete the {what} of the last {minutes} minutes? This cannot be undone.",
                "TimeCapture", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            return;
        }

        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            int files = _pipeline.DeleteRecent(now.AddMinutes(-minutes), now, level);
            Log.Information("Deleted the last {Minutes} minutes ({Level}): {Files} image file(s)", minutes, level, files);
        }
        catch (Exception ex)
        {
            Log.Error("Deleting recent captures failed: {ErrorType} 0x{HResult:X8}", ex.GetType().Name, ex.HResult);
            MessageBox.Show("Deleting failed. See the log for details.", "TimeCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Asks for a day and a folder, then writes that day's screenshots there, decrypted.</summary>
    private void ExportDay()
    {
        try
        {
            IReadOnlyList<DateOnly> days = _images.Days();
            if (days.Count == 0)
            {
                MessageBox.Show("There are no stored screenshots yet.", "TimeCapture", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var picker = new ExportDayDialog(days);
            if (picker.ShowDialog() != DialogResult.OK || picker.SelectedDay is not DateOnly day)
            {
                return;
            }

            using var folderDialog = new FolderBrowserDialog
            {
                Description = $"Folder for the screenshots of {day:yyyy-MM-dd}. They are written there unencrypted.",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
            };
            if (folderDialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            string target = Path.Combine(folderDialog.SelectedPath, $"TimeCapture {day:yyyy-MM-dd}");
            (int exported, int failed) = _images.ExportDay(day, target);
            Log.Information("Exported a day: {Exported} images, {Failed} failed", exported, failed);
            if (failed > 0)
            {
                MessageBox.Show($"{failed} screenshot(s) could not be decrypted (key missing or file changed).",
                    "TimeCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            if (exported > 0)
            {
                Open(target);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Export failed: {ErrorType} 0x{HResult:X8}", ex.GetType().Name, ex.HResult);
            MessageBox.Show("The export failed. See the log for details.", "TimeCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
