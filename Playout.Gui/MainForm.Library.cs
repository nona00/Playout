using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;

namespace Playout.Gui;

internal sealed partial class MainForm
{
    private readonly MusicLibrary library = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playout", "Gui", "music-sources.json"));
    private readonly EngineClient engine = new(new HttpClient
    {
        BaseAddress = new Uri("http://127.0.0.1:5187/"),
        Timeout = TimeSpan.FromSeconds(5)
    });
    private readonly List<string> sources = [];
    private readonly List<MusicTrack> tracks = [];
    private readonly ComboBox sourceSelector = new();
    private readonly TextBox musicSearch = new();
    private readonly ListView musicList = new();
    private readonly Label libraryStatus = new() { AutoSize = true, ForeColor = MutedColor, Padding = new Padding(4, 8, 0, 0) };
    private readonly Label engineStatus = new() { AutoSize = true, ForeColor = TextColor, Padding = new Padding(6, 8, 0, 0), Text = "Engine: Verbindung wird geprüft …" };
    private readonly Label deck1Status = new();
    private readonly Label deck2Status = new();
    private readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 2000 };
    private readonly CancellationTokenSource lifetime = new();
    private bool scanning;
    private bool polling;
    private bool commandInProgress;

    private Control CreateEngineToolbar()
    {
        FlowLayoutPanel toolbar = new() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(12, 4, 0, 0), WrapContents = false };
        toolbar.Controls.Add(CreateActionButton("Audio einrichten", async () => await ConfigureAudioAsync()));
        toolbar.Controls.Add(CreateActionButton("Audio starten", async () => await InitializeAudioAsync()));
        toolbar.Controls.Add(CreateActionButton("Audio beenden", async () => await ShutdownAudioAsync()));
        toolbar.Controls.Add(engineStatus);
        return toolbar;
    }

    private static Button CreateActionButton(string text, Func<Task> action)
    {
        Button button = new() { Text = text, AutoSize = true, ForeColor = Color.Black, UseVisualStyleBackColor = true };
        button.Click += async (_, _) => await action();
        return button;
    }

    private async void MainFormShown(object? sender, EventArgs e)
    {
        try
        {
            sources.AddRange(library.LoadSources());
            RenderSources();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowError("Musikquellen konnten nicht geladen werden", exception);
        }
        statusTimer.Tick += async (_, _) => await RefreshEngineStatusAsync();
        statusTimer.Start();
        Task statusRefresh = RefreshEngineStatusAsync();
        await RefreshMusicAsync();
        await statusRefresh;
    }

    private void MainFormClosed(object? sender, FormClosedEventArgs e)
    {
        statusTimer.Stop();
        statusTimer.Dispose();
        lifetime.Cancel();
        engine.Dispose();
    }

    private async Task AddSourceAsync()
    {
        if (scanning) return;
        using FolderBrowserDialog dialog = new() { Description = "Musikordner auswählen", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            string path = MusicLibrary.NormalizePath(dialog.SelectedPath);
            if (sources.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                sourceSelector.SelectedItem = path;
                return;
            }
            List<string> updated = [.. sources, path];
            library.SaveSources(updated);
            sources.Add(path);
            RenderSources();
            sourceSelector.SelectedItem = path;
            await RefreshMusicAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowError("Musikquelle konnte nicht gespeichert werden", exception);
        }
    }

    private async Task RemoveSourceAsync()
    {
        if (scanning) return;
        if (sourceSelector.SelectedItem is not string source)
        {
            MessageBox.Show(this, "Bitte zuerst eine Musikquelle auswählen.", "Musikquellen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"Quelle entfernen? Die Audiodateien bleiben unverändert.\n\n{source}", "Musikquellen",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            List<string> updated = sources.Where(item => !string.Equals(item, source, StringComparison.OrdinalIgnoreCase)).ToList();
            library.SaveSources(updated);
            sources.Clear();
            sources.AddRange(updated);
            RenderSources();
            await RefreshMusicAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowError("Musikquelle konnte nicht entfernt werden", exception);
        }
    }

    private void RenderSources()
    {
        sourceSelector.Items.Clear();
        foreach (string source in sources) sourceSelector.Items.Add(source);
        if (sourceSelector.Items.Count > 0) sourceSelector.SelectedIndex = 0;
        sourceSelector.Enabled = sourceSelector.Items.Count > 0;
    }

    private async Task RefreshMusicAsync()
    {
        if (scanning || IsDisposed) return;
        scanning = true;
        libraryStatus.Text = "Lese Ordner …";
        try
        {
            string[] snapshot = sources.ToArray();
            MusicScanResult result = await Task.Run(() => library.Scan(snapshot, lifetime.Token), lifetime.Token);
            if (IsDisposed) return;
            tracks.Clear();
            tracks.AddRange(result.Tracks);
            RenderTracks();
            if (result.Errors.Count > 0)
            {
                libraryStatus.Text = $"{tracks.Count} Titel, {result.Errors.Count} Fehler";
                string details = string.Join(Environment.NewLine, result.Errors.Take(5));
                if (result.Errors.Count > 5) details += $"{Environment.NewLine}Weitere Fehler: {result.Errors.Count - 5}";
                MessageBox.Show(this, details, "Musikquellen nicht vollständig eingelesen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!IsDisposed) ShowError("Musikquellen konnten nicht eingelesen werden", exception);
        }
        finally { scanning = false; }
    }

    private void RenderTracks()
    {
        string search = musicSearch.Text.Trim();
        musicList.BeginUpdate();
        try
        {
            musicList.Items.Clear();
            foreach (MusicTrack track in tracks.Where(track => track.Path.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
            {
                ListViewItem item = new(track.Title) { Tag = track };
                item.SubItems.Add(track.Path);
                musicList.Items.Add(item);
            }
            libraryStatus.Text = $"{musicList.Items.Count} / {tracks.Count} Titel";
        }
        finally { musicList.EndUpdate(); }
    }

    private async Task LoadSelectedAsync(string deckId)
    {
        if (musicList.SelectedItems.Count != 1 || musicList.SelectedItems[0].Tag is not MusicTrack track)
        {
            MessageBox.Show(this, "Bitte zuerst eine Audiodatei auswählen.", "Musik laden", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!File.Exists(track.Path))
        {
            MessageBox.Show(this, "Die Datei ist nicht mehr verfügbar. Bitte die Quelle erneut einlesen.", "Musik laden", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await RunEngineActionAsync(async () =>
        {
            EngineAudioStatus status = await engine.GetStatusAsync();
            EngineDeckStatus? deck = status.Decks.FirstOrDefault(item => item.DeckId.Equals(deckId, StringComparison.OrdinalIgnoreCase));
            if (deck?.State == "Playing" && MessageBox.Show(this,
                $"{deckId} spielt gerade. Beim Laden wird der laufende Titel ersetzt. Fortfahren?",
                "Titel ersetzen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            await engine.LoadAsync(deckId, track.Path);
        });
    }

    private async Task ConfigureAudioAsync()
    {
        await RunEngineActionAsync(async () =>
        {
            EngineAudioStatus status = await engine.GetStatusAsync();
            if (status.Initialized)
                throw new InvalidOperationException("Die Audioausgabe ist aktiv. Bitte zuerst bewusst 'Audio beenden' wählen; dadurch wird die laufende Wiedergabe gestoppt.");
            IReadOnlyList<EngineBackend> backends = await engine.GetDevicesAsync();
            using AudioSetupForm dialog = new(backends);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Mapping is null) return;
            if (MessageBox.Show(this, "Die zugeordneten Programmausgänge beider Decks werden auf das gewählte Kanalpaar gelegt. Andere Ausgänge bleiben unverändert. Überlappende Kanäle werden gemischt; auch PFL kann dadurch hörbar sein. Fortfahren?",
                "Routing ändern", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            await engine.AssignDeckOutputsAsync(dialog.Mapping);
        });
    }

    private async Task InitializeAudioAsync()
    {
        await RunEngineActionAsync(async () =>
        {
            EngineRouting routing = await engine.GetRoutingAsync();
            bool connected = routing.Decks.Where(deck => deck.DeckId.Equals("Deck 1", StringComparison.OrdinalIgnoreCase) ||
                deck.DeckId.Equals("Deck 2", StringComparison.OrdinalIgnoreCase)).Any(deck =>
                routing.Outputs.Any(output => output.Id.Equals(deck.OutputId, StringComparison.OrdinalIgnoreCase) && output.Device is not null));
            if (!connected) throw new InvalidOperationException("Kein Deckausgang ist verbunden. Bitte zuerst 'Audio einrichten' wählen.");
            await engine.InitializeAsync();
        });
    }

    private async Task ShutdownAudioAsync()
    {
        if (MessageBox.Show(this, "Die gesamte Wiedergabe der Engine wird beendet. Fortfahren?", "Audio beenden",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await RunEngineActionAsync(engine.ShutdownAsync);
    }

    private async Task RunEngineActionAsync(Func<Task> action)
    {
        if (commandInProgress || IsDisposed) return;
        commandInProgress = true;
        try { await action(); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException or IOException)
        {
            if (!IsDisposed) ShowError("Engine-Befehl fehlgeschlagen", exception);
        }
        finally
        {
            commandInProgress = false;
            if (!IsDisposed) await RefreshEngineStatusAsync();
        }
    }

    private async Task RefreshEngineStatusAsync()
    {
        if (polling || IsDisposed) return;
        polling = true;
        try
        {
            EngineAudioStatus status = await engine.GetStatusAsync();
            if (IsDisposed) return;
            string? error = status.Devices.Select(device => device.Error).FirstOrDefault(value => !string.IsNullOrEmpty(value));
            engineStatus.Text = error is not null ? $"Audiofehler: {error}" : status.Initialized ? "Engine: Audio aktiv" : "Engine verbunden – Audio nicht gestartet";
            UpdateDeckStatus(deck1Status, status.Decks.FirstOrDefault(deck => deck.DeckId.Equals("Deck 1", StringComparison.OrdinalIgnoreCase)));
            UpdateDeckStatus(deck2Status, status.Decks.FirstOrDefault(deck => deck.DeckId.Equals("Deck 2", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException or IOException)
        {
            if (IsDisposed) return;
            engineStatus.Text = "Engine nicht erreichbar – bitte separat starten";
            deck1Status.Text = deck2Status.Text = "Engine nicht verbunden";
        }
        finally { polling = false; }
    }

    private static void UpdateDeckStatus(Label label, EngineDeckStatus? deck)
    {
        label.Text = deck is null ? "Audio nicht initialisiert" :
            $"{(deck.Path is null ? "Kein Titel geladen" : Path.GetFileName(deck.Path))}\n{deck.State}" +
            (string.IsNullOrWhiteSpace(deck.Error) ? "" : $"\n{deck.Error}");
    }

    private void ShowError(string title, Exception exception) =>
        MessageBox.Show(this, exception.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
