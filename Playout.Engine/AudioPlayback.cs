using System.Runtime.InteropServices;

internal sealed record DeckPlaybackStatus(string DeckId, string? Path, string State, string? Error);
internal sealed record DevicePlaybackStatus(AudioBackend Backend, string DeviceId, string Name, string? Error);
internal sealed record AudioPlaybackStatus(bool Initialized, IReadOnlyList<DevicePlaybackStatus> Devices, IReadOnlyList<DeckPlaybackStatus> Decks);

internal sealed class AudioPlayback : IDisposable
{
    private readonly object sync = new();
    private readonly List<AudioDeviceSession> sessions = [];
    private readonly Dictionary<string, PlaybackDeck> decks = new(StringComparer.OrdinalIgnoreCase);
    private bool initialized;
    private bool bassInitialized;

    public bool IsInitialized
    {
        get { lock (sync) return initialized; }
    }

    public IReadOnlyList<DeckPlaybackStatus> GetDecks()
    {
        lock (sync) return decks.Values.Select(deck => deck.GetStatus()).ToArray();
    }

    public AudioPlaybackStatus GetStatus()
    {
        lock (sync)
            return new(initialized, sessions.Select(session => session.GetStatus()).ToArray(),
                decks.Values.Select(deck => deck.GetStatus()).ToArray());
    }

    public void Initialize(AudioRoutingStore store)
    {
        lock (sync)
        {
            AudioRoutingConfiguration routing = store.Get();
            if (initialized) throw new InvalidOperationException("Die Audioausgabe ist bereits initialisiert.");
            IReadOnlyList<string> errors = AudioRoutingValidator.Validate(routing);
            if (errors.Count != 0) throw new InvalidOperationException(string.Join(" ", errors));
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Die Audioausgabe wird nur unter Windows unterstützt.");

            try
            {
                if (AudioNative.Init(0, 44100, 0, IntPtr.Zero, IntPtr.Zero) == 0)
                    throw new InvalidOperationException($"BASS-Initialisierung fehlgeschlagen (Fehler {AudioNative.Error()}).");
                bassInitialized = true;

                foreach (DeckOutputAssignment assignment in routing.Decks!)
                    decks.Add(assignment.DeckId!, new PlaybackDeck(assignment.DeckId!));

                var detected = AudioDeviceDiscovery.Discover();
                var configured = routing.Outputs!.Where(output => output.Device is not null)
                    .GroupBy(output => (output.Device!.Backend, Id: output.Device.DeviceId!),
                        DeviceKeyComparer.Instance);
                foreach (var group in configured)
                {
                    AudioDeviceDiscoveryResult backend = detected.Single(result => result.Backend == group.Key.Backend);
                    if (!backend.Available) throw new InvalidOperationException(backend.Error);
                    DetectedAudioDevice? device = backend.Devices.FirstOrDefault(item =>
                        string.Equals(item.Id, group.Key.Id, StringComparison.OrdinalIgnoreCase));
                    if (device is null || device.IsEnabled == false)
                        throw new InvalidOperationException($"Audiogerät '{group.Key.Id}' ist nicht verfügbar.");

                    List<DeviceRoute> routes = [];
                    foreach (AudioOutputConfiguration output in group)
                    {
                        AudioDeviceMapping mapping = output.Device!;
                        if (group.Key.Backend == AudioBackend.Wasapi &&
                            (device.SharedModeChannels is null || mapping.LeftChannel >= device.SharedModeChannels || mapping.RightChannel >= device.SharedModeChannels))
                            throw new InvalidOperationException($"Ausgang '{output.Id}' verwendet einen nicht verfügbaren Gerätekanal.");
                        foreach (DeckOutputAssignment assignment in routing.Decks!.Where(assignment =>
                            string.Equals(assignment.OutputId, output.Id, StringComparison.OrdinalIgnoreCase)))
                            routes.Add(new DeviceRoute(decks[assignment.DeckId!], mapping.LeftChannel, mapping.RightChannel));
                    }
                    AudioDeviceSession session = new(group.Key.Backend, device, group.Select(output => output.Device!).ToArray(), routes);
                    sessions.Add(session);
                    session.Start();
                }
                initialized = true;
            }
            catch
            {
                Reset();
                throw;
            }
        }
    }

    public void Load(string deckId, string path)
    {
        lock (sync)
        {
            PlaybackDeck deck = FindDeck(deckId);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException("Die Audiodatei wurde nicht gefunden.");
            if (AudioNative.SetDevice(0) == 0) throw new InvalidOperationException($"BASS-Gerät konnte nicht gewählt werden (Fehler {AudioNative.Error()}).");
            deck.Load(path);
        }
    }

    public void Play(string deckId)
    {
        lock (sync)
        {
            PlaybackDeck deck = FindDeck(deckId);
            if (!sessions.Any(session => session.Contains(deck)))
                throw new InvalidOperationException("Für diesen Schacht ist kein verfügbarer Ausgang verbunden.");
            deck.Play();
        }
    }

    public void Pause(string deckId) { lock (sync) FindDeck(deckId).Pause(); }
    public void Stop(string deckId) { lock (sync) FindDeck(deckId).Stop(); }
    public void Shutdown() { lock (sync) Reset(); }
    public void Dispose() => Shutdown();

    public void SaveRouting(AudioRoutingStore store, AudioRoutingConfiguration routing)
    {
        lock (sync)
        {
            if (initialized) throw new InvalidOperationException("Vor einer Routingänderung muss die Audioausgabe beendet werden.");
            store.Save(routing);
        }
    }

    private PlaybackDeck FindDeck(string id)
    {
        if (!initialized) throw new InvalidOperationException("Die Audioausgabe ist nicht initialisiert.");
        return decks.TryGetValue(id, out PlaybackDeck? deck) ? deck : throw new InvalidOperationException($"Schacht '{id}' existiert nicht.");
    }

    private void Reset()
    {
        initialized = false;
        foreach (AudioDeviceSession session in sessions) session.Dispose();
        sessions.Clear();
        foreach (PlaybackDeck deck in decks.Values) deck.Dispose();
        decks.Clear();
        if (bassInitialized)
        {
            AudioNative.SetDevice(0);
            AudioNative.Free();
            bassInitialized = false;
        }
    }

    private sealed class DeviceKeyComparer : IEqualityComparer<(AudioBackend Backend, string Id)>
    {
        internal static readonly DeviceKeyComparer Instance = new();
        public bool Equals((AudioBackend Backend, string Id) x, (AudioBackend Backend, string Id) y) =>
            x.Backend == y.Backend && StringComparer.OrdinalIgnoreCase.Equals(x.Id, y.Id);
        public int GetHashCode((AudioBackend Backend, string Id) obj) =>
            HashCode.Combine(obj.Backend, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id));
    }
}

internal sealed record DeviceRoute(PlaybackDeck Deck, int Left, int Right);

internal sealed class AudioDeviceSession : IDisposable
{
    private readonly AudioBackend backend;
    private readonly DetectedAudioDevice device;
    private readonly AudioDeviceMapping[] mappings;
    private readonly List<DeviceRoute> routes;
    private readonly AudioNative.WasapiProcedure wasapiCallback;
    private readonly AudioNative.AsioProcedure asioCallback;
    private float[] samples = [];
    private int channels;
    private double frequency;
    private bool opened;
    private string? callbackError;

    public AudioDeviceSession(AudioBackend backend, DetectedAudioDevice device, AudioDeviceMapping[] mappings, List<DeviceRoute> routes)
    {
        this.backend = backend;
        this.device = device;
        this.mappings = mappings;
        this.routes = routes;
        wasapiCallback = OnWasapi;
        asioCallback = OnAsio;
    }

    public bool Contains(PlaybackDeck deck) => routes.Any(route => ReferenceEquals(route.Deck, deck));
    public DevicePlaybackStatus GetStatus() => new(backend, device.Id, device.Name, Volatile.Read(ref callbackError));

    public void Start()
    {
        if (backend == AudioBackend.Wasapi)
        {
            channels = device.SharedModeChannels ?? 0;
            frequency = device.SharedModeSampleRate ?? 0;
            if (channels <= 0 || frequency <= 0) throw new InvalidOperationException("Das WASAPI-Mixformat ist ungültig.");
            if (AudioNative.WasapiInit(device.NativeIndex, 0, 0, 0, 0.1f, 0, wasapiCallback, IntPtr.Zero) == 0)
                throw new InvalidOperationException($"WASAPI-Initialisierung fehlgeschlagen (Fehler {AudioNative.Error()}).");
            opened = true;
            if (AudioNative.WasapiStart() == 0) throw new InvalidOperationException($"WASAPI-Start fehlgeschlagen (Fehler {AudioNative.Error()}).");
            return;
        }

        if (AudioNative.AsioInit(device.NativeIndex, 0) == 0)
            throw new InvalidOperationException($"ASIO-Initialisierung fehlgeschlagen (Fehler {AudioNative.AsioError()}).");
        opened = true;
        frequency = AudioNative.AsioGetRate();
        if (!double.IsFinite(frequency) || frequency <= 0) throw new InvalidOperationException("Die ASIO-Abtastrate ist ungültig.");
        int[] nativeChannels = mappings.SelectMany(mapping => new[] { mapping.LeftChannel, mapping.RightChannel }).Distinct().Order().ToArray();
        channels = nativeChannels.Length;
        for (int i = 0; i < routes.Count; i++)
            routes[i] = routes[i] with { Left = Array.IndexOf(nativeChannels, routes[i].Left), Right = Array.IndexOf(nativeChannels, routes[i].Right) };
        uint master = (uint)nativeChannels[0];
        if (AudioNative.AsioChannelEnable(0, master, asioCallback, IntPtr.Zero) == 0)
            throw new InvalidOperationException($"ASIO-Kanal konnte nicht aktiviert werden (Fehler {AudioNative.AsioError()}).");
        foreach (int channel in nativeChannels.Skip(1))
            if (AudioNative.AsioChannelJoin(0, (uint)channel, master) == 0)
                throw new InvalidOperationException($"ASIO-Kanal konnte nicht verbunden werden (Fehler {AudioNative.AsioError()}).");
        if (AudioNative.AsioChannelSetFormat(0, master, AudioNative.AsioFloat) == 0 || AudioNative.AsioStart(0, 0) == 0)
            throw new InvalidOperationException($"ASIO-Start fehlgeschlagen (Fehler {AudioNative.AsioError()}).");
    }

    private uint OnWasapi(IntPtr buffer, uint length, IntPtr user) => Fill(buffer, length);
    private uint OnAsio(int input, uint channel, IntPtr buffer, uint length, IntPtr user) => Fill(buffer, length);

    private uint Fill(IntPtr buffer, uint length)
    {
        try
        {
            int count = checked((int)(length / sizeof(float)));
            if (samples.Length < count) samples = new float[count];
            Array.Clear(samples, 0, count);
            int frames = count / channels;
            foreach (DeviceRoute route in routes) route.Deck.Mix(samples, frames, channels, route.Left, route.Right, frequency);
            for (int i = 0; i < count; i++) samples[i] = Math.Clamp(samples[i], -1f, 1f);
            Marshal.Copy(samples, 0, buffer, count);
            return length;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref callbackError, exception.Message);
            return 0;
        }
    }

    public void Dispose()
    {
        if (!opened) return;
        if (backend == AudioBackend.Wasapi)
        {
            if (AudioNative.WasapiSetDevice((uint)device.NativeIndex) != 0)
            {
                AudioNative.WasapiStop(1);
                AudioNative.WasapiFree();
            }
        }
        else if (AudioNative.AsioSetDevice((uint)device.NativeIndex) != 0)
        {
            AudioNative.AsioStop();
            AudioNative.AsioFree();
        }
        opened = false;
    }
}

internal sealed class PlaybackDeck : IDisposable
{
    private readonly object sync = new();
    private readonly string id;
    private readonly float[] decoded = new float[8192];
    private uint handle;
    private int sourceChannels;
    private double sourceRate;
    private int readIndex;
    private int readCount;
    private StereoResampler? resampler;
    private string? path;
    private string state = "Empty";
    private string? error;

    public PlaybackDeck(string id) => this.id = id;
    public DeckPlaybackStatus GetStatus() { lock (sync) return new(id, path, state, error); }

    public void Load(string newPath)
    {
        lock (sync)
        {
            uint stream = AudioNative.StreamCreateFile(0, newPath, 0, 0, AudioNative.StreamDecode | AudioNative.SampleFloat | AudioNative.Unicode);
            if (stream == 0) throw new InvalidOperationException($"Audiodatei konnte nicht geöffnet werden (BASS-Fehler {AudioNative.Error()}).");
            if (AudioNative.ChannelGetInfo(stream, out AudioNative.ChannelInfo info) == 0 || info.Channels is < 1 or > 2 || info.Frequency == 0)
            {
                AudioNative.StreamFree(stream);
                throw new InvalidOperationException("Nur Mono- und Stereo-Dateien mit gültiger Abtastrate werden unterstützt.");
            }
            if (handle != 0) AudioNative.StreamFree(handle);
            handle = stream;
            sourceChannels = (int)info.Channels;
            sourceRate = info.Frequency;
            path = newPath;
            state = "Loaded";
            error = null;
            ClearBuffer();
        }
    }

    public void Play()
    {
        lock (sync)
        {
            if (handle == 0) throw new InvalidOperationException("Es ist keine Audiodatei geladen.");
            if (state == "Error") throw new InvalidOperationException("Der Titel muss nach einem Dekodierungsfehler zurückgesetzt oder neu geladen werden.");
            if (state == "Ended") Rewind();
            state = "Playing";
            error = null;
        }
    }

    public void Pause() { lock (sync) { if (state == "Playing") state = "Paused"; } }
    public void Stop() { lock (sync) { if (handle != 0) { Rewind(); state = "Loaded"; } } }

    public void Mix(float[] target, int frames, int channels, int left, int right, double outputRate)
    {
        lock (sync)
        {
            if (state != "Playing") return;
            if (!resampler!.Mix(target, frames, channels, left, right, outputRate))
                state = error is null ? "Ended" : "Error";
        }
    }

    private (float Left, float Right)? ReadFrame()
    {
        if (readIndex >= readCount)
        {
            int bytes = AudioNative.ChannelGetData(handle, decoded, (uint)(decoded.Length * sizeof(float)));
            if (bytes <= 0)
            {
                int code = AudioNative.Error();
                if (bytes < 0 && code != 45) error = $"Dekodierung fehlgeschlagen (BASS-Fehler {code}).";
                return null;
            }
            if (bytes % (sizeof(float) * sourceChannels) != 0)
            {
                error = "Der Dekoder hat unvollständige Samples geliefert.";
                return null;
            }
            readIndex = 0;
            readCount = bytes / sizeof(float);
        }
        float left = decoded[readIndex++];
        float right = sourceChannels == 1 ? left : decoded[readIndex++];
        return (float.IsFinite(left) ? left : 0, float.IsFinite(right) ? right : 0);
    }

    private void Rewind()
    {
        if (AudioNative.ChannelSetPosition(handle, 0, 0) == 0)
            throw new InvalidOperationException($"Zurücksetzen fehlgeschlagen (BASS-Fehler {AudioNative.Error()}).");
        ClearBuffer();
        error = null;
    }

    private void ClearBuffer()
    {
        readIndex = readCount = 0;
        resampler = new StereoResampler(sourceRate, ReadFrame);
    }
    public void Dispose() { lock (sync) { if (handle != 0) AudioNative.StreamFree(handle); handle = 0; } }
}
