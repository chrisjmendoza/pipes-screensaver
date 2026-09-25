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

    private readonly ComboBox _joints = Dropdown("Classic (ball joints)", "Smooth elbows", "Mixed");
    private readonly ComboBox _finish = Dropdown("Glossy plastic", "Metallic", "Weathered (rust, patina, worn paint)", "Mixed (per pipe)");
    private readonly CheckBox _thickness = Check("Vary pipe thickness");
    private readonly CheckBox _fittings = Check("Valves, couplings, flanges and junctions");
    private readonly CheckBox _teapots = Check("Rare teapots (like the original)");

    private readonly ComboBox _style = Dropdown("Modern", "Classic (lite, like the original)");
    private readonly ComboBox _aa = Dropdown("Off", "2x", "4x", "8x");
    private readonly CheckBox _shadows = Check("Shadows (pipes shade the pipes behind them)");
    private readonly CheckBox _ao = Check("Ambient occlusion (soft contact shadows)");
    private readonly CheckBox _bloom = Check("Bloom (glow on highlights)");
    private readonly CheckBox _dof = Check("Depth of field (blur near and far pipes; off in flight)");

    private readonly GroupBox _flightGroup;

    // Each column's groups and row labels, so OnLoad can line them up (see AlignColumns).
    private readonly List<GroupBox> _leftGroups = [], _rightGroups = [];
    private readonly List<Label> _leftLabels = [], _rightLabels = [];

    public ConfigForm(PipesSettings settings)
    {
        _settings = settings;
        Text = "Pipes Settings";
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
            ("", _separateMonitors));
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
            ("Style", _style),
            ("Anti-aliasing", _aa),
            ("", _shadows),
            ("", _ao),
            ("", _bloom),
            ("", _dof));

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
        _camera.SelectedIndexChanged += (_, _) => UpdateFlightSpeedToggle();

        // Hooked up after loading, so opening the dialog doesn't count as the user picking a style.
        _style.SelectedIndexChanged += (_, _) =>
        {
            if (_style.SelectedIndex == (int)GraphicsStyle.Classic) UseClassicPipes();
            UpdateEffectToggles();
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

    /// <summary>The flight settings only matter when flying through the pipes: grey out the whole group otherwise.</summary>
    private void UpdateFlightSpeedToggle() => _flightGroup.Enabled = _camera.SelectedIndex == (int)CameraMotion.FlyThrough;

    /// <summary>A word for each stretch of the course complexity slider, from calm to hectic.</summary>
    private static string ComplexityName(int level) => level switch
    {
        <= 2 => "Zen",
        <= 4 => "Relaxed",
        <= 6 => "Balanced",
        <= 8 => "Lively",
        _ => "Wild",
    };

    /// <summary>The effects only exist in the modern style.</summary>
    private void UpdateEffectToggles()
    {
        var modern = _style.SelectedIndex == (int)GraphicsStyle.Modern;
        _shadows.Enabled = _ao.Enabled = _bloom.Enabled = _dof.Enabled = modern;
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
        UpdateFlightSpeedToggle();
        _separateMonitors.Checked = s.SeparateMonitors;
        _joints.SelectedIndex = (int)s.Joints;
        _finish.SelectedIndex = (int)s.Finish;
        _thickness.Checked = s.VaryThickness;
        _fittings.Checked = s.Fittings;
        _teapots.Checked = s.Teapots;
        _style.SelectedIndex = (int)s.Style;
        _aa.SelectedIndex = s.Antialiasing switch { 0 => 0, 2 => 1, 4 => 2, _ => 3 };
        _ao.Checked = s.AmbientOcclusion;
        _bloom.Checked = s.Bloom;
        _dof.Checked = s.DepthOfField;
        _shadows.Checked = s.Shadows;
        UpdateEffectToggles();
    }

    private void Apply()
    {
        _settings.ConcurrentPipes = (int)_concurrent.Value;
        _settings.PipesPerScene = (int)_perScene.Value;
        _settings.Speed = _speed.Value;
        _settings.Camera = (CameraMotion)_camera.SelectedIndex;
        _settings.FlightSpeed = _flightSpeed.Value;
        _settings.VaryFlightSpeed = _varySpeed.Checked;
        _settings.CourseComplexity = _complexity.Value;
        _settings.TunnelDensity = _density.Value;
        _settings.SeparateMonitors = _separateMonitors.Checked;
        _settings.Joints = (JointStyle)_joints.SelectedIndex;
        _settings.Finish = (Finish)_finish.SelectedIndex;
        _settings.VaryThickness = _thickness.Checked;
        _settings.Fittings = _fittings.Checked;
        _settings.Teapots = _teapots.Checked;
        _settings.Style = (GraphicsStyle)_style.SelectedIndex;
        _settings.Antialiasing = _aa.SelectedIndex switch { 0 => 0, 1 => 2, 2 => 4, _ => 8 };
        _settings.AmbientOcclusion = _ao.Checked;
        _settings.Bloom = _bloom.Checked;
        _settings.DepthOfField = _dof.Checked;
        _settings.Shadows = _shadows.Checked;
        _settings.Clamped();
    }
}
