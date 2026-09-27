using System.Diagnostics;

namespace Pipes;

/// <summary>The "Settings..." dialog from Screen Saver Settings. Built in code, no designer.</summary>
internal sealed class ConfigForm : Form
{
    private readonly PipesSettings _settings;

    private readonly NumericUpDown _concurrent = new() { Minimum = 1, Maximum = PipesSettings.MaxConcurrentPipes, Width = 70 };
    private readonly NumericUpDown _perScene = new() { Minimum = 1, Maximum = PipesSettings.MaxPipesPerScene, Width = 70 };
    private readonly TrackBar _speed = Slider(1, PipesSettings.MaxSpeed);
    private readonly Label _speedValue = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox _camera = Dropdown("Still", "Slow orbit", "Floating drift", "Fly through the pipes");
    private readonly TrackBar _flightSpeed = Slider(1, PipesSettings.MaxFlightSpeed);
    private readonly Label _flightSpeedValue = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TrackBar _complexity = Slider(1, PipesSettings.MaxCourseComplexity);
    private readonly Label _complexityValue = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TrackBar _density = Slider(1, PipesSettings.MaxTunnelDensity);
    private readonly Label _densityValue = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly CheckBox _varySpeed = Check("Pilot varies the speed");
    private readonly CheckBox _separateMonitors = Check("Own scene on each monitor");
    private readonly CheckBox _mainOnly = Check("Main monitor only (the others go black)");

    private readonly ComboBox _joints = Dropdown("Classic (ball joints)", "Smooth elbows", "Mixed");
    private readonly ComboBox _finish = Dropdown("Plastic (glossy and satin)", "Metallic", "Weathered (rust, patina, worn paint)", "Mixed (per pipe)");
    private readonly CheckBox _thickness = Check("Vary pipe thickness");
    private readonly CheckBox _fittings = Check("Valves, couplings, flanges and junctions");
    private readonly CheckBox _teapots = Check("Rare teapots (like the original)");

    // Quality presets (see QualityPresets): the dropdown's items are "Custom" then the presets in enum order, so a
    // preset's index is (int)preset + 1.
    private readonly ComboBox _quality = Dropdown("Custom", "Lite", "Low", "Medium", "High", "Ultra");
    private readonly Button _testPc = new() { Text = "Test this PC…", AutoSize = true };
    // Same items as _quality minus "Custom", so this one's index is (int)preset + 1 too, with 0 meaning "leave it".
    private readonly ComboBox _batteryQuality = Dropdown("Don't change", "Lite", "Low", "Medium", "High", "Ultra");
    private readonly ComboBox _style = Dropdown("Modern", "Classic (lite, like the original)");
    private readonly ComboBox _aa = Dropdown("Off", "2x", "4x", "8x");
    private readonly CheckBox _surfaces = Check("Surface detail (grain, scratches, rust; subtle on plain paint)");
    private readonly CheckBox _shadows = Check("Shadows (pipes shade the pipes behind them)");
    private readonly CheckBox _movingLight = Check("Moving light (shadows sweep slowly)");
    private readonly CheckBox _traced = Check("Traced reflections (metal pipes mirror nearby pipes)");
    private readonly CheckBox _ao = Check("Ambient occlusion (soft contact shadows)");
    private readonly CheckBox _bloom = Check("Bloom (glow on highlights)");
    private readonly CheckBox _dof = Check("Depth of field (blur near and far pipes; off in flight)");
    private readonly CheckBox _stats = Check("Show performance stats (FPS, frame graph, GPU load)");

    private readonly GroupBox _flightGroup;

    /// <summary>
    /// Set while the dialog itself is changing controls for the quality preset (applying one, or showing which one
    /// the controls match), so those changes don't count as the user's: they don't re-derive the preset halfway
    /// through, and switching the style to Classic doesn't also switch the pipes to the classic ones.
    /// </summary>
    private bool _settingQuality;

    // Each column's groups and row labels, so OnLoad can line them up (see AlignColumns).
    private readonly List<GroupBox> _leftGroups = [], _rightGroups = [];
    private readonly List<Label> _leftLabels = [], _rightLabels = [];

    public ConfigForm(PipesSettings settings)
    {
        _settings = settings;
        Text = "Pipes Settings";
        // Reuses the icon the build already embedded in the exe (<ApplicationIcon> in the csproj) instead of
        // embedding a second copy as a resource. ProcessPath is only null in exotic hosting scenarios that don't
        // apply to a WinExe like this one; the fallback just leaves the default WinForms icon.
        if (Environment.ProcessPath is { } exePath) Icon = Icon.ExtractAssociatedIcon(exePath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _speed.ValueChanged += (_, _) => _speedValue.Text = $"{_speed.Value} cells/s";
        _flightSpeed.ValueChanged += (_, _) => _flightSpeedValue.Text = $"{_flightSpeed.Value} cells/s";
        _complexity.ValueChanged += (_, _) => _complexityValue.Text = ComplexityName(_complexity.Value);
        _density.ValueChanged += (_, _) => _densityValue.Text = $"{_density.Value * 10}%";

        // Two columns of labelled groups, so it reads at a glance instead of as one long list:
        //   Animation | Pipes
        //   Flight    | Graphics
        var animation = Group("Animation", _leftGroups, _leftLabels,
            ("Pipes at once", _concurrent),
            ("Pipes per scene", _perScene),
            ("Growth speed", WithValue(_speed, _speedValue)),
            ("Camera", _camera),
            ("", _separateMonitors),
            ("", _mainOnly));
        _flightGroup = Group("Flight (fly-through camera)", _leftGroups, _leftLabels,
            ("Flight speed", WithValue(_flightSpeed, _flightSpeedValue)),
            ("", _varySpeed),
            ("Course complexity", WithValue(_complexity, _complexityValue)),
            ("Tunnel density", WithValue(_density, _densityValue)));
        var pipes = Group("Pipes", _rightGroups, _rightLabels,
            ("Joints", _joints),
            ("Finish", _finish),
            ("", _thickness),
            ("", _fittings),
            ("", _teapots));
        var graphics = Group("Graphics", _rightGroups, _rightLabels,
            ("Quality", QualityRow()),
            ("On battery", _batteryQuality),
            ("Style", _style),
            ("Anti-aliasing", _aa),
            ("", _surfaces),
            ("", _shadows),
            ("", _movingLight),
            ("", _traced),
            ("", _ao),
            ("", _bloom),
            ("", _dof),
            ("", _stats));

        var columns = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        columns.Controls.Add(Column(animation, _flightGroup), 0, 0);
        columns.Controls.Add(Column(pipes, graphics), 1, 0);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var preview = new Button { Text = "Try it (windowed)", AutoSize = true };
        // Puts the defaults into the dialog. Like any other change, nothing is saved until OK (or Try it).
        var defaults = new Button { Text = "Reset to defaults", AutoSize = true };
        defaults.Click += (_, _) => LoadFrom(new PipesSettings());
        preview.Click += (_, _) =>
        {
            Apply();
            _settings.Save();
            Process.Start(Environment.ProcessPath!, "/w");
        };
        ok.Click += (_, _) => { Apply(); _settings.Save(); Close(); };
        // A button's DialogResult only closes a form shown with ShowDialog(). This one is the app's main window
        // (Application.Run), where it just records the result and the window stays open, so close it explicitly.
        // Escape presses this button too (CancelButton below).
        cancel.Click += (_, _) => Close();
        AcceptButton = ok;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok, preview]);
        // Reset sits on its own at the left, apart from the buttons that close the dialog.
        var buttonRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Bottom };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        defaults.Margin = new Padding(9, 3, 3, 3);
        buttonRow.Controls.Add(defaults, 0, 0);
        buttonRow.Controls.Add(buttons, 1, 0);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(columns);
        layout.Controls.Add(buttonRow);
        Controls.Add(layout);

        LoadFrom(settings);
        _camera.SelectedIndexChanged += (_, _) => UpdateFlightGroup();
        // With one monitor drawn, "own scene on each" has nothing to decide.
        _mainOnly.CheckedChanged += (_, _) => _separateMonitors.Enabled = !_mainOnly.Checked;

        // Hooked up after loading, so opening the dialog doesn't count as the user picking a style.
        _style.SelectedIndexChanged += (_, _) =>
        {
            // Only when the user picks Classic by hand. Choosing the Lite preset switches the style too, but a
            // quality preset is about speed, and mustn't change the pipes' look.
            if (_style.SelectedIndex == (int)GraphicsStyle.Classic && !_settingQuality) UseClassicPipes();
            UpdateEffectToggles();
        };

        // Quality presets. Picking one sets the graphics controls; changing any of those controls by hand shows which
        // preset they now match, or Custom. Picking "Custom" itself changes nothing: it's just where the dropdown
        // sits while the controls don't match a preset.
        _quality.SelectedIndexChanged += (_, _) =>
        {
            if (!_settingQuality && _quality.SelectedIndex > 0) ApplyPreset((QualityPreset)(_quality.SelectedIndex - 1));
        };
        foreach (var combo in new[] { _style, _aa }) combo.SelectedIndexChanged += (_, _) => UpdateQuality();
        foreach (var check in new[] { _surfaces, _shadows, _ao, _bloom, _traced }) check.CheckedChanged += (_, _) => UpdateQuality();
        _testPc.Click += async (_, _) => await TestThisPc();
        UpdateQuality(); // loading didn't fire the handlers above (they weren't attached yet); "Reset" does

        // The Weathered finish is all surface detail (flat, it would just be Mixed), so the two go together: picking
        // Weathered turns detail on, and turning detail off moves Weathered to Mixed.
        _finish.SelectedIndexChanged += (_, _) =>
        {
            if (_finish.SelectedIndex == (int)Finish.Weathered) _surfaces.Checked = true;
        };
        _surfaces.CheckedChanged += (_, _) =>
        {
            if (!_surfaces.Checked && _finish.SelectedIndex == (int)Finish.Weathered) _finish.SelectedIndex = (int)Finish.Mixed;
        };
    }

    /// <summary>
    /// Picking the classic style also sets the pipe options to match the original screensaver. Only a starting
    /// point: any of them can be changed back afterwards.
    /// </summary>
    private void UseClassicPipes()
    {
        _joints.SelectedIndex = (int)JointStyle.Classic;
        _finish.SelectedIndex = (int)Finish.Plastic;
        _camera.SelectedIndex = (int)CameraMotion.Still;
        _thickness.Checked = false;
        _fittings.Checked = false;
        _teapots.Checked = true; // the original had them too
    }

    /// <summary>
    /// Set the graphics controls for <paramref name="preset"/>. The preset is applied to a copy of what the dialog
    /// shows, and the result copied back into the controls, so the rules for what a preset changes (and leaves alone)
    /// live in one place, <see cref="QualityPresets.Apply"/>. Nothing is saved until OK, as with any other change.
    /// </summary>
    private void ApplyPreset(QualityPreset preset)
    {
        var s = new PipesSettings();
        ApplyTo(s);
        QualityPresets.Apply(preset, s);
        _settingQuality = true;
        try
        {
            _style.SelectedIndex = (int)s.Style;
            _aa.SelectedIndex = s.Antialiasing switch { 0 => 0, 2 => 1, 4 => 2, _ => 3 };
            // Turning surface detail off moves a Weathered finish to Mixed (see the constructor): Weathered can't be
            // drawn without it, so that's what the pipes would look like anyway.
            _surfaces.Checked = s.SurfaceDetail;
            _shadows.Checked = s.Shadows;
            _ao.Checked = s.AmbientOcclusion;
            _bloom.Checked = s.Bloom;
            _traced.Checked = s.TracedReflections;
        }
        finally
        {
            _settingQuality = false;
        }
        UpdateEffectToggles();
    }

    /// <summary>Point the quality dropdown at the preset the controls match, or at Custom.</summary>
    private void UpdateQuality()
    {
        if (_settingQuality) return; // mid-preset: the controls are only partly set
        var s = new PipesSettings();
        ApplyTo(s);
        _settingQuality = true; // so moving the dropdown doesn't apply the preset it moves to
        try
        {
            _quality.SelectedIndex = QualityPresets.Match(s) is { } preset ? (int)preset + 1 : 0;
        }
        finally
        {
            _settingQuality = false;
        }
    }

    /// <summary>
    /// "Test this PC": time each preset on this machine and offer the best one that keeps up with the monitor (see
    /// <see cref="Autotune"/>). The test runs in a separate process, with the dialog's current (unsaved) settings,
    /// while this window waits without freezing: <c>await</c> hands the UI thread back to Windows until the test
    /// is done, so the progress window keeps animating and its Cancel button works.
    /// </summary>
    private async Task TestThisPc()
    {
        var snapshot = new PipesSettings();
        ApplyTo(snapshot);
        // The size the test will draw: the main monitor alone if that's all the pipes will use.
        var (width, height) = _mainOnly.Checked && Screen.PrimaryScreen is { } main
            ? (main.Bounds.Width, main.Bounds.Height)
            : GLHost.VirtualScreenSize();
        var hz = GLHost.PrimaryRefreshRate();

        using var cancel = new CancellationTokenSource();
        using var progress = new AutotuneProgressForm($"Testing graphics at {width}×{height}, {hz} Hz…", cancel.Cancel);
        // Like a modal dialog, but without blocking: the settings can't be changed (or the dialog closed) mid-test.
        Enabled = false;
        progress.Show(this);
        AutotuneReport? report = null;
        string? failure = null;
        try
        {
            report = await Autotune.RunAsync(snapshot, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // The user cancelled: nothing to report.
        }
        catch (Exception ex) when (ex is AutotuneFailedException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            failure = ex.Message;
        }
        finally
        {
            // Re-enable this window before closing the progress window. The other way round, for a moment no window
            // of ours can take the focus, so Windows gives it to some other program and the dialog drops behind it.
            Enabled = true;
            progress.Close();
        }

        if (failure != null)
        {
            MessageBox.Show(this,
                "The graphics test couldn't finish. This can be a graphics driver problem: rendering as fast as possible " +
                "is harder on a driver than running the screensaver normally.\n\n" +
                failure + "\n\n" +
                "Try a lower quality preset (Low or Lite), or update your graphics driver.",
                "Test this PC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (report == null) return;

        using var results = new AutotuneResultsForm(report);
        if (results.ShowDialog(this) == DialogResult.OK) _quality.SelectedIndex = (int)report.Recommended + 1;
    }

    /// <summary>The flight settings only matter when flying through the pipes: grey out the whole group otherwise.</summary>
    private void UpdateFlightGroup() => _flightGroup.Enabled = _camera.SelectedIndex == (int)CameraMotion.FlyThrough;

    /// <summary>A word for each stretch of the course complexity slider, from calm to hectic.</summary>
    private static string ComplexityName(int level) => level switch
    {
        <= 2 => "Zen",
        <= 4 => "Relaxed",
        <= 6 => "Balanced",
        <= 8 => "Lively",
        _ => "Wild",
    };

    /// <summary>The effects (and the surface detail) only exist in the modern style.</summary>
    private void UpdateEffectToggles()
    {
        var modern = _style.SelectedIndex == (int)GraphicsStyle.Modern;
        _surfaces.Enabled = _shadows.Enabled = _movingLight.Enabled = _traced.Enabled = _ao.Enabled = _bloom.Enabled = _dof.Enabled = modern;
    }

    /// <summary>
    /// A labelled group box holding a two-column table: a label on the left (blank for checkboxes, which carry their
    /// own text), the control on the right.
    /// </summary>
    private static GroupBox Group(string title, List<GroupBox> groups, List<Label> labels, params (string Label, Control Control)[] rows)
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(4, 6, 4, 4) };
        foreach (var (text, control) in rows)
        {
            // Anchored left only (not top), a control is centred vertically in its row, so labels line up with
            // their controls whatever height the row ends up.
            var label = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 14, 3) };
            labels.Add(label);
            control.Anchor = AnchorStyles.Left;
            control.Margin = new Padding(3, 5, 3, 5);
            table.Controls.Add(label);
            table.Controls.Add(control);
        }
        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly, // so AlignColumns can widen it to match its neighbour
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(6),
        };
        box.Controls.Add(table);
        groups.Add(box);
        return box;
    }

    /// <summary>Groups stacked top to bottom.</summary>
    private static FlowLayoutPanel Column(params Control[] groups)
    {
        var column = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty };
        column.Controls.AddRange(groups);
        return column;
    }

    /// <summary>
    /// The quality dropdown with the "Test this PC" button beside it. The dropdown has no margin on the left, so it
    /// lines up with the dropdowns below it; <see cref="FitDropdowns"/> narrows it so the button ends where they do.
    /// </summary>
    private FlowLayoutPanel QualityRow()
    {
        _quality.Anchor = _testPc.Anchor = AnchorStyles.Left; // centred on each other
        _quality.Margin = new Padding(0, 0, 3, 0);
        _testPc.Margin = new Padding(3, 0, 0, 0);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange([_quality, _testPc]);
        return row;
    }

    /// <summary>A slider with its current value shown to the right of it.</summary>
    private static FlowLayoutPanel WithValue(TrackBar slider, Label value)
    {
        value.Anchor = AnchorStyles.Left; // centred on the slider
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange([slider, value]);
        return row;
    }

    /// <summary>
    /// A slider without tick marks (the value shown next to it says where it is). A Windows slider draws its track
    /// near the top of the control and keeps room below for ticks, so at its usual height it looks higher than the
    /// labels centred on it. OnLoad trims it to just the thumb's height (see <see cref="SliderHeight"/>).
    /// </summary>
    private static TrackBar Slider(int min, int max) =>
        new() { Minimum = min, Maximum = max, TickStyle = TickStyle.None, AutoSize = false, Width = 200 };

    /// <summary>Slider height at 100% display scaling: about the thumb's height plus a little room.</summary>
    private const int SliderHeight = 28;

    /// <summary>
    /// Line things up within each column: every row label as wide as the widest (so the controls start at the same
    /// x in both groups), and both groups as wide as the wider one (so their borders line up).
    /// </summary>
    private static void AlignColumns(List<GroupBox> groups, List<Label> labels)
    {
        var labelWidth = labels.Max(l => l.PreferredWidth);
        foreach (var label in labels) label.MinimumSize = new Size(labelWidth, 0);
        var groupWidth = groups.Max(g => g.PreferredSize.Width);
        foreach (var group in groups) group.MinimumSize = new Size(groupWidth, 0);
    }

    private static ComboBox Dropdown(params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(items);
        return combo;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Sized here rather than when the dropdowns are created: only now are the final font and screen scaling
        // (DPI) known, and text measured earlier would come out too small on a scaled display.
        FitDropdowns(this);
        foreach (var slider in new[] { _speed, _flightSpeed, _complexity, _density })
            slider.Height = LogicalToDeviceUnits(SliderHeight);
        AlignColumns(_leftGroups, _leftLabels);
        AlignColumns(_rightGroups, _rightLabels);
    }

    /// <summary>
    /// Make every dropdown wide enough for its longest item, and all of them the same width so the column lines up.
    /// A fixed pixel width cut off longer labels, especially with Windows display scaling above 100%.
    /// </summary>
    private void FitDropdowns(Control root)
    {
        var combos = new List<ComboBox>();
        void Collect(Control c)
        {
            if (c is ComboBox combo) combos.Add(combo);
            foreach (Control child in c.Controls) Collect(child);
        }
        Collect(root);

        var width = LogicalToDeviceUnits(170); // minimum, scaled for the display
        foreach (var combo in combos)
        {
            foreach (var item in combo.Items)
            {
                var text = TextRenderer.MeasureText(item.ToString(), combo.Font).Width;
                // Room for the arrow button and the control's own padding.
                width = Math.Max(width, text + SystemInformation.VerticalScrollBarWidth + LogicalToDeviceUnits(12));
            }
        }
        foreach (var combo in combos) combo.Width = width;

        // The quality dropdown shares its row with the "Test this PC" button. Narrowed so the pair ends where the
        // other dropdowns do (its own items are short), but never narrower than those items need.
        var qualityItems = _quality.Items.Cast<object>().Max(item => TextRenderer.MeasureText(item.ToString(), _quality.Font).Width)
            + SystemInformation.VerticalScrollBarWidth + LogicalToDeviceUnits(12);
        var besideButton = width - _quality.Margin.Right - _testPc.Margin.Left - _testPc.PreferredSize.Width;
        _quality.Width = Math.Max(qualityItems, besideButton);
    }

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true };

    // Dropdown indices line up with the enum values, so (int) casts convert between them.
    private void LoadFrom(PipesSettings s)
    {
        _concurrent.Value = s.ConcurrentPipes;
        _perScene.Value = s.PipesPerScene;
        _speed.Value = (int)Math.Round(s.Speed);
        _speedValue.Text = $"{_speed.Value} cells/s";
        _camera.SelectedIndex = (int)s.Camera;
        _flightSpeed.Value = (int)Math.Round(s.FlightSpeed);
        _flightSpeedValue.Text = $"{_flightSpeed.Value} cells/s";
        _varySpeed.Checked = s.VaryFlightSpeed;
        _complexity.Value = s.CourseComplexity;
        _complexityValue.Text = ComplexityName(s.CourseComplexity);
        _density.Value = s.TunnelDensity;
        _densityValue.Text = $"{s.TunnelDensity * 10}%";
        UpdateFlightGroup();
        _separateMonitors.Checked = s.SeparateMonitors;
        _mainOnly.Checked = s.MainMonitorOnly;
        _separateMonitors.Enabled = !s.MainMonitorOnly;
        _joints.SelectedIndex = (int)s.Joints;
        _finish.SelectedIndex = (int)s.Finish;
        _thickness.Checked = s.VaryThickness;
        _fittings.Checked = s.Fittings;
        _teapots.Checked = s.Teapots;
        _style.SelectedIndex = (int)s.Style;
        _aa.SelectedIndex = s.Antialiasing switch { 0 => 0, 2 => 1, 4 => 2, _ => 3 };
        _surfaces.Checked = s.SurfaceDetail;
        _ao.Checked = s.AmbientOcclusion;
        _bloom.Checked = s.Bloom;
        _dof.Checked = s.DepthOfField;
        _shadows.Checked = s.Shadows;
        _movingLight.Checked = s.MovingLight;
        _traced.Checked = s.TracedReflections;
        _stats.Checked = s.ShowStats;
        _batteryQuality.SelectedIndex = s.BatteryQuality is { } battery ? (int)battery + 1 : 0;
        UpdateEffectToggles();
    }

    private void Apply() => ApplyTo(_settings);

    /// <summary>
    /// Copy the controls into <paramref name="s"/>. Usually that's the real settings (<see cref="Apply"/>), but the
    /// quality preset logic and the "Test this PC" check read the dialog's state into a throwaway copy, so nothing
    /// changes before OK.
    /// </summary>
    private void ApplyTo(PipesSettings s)
    {
        s.ConcurrentPipes = (int)_concurrent.Value;
        s.PipesPerScene = (int)_perScene.Value;
        s.Speed = _speed.Value;
        s.Camera = (CameraMotion)_camera.SelectedIndex;
        s.FlightSpeed = _flightSpeed.Value;
        s.VaryFlightSpeed = _varySpeed.Checked;
        s.CourseComplexity = _complexity.Value;
        s.TunnelDensity = _density.Value;
        s.SeparateMonitors = _separateMonitors.Checked;
        s.MainMonitorOnly = _mainOnly.Checked;
        s.Joints = (JointStyle)_joints.SelectedIndex;
        s.Finish = (Finish)_finish.SelectedIndex;
        s.VaryThickness = _thickness.Checked;
        s.Fittings = _fittings.Checked;
        s.Teapots = _teapots.Checked;
        s.Style = (GraphicsStyle)_style.SelectedIndex;
        s.Antialiasing = _aa.SelectedIndex switch { 0 => 0, 1 => 2, 2 => 4, _ => 8 };
        s.SurfaceDetail = _surfaces.Checked;
        s.AmbientOcclusion = _ao.Checked;
        s.Bloom = _bloom.Checked;
        s.DepthOfField = _dof.Checked;
        s.Shadows = _shadows.Checked;
        s.MovingLight = _movingLight.Checked;
        s.TracedReflections = _traced.Checked;
        s.ShowStats = _stats.Checked;
        s.BatteryQuality = _batteryQuality.SelectedIndex > 0 ? (QualityPreset)(_batteryQuality.SelectedIndex - 1) : null;
        s.Clamped();
    }
}
