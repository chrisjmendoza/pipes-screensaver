using System.Diagnostics;

namespace Pipes;

/// <summary>The "Settings..." dialog from Screen Saver Settings. Built in code, no designer.</summary>
internal sealed class ConfigForm : Form
{
    private readonly PipesSettings _settings;
    private readonly NumericUpDown _concurrent = new() { Minimum = 1, Maximum = 10, Width = 70 };
    private readonly NumericUpDown _perScene = new() { Minimum = 1, Maximum = 100, Width = 70 };
    private readonly TrackBar _speed = new() { Minimum = 1, Maximum = 40, TickFrequency = 5, Width = 220 };
    private readonly ComboBox _joints = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _finish = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _aa = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly CheckBox _drift = new() { Text = "Slowly orbit the camera", AutoSize = true };

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

        _joints.Items.AddRange(["Classic (ball joints)", "Smooth elbows", "Mixed"]);
        _finish.Items.AddRange(["Glossy plastic", "Metallic"]);
        _aa.Items.AddRange(["Off", "2x", "4x", "8x"]);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) });
            grid.Controls.Add(c);
        }
        Row("Pipes at once", _concurrent);
        Row("Pipes per scene", _perScene);
        Row("Growth speed", _speed);
        Row("Joints", _joints);
        Row("Finish", _finish);
        Row("Anti-aliasing", _aa);
        Row("", _drift);

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

    private void LoadFrom(PipesSettings s)
    {
        _concurrent.Value = s.ConcurrentPipes;
        _perScene.Value = s.PipesPerScene;
        _speed.Value = (int)Math.Round(s.Speed);
        _joints.SelectedIndex = (int)s.Joints;
        _finish.SelectedIndex = (int)s.Finish;
        _aa.SelectedIndex = s.Antialiasing switch { 0 => 0, 2 => 1, 4 => 2, _ => 3 };
        _drift.Checked = s.CameraDrift;
    }

    private void Apply()
    {
        _settings.ConcurrentPipes = (int)_concurrent.Value;
        _settings.PipesPerScene = (int)_perScene.Value;
        _settings.Speed = _speed.Value;
        _settings.Joints = (JointStyle)_joints.SelectedIndex;
        _settings.Finish = (Finish)_finish.SelectedIndex;
        _settings.Antialiasing = _aa.SelectedIndex switch { 0 => 0, 1 => 2, 2 => 4, _ => 8 };
        _settings.CameraDrift = _drift.Checked;
        _settings.Clamped();
    }
}
