using System.Drawing;
using System.Diagnostics;
using System.ComponentModel;
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
    private readonly TextBox musicSearch = new();
    private readonly ListView musicList = new();
    private readonly Label libraryStatus = new() { AutoSize = true, ForeColor = MutedColor, Padding = new Padding(4, 8, 0, 0) };
    private readonly Label engineStatus = new() { AutoSize = true, ForeColor = TextColor, Padding = new Padding(6, 8, 0, 0), Text = "Engine: Verbindung wird geprüft …" };
    private readonly Label deck1Status = new();
    private readonly Label deck2Status = new();
    private readonly Label pflStatus = new() { AutoSize = true, ForeColor = MutedColor, Padding = new Padding(0, 8, 8, 0), Text = "PFL nicht verbunden" };
    private readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 2000 };
    private readonly CancellationTokenSource lifetime = new();
    private bool scanning;
    private bool polling;
    private bool commandInProgress;
    private bool startingEngine;
    private bool startingWeb;

    private Control CreateEngineToolbar()
    {
        FlowLayoutPanel toolbar = new() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(12, 4, 0, 0), WrapContents = false };
        Button settings = CreateActionButton("?", async () => await OpenSettingsAsync());
        settings.AccessibleName = "Einstellungen";
        settings.Font = new Font("Segoe UI Symbol", 12, FontStyle.Regular);
        settings.Width = 38;
        settings.TextAlign = ContentAlignment.MiddleCenter;
        toolbar.Controls.Add(settings);
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
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowError("Musikquellen konnten nicht geladen werden", exception);
        }
        statusTimer.Tick += async (_, _) => await RefreshEngineStatusAsync();
        statusTimer.Start();
        await EnsureEngineRunningAsync();
        await EnsureWebRunningAsync();
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

    private async Task OpenSettingsAsync()
    {
        if (scanning) return;
        using SettingsForm dialog = new(
            sources,
            updated => library.SaveSources(updated),
            ConfigureAudioAsync,
            ConfigurePflAsync,
            InitializeAudioAsync,
            ShutdownAudioAsync);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        sources.Clear();
        sources.AddRange(dialog.Sources);
        await RefreshMusicAsync();
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

    private async Task EnsureEngineRunningAsync()
    {
        try
        {
            await engine.GetStatusAsync();
            return;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
        }

        if (startingEngine) return;
        startingEngine = true;
        try
        {
            StartEngineProcess();
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !lifetime.IsCancellationRequested)
            {
                await Task.Delay(250, lifetime.Token);
                try
                {
                    await engine.GetStatusAsync();
                    return;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                }
            }
            throw new InvalidOperationException("Die gestartete Engine antwortet nicht auf http://127.0.0.1:5187.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            if (!IsDisposed) ShowError("Engine konnte nicht gestartet werden", exception);
        }
        finally { startingEngine = false; }
    }

    private static void StartEngineProcess()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Playout.Engine.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Playout.Engine", "bin", "Debug", "net8.0", "Playout.Engine.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Playout.Engine", "bin", "Release", "net8.0", "Playout.Engine.exe"))
        ];
        string? executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null)
            throw new InvalidOperationException("Playout.Engine.exe wurde nicht gefunden. Bitte die Engine erstellen oder neben der GUI bereitstellen.");
        Process? process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        });
        if (process is null) throw new InvalidOperationException("Der Engine-Prozess konnte nicht erstellt werden.");
    }

    private async Task EnsureWebRunningAsync()
    {
        if (await IsWebRunningAsync()) return;
        if (startingWeb) return;
        startingWeb = true;
        try
        {
            StartWebProcess();
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !lifetime.IsCancellationRequested)
            {
                await Task.Delay(250, lifetime.Token);
                if (await IsWebRunningAsync()) return;
            }
            throw new InvalidOperationException("Die gestartete Weboberfläche antwortet nicht auf http://127.0.0.1:5188.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            if (!IsDisposed) ShowError("Weboberfläche konnte nicht gestartet werden", exception);
        }
        finally { startingWeb = false; }
    }

    private static async Task<bool> IsWebRunningAsync()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            using HttpResponseMessage response = await client.GetAsync("http://127.0.0.1:5188/");
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    private static void StartWebProcess()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Playout.Web.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Playout.Web", "bin", "Debug", "net8.0", "Playout.Web.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Playout.Web", "bin", "Release", "net8.0", "Playout.Web.exe"))
        ];
        string? executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null)
            throw new InvalidOperationException("Playout.Web.exe wurde nicht gefunden. Bitte die Webanwendung erstellen oder neben der GUI bereitstellen.");
        Process? process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        });
        if (process is null) throw new InvalidOperationException("Der Webprozess konnte nicht erstellt werden.");
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

    private async Task PreviewSelectedAsync()
    {
        if (!TryGetSelectedTrack(out MusicTrack? track)) return;
        MusicTrack selectedTrack = track!;
        await RunEngineActionAsync(async () =>
        {
            await engine.LoadAsync("PFL", selectedTrack.Path);
            await engine.PlayAsync("PFL");
        });
    }

    private async Task LoadAndPlayInFreeDeckAsync()
    {
        if (!TryGetSelectedTrack(out MusicTrack? track)) return;
        MusicTrack selectedTrack = track!;
        await RunEngineActionAsync(async () =>
        {
            EngineAudioStatus status = await engine.GetStatusAsync();
            EngineDeckStatus? free = status.Decks.FirstOrDefault(deck =>
                (deck.DeckId.Equals("Deck 1", StringComparison.OrdinalIgnoreCase) || deck.DeckId.Equals("Deck 2", StringComparison.OrdinalIgnoreCase)) &&
                deck.Path is null);
            if (free is null) throw new InvalidOperationException("Kein freier Ausspielschacht verfügbar.");
            await engine.LoadAsync(free.DeckId, selectedTrack.Path);
            await engine.PlayAsync(free.DeckId);
        });
    }

    private bool TryGetSelectedTrack(out MusicTrack? track)
    {
        track = musicList.SelectedItems.Count == 1 ? musicList.SelectedItems[0].Tag as MusicTrack : null;
        if (track is null)
        {
            MessageBox.Show(this, "Bitte zuerst eine Audiodatei auswählen.", "Musik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        if (!File.Exists(track.Path))
        {
            MessageBox.Show(this, "Die Datei ist nicht mehr verfügbar. Bitte die Quelle erneut einlesen.", "Musik", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            track = null;
            return false;
        }
        return true;
    }

    private async Task ConfigureAudioAsync()
    {
        await RunEngineActionAsync(async () =>
        {
            EngineAudioStatus status = await engine.GetStatusAsync();
            if (status.Initialized)
                throw new InvalidOperationException("Die Audioausgabe ist aktiv. Bitte zuerst bewusst 'Audio beenden' wählen; dadurch wird die laufende Wiedergabe gestoppt.");
            IReadOnlyList<EngineBackend> backends = await engine.GetDevicesAsync();
            using AudioSetupForm dialog = new(backends, "Programmausgänge einrichten",
                "Beide Decks werden auf dieses Kanalpaar gemischt. Andere Ausgänge bleiben unverändert; gemeinsame Kanäle können auch deren Signal enthalten.");
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Mapping is null) return;
            if (MessageBox.Show(this, "Die zugeordneten Programmausgänge beider Decks werden auf das gewählte Kanalpaar gelegt. Andere Ausgänge bleiben unverändert. Überlappende Kanäle werden gemischt; auch PFL kann dadurch hörbar sein. Fortfahren?",
                "Routing ändern", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            await engine.AssignDeckOutputsAsync(dialog.Mapping);
        });
    }

    private async Task ConfigurePflAsync()
    {
        await RunEngineActionAsync(async () =>
        {
            EngineAudioStatus status = await engine.GetStatusAsync();
            if (status.Initialized)
                throw new InvalidOperationException("Die Audioausgabe ist aktiv. Bitte zuerst bewusst 'Audio beenden' wählen; dadurch wird die laufende Wiedergabe gestoppt.");
            IReadOnlyList<EngineBackend> backends = await engine.GetDevicesAsync();
            using AudioSetupForm dialog = new(backends, "PFL-Ausgang einrichten",
                "PFL wird auf dieses Kanalpaar gelegt. Es sollte sich vom Programmkanal unterscheiden; gleiche Kanäle mischen Programm und PFL.");
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Mapping is null) return;
            await engine.AssignPflOutputAsync(dialog.Mapping);
        });
    }

    private async Task InitializeAudioAsync()
    {
        await RunEngineActionAsync(async () =>
        {
            EngineRouting routing = await engine.GetRoutingAsync();
            bool connected = routing.Decks.Where(deck => deck.DeckId.Equals("Deck 1", StringComparison.OrdinalIgnoreCase) ||
                deck.DeckId.Equals("Deck 2", StringComparison.OrdinalIgnoreCase) || deck.DeckId.Equals("PFL", StringComparison.OrdinalIgnoreCase)).Any(deck =>
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
            UpdatePflStatus(status.Decks.FirstOrDefault(deck => deck.DeckId.Equals("PFL", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException or IOException)
        {
            if (IsDisposed) return;
            engineStatus.Text = "Engine nicht erreichbar – bitte separat starten";
            deck1Status.Text = deck2Status.Text = "Engine nicht verbunden";
            pflStatus.Text = "PFL: Engine nicht verbunden";
        }
        finally { polling = false; }
    }

    private void UpdatePflStatus(EngineDeckStatus? deck)
    {
        pflStatus.Text = deck is null ? "PFL nicht initialisiert" :
            $"{(deck.Path is null ? "Kein Titel" : Path.GetFileName(deck.Path))}: {deck.State}" +
            (string.IsNullOrWhiteSpace(deck.Error) ? "" : $" – {deck.Error}");
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
