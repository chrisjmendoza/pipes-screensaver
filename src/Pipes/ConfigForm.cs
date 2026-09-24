using System.Diagnostics;

namespace Pipes;

/// <summary>The "Settings..." dialog from Screen Saver Settings. Built in code, no designer.</summary>
internal sealed class ConfigForm : Form
{
    private readonly PipesSettings _settings;

    private readonly NumericUpDown _concurrent = new() { Minimum = 1, Maximum = PipesSettings.MaxConcurrentPipes, Width = 70 };
    private readonly NumericUpDown _perScene = new() { Minimum = 1, Maximum = PipesSettings.MaxPipesPerScene, Width = 70 };
    private readonly TrackBar _speed = new() { Minimum = 1, Maximum = PipesSettings.MaxSpeed, TickFrequency = 10, Width = 220 };
    private readonly Label _speedValue = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox _camera = Dropdown("Still", "Slow orbit", "Floating drift");

    private readonly ComboBox _joints = Dropdown("Classic (ball joints)", "Smooth elbows", "Mixed");
    private readonly ComboBox _finish = Dropdown("Glossy plastic", "Metallic", "Mixed (per pipe)");
    private readonly CheckBox _thickness = Check("Vary pipe thickness");
    private readonly CheckBox _fittings = Check("Valves, couplings, flanges and junctions");
    private readonly CheckBox _teapots = Check("Rare teapots (like the original)");

    private readonly ComboBox _aa = Dropdown("Off", "2x", "4x", "8x");
    private readonly CheckBox _ao = Check("Ambient occlusion (soft contact shadows)");
    private readonly CheckBox _bloom = Check("Bloom (glow on highlights)");
    private readonly CheckBox _dof = Check("Depth of field (blur near and far pipes)");

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
        var speedRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        speedRow.Controls.AddRange([_speed, _speedValue]);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) });
            grid.Controls.Add(c);
        }
        void Heading(string text)
        {
            var heading = new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 12, 0, 2) };
            grid.Controls.Add(heading);
            grid.SetColumnSpan(heading, 2);
        }

        Heading("Animation");
        Row("Pipes at once", _concurrent);
        Row("Pipes per scene", _perScene);
        Row("Growth speed", speedRow);
        Row("Camera", _camera);

        Heading("Pipes");
        Row("Joints", _joints);
        Row("Finish", _finish);
        Row("", _thickness);
        Row("", _fittings);
        Row("", _teapots);

        Heading("Graphics");
        Row("Anti-aliasing", _aa);
        Row("", _ao);
        Row("", _bloom);
        Row("", _dof);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var preview = new Button { Text = "Try it (windowed)", AutoSize = true };
        preview.Click += (_, _) =>
        {
            Apply();
            _settings.Save();
            Process.Start(Environment.ProcessPath!, "/w");
        };
        ok.Click += (_, _) => { Apply(); _settings.Save(); Close(); };
        AcceptButton = ok;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.AddRange([cancel, ok, preview]);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(grid);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        LoadFrom(settings);
    }

    private static ComboBox Dropdown(params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        combo.Items.AddRange(items);
        return combo;
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
        _joints.SelectedIndex = (int)s.Joints;
        _finish.SelectedIndex = (int)s.Finish;
        _thickness.Checked = s.VaryThickness;
        _fittings.Checked = s.Fittings;
        _teapots.Checked = s.Teapots;
        _aa.SelectedIndex = s.Antialiasing switch { 0 => 0, 2 => 1, 4 => 2, _ => 3 };
        _ao.Checked = s.AmbientOcclusion;
        _bloom.Checked = s.Bloom;
        _dof.Checked = s.DepthOfField;
    }

    private void Apply()
    {
        _settings.ConcurrentPipes = (int)_concurrent.Value;
        _settings.PipesPerScene = (int)_perScene.Value;
        _settings.Speed = _speed.Value;
        _settings.Camera = (CameraMotion)_camera.SelectedIndex;
        _settings.Joints = (JointStyle)_joints.SelectedIndex;
        _settings.Finish = (Finish)_finish.SelectedIndex;
        _settings.VaryThickness = _thickness.Checked;
        _settings.Fittings = _fittings.Checked;
        _settings.Teapots = _teapots.Checked;
        _settings.Antialiasing = _aa.SelectedIndex switch { 0 => 0, 1 => 2, 2 => 4, _ => 8 };
        _settings.AmbientOcclusion = _ao.Checked;
        _settings.Bloom = _bloom.Checked;
        _settings.DepthOfField = _dof.Checked;
        _settings.Clamped();
    }
}
