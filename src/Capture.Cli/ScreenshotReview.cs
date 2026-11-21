using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Capture.Core.Redaction;
using Capture.Storage;

namespace Capture.Cli;

/// <summary>
/// Shows the user exactly what would be shared (the redacted image and the redacted text) and
/// asks whether to share it. The answer is yes only after a person clicked "Share": the button
/// ignores the keyboard, UI Automation, messages sent to it and injected mouse input (see
/// <see cref="PhysicalClickGuard"/>), and stays disabled for a moment so a click meant for another
/// window cannot land on it. Closing the window, Escape, "Don't share", or any error means no.
/// </summary>
internal sealed class ScreenshotReview : Form
{
    /// <summary>"Share" stays disabled this long after the window appears.</summary>
    private static readonly TimeSpan ArmDelay = TimeSpan.FromSeconds(1.5);

    private readonly PhysicalClickGuard _guard;
    private readonly PhysicalButton _share;
    private bool _shared;

    private ScreenshotReview(Image image, StoredScreenshot screenshot, RedactedScreenText text, int hiddenCount, PhysicalClickGuard guard)
    {
        _guard = guard;

        Text = "TimeCapture: share this screenshot with Claude?";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        TopMost = true;
        ShowInTaskbar = true;
        KeyPreview = true;
        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Size = new Size(area.Width * 4 / 5, area.Height * 4 / 5);
        MinimumSize = new Size(640, 400);

        var header = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 76,
            Padding = new Padding(10, 8, 10, 4),
            Font = new Font(Font.FontFamily, 10f),
            Text =
                $"Claude asks to see the screenshot of {screenshot.ProcessName} \"{screenshot.WindowTitle}\", "
                + $"{screenshot.Timestamp.ToLocalTime():dddd yyyy-MM-dd HH:mm:ss}.\n"
                + $"Black boxes: {hiddenCount} part(s) hidden by the redaction rules. If you share, Claude receives exactly this "
                + "image and the text on the right. Check both for anything that must not leave this machine.",
        };

        var picture = new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill };
        var imagePanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        imagePanel.Controls.Add(picture);
        var actualSize = new CheckBox { Text = "Actual size", Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(6, 0, 0, 0) };
        actualSize.CheckedChanged += (_, _) =>
        {
            picture.Dock = actualSize.Checked ? DockStyle.None : DockStyle.Fill;
            picture.SizeMode = actualSize.Checked ? PictureBoxSizeMode.AutoSize : PictureBoxSizeMode.Zoom;
        };

        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            Text = TextListing(text),
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        split.Panel1.Controls.Add(imagePanel);
        split.Panel1.Controls.Add(actualSize);
        split.Panel2.Controls.Add(textBox);

        var deny = new Button { Text = "Don't share", AutoSize = true, Padding = new Padding(12, 4, 12, 4), DialogResult = DialogResult.Cancel };
        _share = new PhysicalButton(guard)
        {
            Text = "Share with Claude (click with the mouse)",
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 4),
            Enabled = false,
            TabStop = false,
        };
        _share.PhysicalClick += (_, _) =>
        {
            _shared = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
        };
        buttons.Controls.Add(deny);
        buttons.Controls.Add(_share);

        Controls.Add(split);
        Controls.Add(header);
        Controls.Add(buttons);
        CancelButton = deny;
        // No AcceptButton: Enter must not share.

        Shown += (_, _) =>
        {
            split.SplitterDistance = Math.Max(200, split.Width * 2 / 3);
            deny.Focus();
            Activate();
            var arm = new System.Windows.Forms.Timer { Interval = (int)ArmDelay.TotalMilliseconds };
            arm.Tick += (_, _) =>
            {
                arm.Dispose();
                _share.Enabled = true;
            };
            arm.Start();
        };
    }

    /// <summary>
    /// Shows the review and returns true only if the user clicked "Share" with the mouse. Runs a
    /// modal message loop on the calling thread, which must be an STA thread.
    /// </summary>
    public static bool Ask(byte[] redactedPng, StoredScreenshot screenshot, RedactedScreenText text)
    {
        PhysicalClickGuard guard;
        try
        {
            guard = new PhysicalClickGuard();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // Without the check, nothing can be approved.
        }

        using (guard)
        using (var stream = new MemoryStream(redactedPng))
        using (var image = Image.FromStream(stream))
        using (var form = new ScreenshotReview(image, screenshot, text, text.HiddenRegions.Count, guard))
        {
            // Modal, so "Don't share", Escape and the close box end the dialog. Anything but a
            // physical click on "Share" leaves _shared false.
            form.ShowDialog();
            return form._shared;
        }
    }

    /// <summary>The text as the model will get it, one line per recognized line, with its box.</summary>
    internal static string TextListing(RedactedScreenText text)
    {
        if (text.Lines.Count == 0)
        {
            return "(no text recognized)";
        }
        var builder = new StringBuilder();
        foreach (RedactedLine line in text.Lines)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"{line.Bounds.Left,5},{line.Bounds.Top,-5} {line.Bounds.Width,4}x{line.Bounds.Height,-3} {line.Text}\r\n");
        }
        return builder.ToString();
    }

    /// <summary>A button that reacts only to a real left click: never to Space, Enter, PerformClick or accessibility actions.</summary>
    private sealed class PhysicalButton(PhysicalClickGuard guard) : Button
    {
        public event EventHandler? PhysicalClick;

        // Keyboard, PerformClick and UI Automation's Invoke all end up here: ignore them.
        protected override void OnClick(EventArgs e)
        {
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            base.OnMouseUp(mevent);
            if (Enabled && mevent.Button == MouseButtons.Left && ClientRectangle.Contains(mevent.Location)
                && guard.WasPhysicalClick(RectangleToScreen(ClientRectangle)))
            {
                PhysicalClick?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
