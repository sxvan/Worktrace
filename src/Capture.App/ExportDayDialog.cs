using System.Globalization;
using System.Windows.Forms;

namespace Capture.App;

/// <summary>A small dialog to pick one of the days that have stored screenshots.</summary>
internal sealed class ExportDayDialog : Form
{
    private readonly ListBox _days = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    public ExportDayDialog(IReadOnlyList<DateOnly> days)
    {
        Text = "Export day";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(320, 300);
        TopMost = true;

        foreach (DateOnly day in days)
        {
            _days.Items.Add(day.ToString("yyyy-MM-dd (dddd)", CultureInfo.CurrentCulture));
        }
        _days.SelectedIndex = 0;
        _days.DoubleClick += (_, _) => Accept(days);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 48,
            Padding = new Padding(8),
            Text = "The screenshots of the selected day are decrypted into a folder you choose. Delete it when you are done.",
        };

        var ok = new Button { Text = "Export…", DialogResult = DialogResult.None, Width = 90 };
        ok.Click += (_, _) => Accept(days);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(4) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var list = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
        list.Controls.Add(_days);

        Controls.Add(list);
        Controls.Add(hint);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        void Accept(IReadOnlyList<DateOnly> all)
        {
            if (_days.SelectedIndex >= 0)
            {
                SelectedDay = all[_days.SelectedIndex];
                DialogResult = DialogResult.OK;
            }
        }
    }

    public DateOnly? SelectedDay { get; private set; }
}
