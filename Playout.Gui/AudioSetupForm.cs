using System.Drawing;

namespace Playout.Gui;

internal sealed class AudioSetupForm : Form
{
    private readonly ComboBox devices = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown left = new() { Minimum = 0, Maximum = 1023, Dock = DockStyle.Fill };
    private readonly NumericUpDown right = new() { Minimum = 0, Maximum = 1023, Value = 1, Dock = DockStyle.Fill };

    public EngineDeviceMapping? Mapping { get; private set; }

    public AudioSetupForm(IReadOnlyList<EngineBackend> backends, string title, string description)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(580, 240);

        foreach (EngineBackend backend in backends.Where(item => item.Available))
            foreach (EngineDevice device in backend.Devices.Where(item => item.IsEnabled != false &&
                (backend.Backend != "Wasapi" || item.SharedModeChannels >= 2)))
                devices.Items.Add(new DeviceChoice(backend.Backend, device));

        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.Controls.Add(new Label { Text = "Gerät", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(devices, 1, 0);
        layout.Controls.Add(new Label { Text = "Kanal links (ab 0)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(left, 1, 1);
        layout.Controls.Add(new Label { Text = "Kanal rechts (ab 0)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(right, 1, 2);
        Label info = new() { Dock = DockStyle.Fill, Text = description };
        layout.Controls.Add(info, 0, 3);
        layout.SetColumnSpan(info, 2);
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        Button cancel = new() { Text = "Abbrechen", AutoSize = true, DialogResult = DialogResult.Cancel };
        Button save = new() { Text = "Übernehmen", AutoSize = true };
        save.Click += (_, _) => SaveSelection();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(buttons, 0, 4);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        CancelButton = cancel;
        AcceptButton = save;
        devices.SelectedIndexChanged += (_, _) => UpdateChannels();
        if (devices.Items.Count > 0)
        {
            int defaultIndex = Enumerable.Range(0, devices.Items.Count).FirstOrDefault(index =>
                devices.Items[index] is DeviceChoice choice && choice.Backend == "Wasapi" && choice.Device.IsDefault == true, 0);
            devices.SelectedIndex = defaultIndex;
        }
        else
        {
            save.Enabled = false;
            string errors = string.Join(Environment.NewLine, backends.Where(item => !item.Available && item.Error is not null)
                .Select(item => $"{item.Backend}: {item.Error}"));
            info.Text = "Kein geeignetes Ausgabegerät gefunden." + (errors.Length == 0 ? "" : Environment.NewLine + errors);
        }
    }

    private void UpdateChannels()
    {
        if (devices.SelectedItem is not DeviceChoice choice) return;
        int max = choice.Backend == "Wasapi" ? choice.Device.SharedModeChannels!.Value - 1 : 1023;
        left.Maximum = right.Maximum = max;
        left.Value = 0;
        right.Value = 1;
    }

    private void SaveSelection()
    {
        if (devices.SelectedItem is not DeviceChoice choice) return;
        if (left.Value == right.Value)
        {
            MessageBox.Show(this, "Links und rechts müssen unterschiedliche Kanäle sein.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Mapping = new EngineDeviceMapping(choice.Backend, choice.Device.Id, (int)left.Value, (int)right.Value);
        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record DeviceChoice(string Backend, EngineDevice Device)
    {
        public override string ToString() => $"{Backend}: {Device.Name}" + (Device.IsDefault == true ? " (Standard)" : "");
    }
}
