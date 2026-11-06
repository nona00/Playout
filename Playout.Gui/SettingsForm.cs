using System.Drawing;
using System.Windows.Forms;

namespace Playout.Gui;

internal sealed class SettingsForm : Form
{
    private readonly Action<IReadOnlyList<string>> saveSources;
    private readonly Func<Task> configureAudio;
    private readonly Func<Task> configurePfl;
    private readonly Func<Task> initializeAudio;
    private readonly Func<Task> shutdownAudio;
    private readonly ListBox sourcesList = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly List<string> sources;

    public IReadOnlyList<string> Sources => sources;

    public SettingsForm(
        IEnumerable<string> currentSources,
        Action<IReadOnlyList<string>> saveSources,
        Func<Task> configureAudio,
        Func<Task> configurePfl,
        Func<Task> initializeAudio,
        Func<Task> shutdownAudio)
    {
        this.saveSources = saveSources;
        this.configureAudio = configureAudio;
        this.configurePfl = configurePfl;
        this.initializeAudio = initializeAudio;
        this.shutdownAudio = shutdownAudio;
        sources = currentSources.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Text = "Einstellungen";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 450);
        Size = new Size(760, 560);
        BackColor = Color.FromArgb(29, 34, 45);
        ForeColor = Color.FromArgb(235, 238, 245);
        Font = new Font("Segoe UI", 10);

        TabControl tabs = new() { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateSourcesPage());
        tabs.TabPages.Add(CreateAudioPage());

        Button cancel = new() { Text = "Abbrechen", AutoSize = true, DialogResult = DialogResult.Cancel };
        Button save = new() { Text = "Speichern und schließen", AutoSize = true };
        save.Click += (_, _) => SaveAndClose();
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(12, 8, 12, 8), FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        Controls.Add(tabs);
        Controls.Add(buttons);
        CancelButton = cancel;
        AcceptButton = save;
        RenderSources();
    }

    private TabPage CreateSourcesPage()
    {
        TabPage page = new("Musikquellen") { BackColor = BackColor, ForeColor = ForeColor };
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Musik wird aus diesen Ordnern einschließlich Unterordnern eingelesen. Die Audiodateien bleiben an ihrem Speicherort."
        }, 0, 0);
        layout.Controls.Add(sourcesList, 0, 1);
        FlowLayoutPanel actions = new() { Dock = DockStyle.Fill, AutoSize = true };
        Button add = new() { Text = "Ordner hinzufügen", AutoSize = true };
        Button remove = new() { Text = "Ausgewählten Ordner entfernen", AutoSize = true };
        add.Click += (_, _) => AddSource();
        remove.Click += (_, _) => RemoveSource();
        actions.Controls.Add(add);
        actions.Controls.Add(remove);
        layout.Controls.Add(actions, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage CreateAudioPage()
    {
        TabPage page = new("Audio") { BackColor = BackColor, ForeColor = ForeColor };
        FlowLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(12), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            Text = "Die Audioeinrichtung ordnet die beiden Programmschächte und PFL getrennt Kanalpaaren vorhandener WASAPI- oder ASIO-Geräte zu. Änderungen sind nur bei beendeter Audioausgabe möglich."
        });
        layout.Controls.Add(CreateAsyncButton("Programmausgänge einrichten", configureAudio));
        layout.Controls.Add(CreateAsyncButton("PFL-Ausgang einrichten", configurePfl));
        layout.Controls.Add(CreateAsyncButton("Audio starten", initializeAudio));
        layout.Controls.Add(CreateAsyncButton("Audio beenden", shutdownAudio));
        page.Controls.Add(layout);
        return page;
    }

    private static Button CreateAsyncButton(string text, Func<Task> action)
    {
        Button button = new() { Text = text, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        button.Click += async (_, _) => await action();
        return button;
    }

    private void AddSource()
    {
        using FolderBrowserDialog dialog = new() { Description = "Musikordner auswählen", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string path = MusicLibrary.NormalizePath(dialog.SelectedPath);
        if (sources.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Dieser Ordner ist bereits als Musikquelle eingetragen.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        sources.Add(path);
        RenderSources();
    }

    private void RemoveSource()
    {
        if (sourcesList.SelectedItem is not string source) return;
        sources.RemoveAll(item => string.Equals(item, source, StringComparison.OrdinalIgnoreCase));
        RenderSources();
    }

    private void RenderSources()
    {
        sourcesList.Items.Clear();
        foreach (string source in sources.OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)) sourcesList.Items.Add(source);
    }

    private void SaveAndClose()
    {
        try
        {
            saveSources(sources);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Einstellungen konnten nicht gespeichert werden", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
