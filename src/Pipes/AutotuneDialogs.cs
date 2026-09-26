namespace Pipes;

/// <summary>
/// The small window shown while "Test this PC" runs: what's being tested, a moving bar, and Cancel. Shown modeless
/// (<c>Show</c>, not <c>ShowDialog</c>) so the settings dialog's <c>await</c> keeps running; the settings dialog
/// disables itself meanwhile, which makes this behave like a modal dialog. Built in code, like the settings dialog.
/// </summary>
internal sealed class AutotuneProgressForm : Form
{
    private readonly Label _message;

    public AutotuneProgressForm(string message, Action cancel)
    {
        Text = "Test this PC";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false; // no close box: Cancel is the way out, and it stops the test properly
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; // centred on the settings dialog in OnLoad
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _message = new Label
        {
            Text = message + "\n" +
                "Each quality preset is drawn off screen for a moment and timed. This takes about 10-20 seconds.",
            AutoSize = true,
            Margin = new Padding(3, 3, 3, 9),
        };
        // A marquee bar just moves: the test doesn't report progress as it goes, and doesn't need to.
        var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Dock = DockStyle.Fill };
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, Anchor = AnchorStyles.Right };
        cancelButton.Click += (_, _) =>
        {
            cancelButton.Enabled = false;
            cancel();
        };
        CancelButton = cancelButton; // Esc

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(_message);
        layout.Controls.Add(bar);
        layout.Controls.Add(cancelButton);
        Controls.Add(layout);
    }

    protected override void OnLoad(EventArgs e)
    {
        // Wrap the text at a width that suits the display's scaling, known only now (see ConfigForm.OnLoad).
        _message.MaximumSize = new Size(LogicalToDeviceUnits(380), 0);
        base.OnLoad(e);
        // CenterParent only works for ShowDialog, so centre on the owner by hand.
        if (Owner is { } owner)
            Location = new Point(owner.Left + (owner.Width - Width) / 2, owner.Top + (owner.Height - Height) / 2);
    }
}

/// <summary>
/// "Test this PC" results: each preset's frame time against the monitor's frame budget, which one is recommended,
/// and which graphics chip it ran on. "Use ..." returns <see cref="DialogResult.OK"/>; the settings dialog then
/// picks the recommended preset (in its controls only: nothing is saved until OK there).
/// </summary>
internal sealed class AutotuneResultsForm : Form
{
    private readonly Font _bold;
    private readonly List<Label> _wrapped = [];

    public AutotuneResultsForm(AutotuneReport report)
    {
        Text = "Test this PC: results";
        if (Environment.ProcessPath is { } exePath) Icon = Icon.ExtractAssociatedIcon(exePath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        _bold = new Font(Font, FontStyle.Bold);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };

        // Three outcomes, and they read very differently: comfortably fast, fast enough but without much spare, and
        // genuinely unable to hold the refresh. Only the last is bad news, and only the last says so.
        var best = report.Results.Count == 0 ? AutotuneVerdict.TooSlow : report.Results.Min(r => r.Verdict);
        layout.Controls.Add(Paragraph(best switch
        {
            AutotuneVerdict.Comfortable =>
                $"Recommended: {report.Recommended}, the highest quality that keeps up with your screen here with room to spare.",
            AutotuneVerdict.Tight =>
                $"Recommended: {report.Recommended}. It keeps up with your screen here, but without much to spare, " +
                "so a busy moment may stutter. Drop a level if you see one.",
            _ =>
                "Even Lite couldn't hold your screen's frame rate here, but it's the lightest there is, so it's still " +
                "the best choice. If other programs were busy, close them and test again: the frame times include " +
                "the processor's work too.",
        }, bold: true));
        // "144 Hz: 6.9 ms per frame, aiming for at most 5.2 ms". The rest is headroom; see Autotune.TargetShareOfBudget.
        layout.Controls.Add(Paragraph(
            $"{report.RefreshHz} Hz: {report.BudgetMs:F1} ms per frame, aiming for at most {report.TargetMs:F1} ms " +
            "to leave room for busier moments."));
        layout.Controls.Add(ResultsTable(report));
        var screens = report.ViewCount == 1 ? "" : $" ({report.ViewCount} scenes, one per monitor)";
        layout.Controls.Add(Paragraph($"Tested on: {report.Renderer}, {report.Width}×{report.Height}{screens}."));
        if (Autotune.LooksIntegratedOrSoftware(report.Renderer))
        {
            layout.Controls.Add(Paragraph(
                "This may have run on the integrated graphics chip, or Windows' software renderer, rather than a " +
                "graphics card. On a laptop with NVIDIA Optimus or AMD switchable graphics, set Pipes to \"High " +
                "performance\" in Windows Settings > System > Display > Graphics (or in the NVIDIA Control Panel), " +
                $"then test again. The file to add is:\n{Environment.ProcessPath}"));
        }

        var use = new Button { Text = $"Use {report.Recommended}", DialogResult = DialogResult.OK, AutoSize = true };
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = use;
        CancelButton = close;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([close, use]);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
    }

    /// <summary>
    /// One row per preset: the frame time nine frames in ten stayed under, that as frames per second, and whether it
    /// fits. Presets after the first that didn't fit weren't tried (they're heavier), and say so.
    /// </summary>
    private TableLayoutPanel ResultsTable(AutotuneReport report)
    {
        var table = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Margin = new Padding(3, 6, 3, 9) };
        void Cell(string text, int column, bool bold = false, bool right = false)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = right ? AnchorStyles.Right : AnchorStyles.Left,
                Margin = new Padding(3, 2, column < 3 ? 18 : 3, 2),
            };
            if (bold) label.Font = _bold;
            table.Controls.Add(label);
        }

        Cell("Preset", 0, bold: true);
        Cell("Frame time*", 1, bold: true, right: true);
        Cell("fps", 2, bold: true, right: true);
        Cell("", 3, bold: true);
        foreach (var preset in Enum.GetValues<QualityPreset>())
        {
            var bold = preset == report.Recommended;
            if (report.Results.Find(r => r.Preset == preset) is { } r)
            {
                Cell(preset.ToString(), 0, bold);
                Cell($"{r.P90Ms:F1} ms", 1, bold, right: true);
                Cell($"{1000 / r.P90Ms:F0}", 2, bold, right: true);
                Cell(r.Verdict switch
                {
                    AutotuneVerdict.Comfortable => "✓",
                    AutotuneVerdict.Tight => "✓ little to spare",
                    _ => "too slow",
                }, 3, bold);
            }
            else
            {
                Cell(preset.ToString(), 0);
                Cell("–", 1, right: true);
                Cell("–", 2, right: true);
                Cell("not tried", 3);
            }
        }

        var wrapper = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = Padding.Empty };
        wrapper.Controls.Add(table);
        wrapper.Controls.Add(Paragraph("* Nine frames in ten took this long or less."));
        return wrapper;
    }

    private Label Paragraph(string text, bool bold = false)
    {
        var label = new Label { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
        if (bold) label.Font = _bold;
        _wrapped.Add(label);
        return label;
    }

    protected override void OnLoad(EventArgs e)
    {
        // Wrap at a width that suits the display's scaling, known only now (see ConfigForm.OnLoad).
        foreach (var label in _wrapped) label.MaximumSize = new Size(LogicalToDeviceUnits(420), 0);
        base.OnLoad(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bold.Dispose();
        base.Dispose(disposing);
    }
}
