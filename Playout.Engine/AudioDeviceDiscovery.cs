using System.Runtime.InteropServices;
using System.Text;

internal sealed record DetectedAudioDevice(
    string Id,
    string Name,
    int NativeIndex,
    bool? IsEnabled,
    bool? IsDefault,
    int? SharedModeChannels,
    int? SharedModeSampleRate);

internal sealed record AudioDeviceDiscoveryResult(
    AudioBackend Backend,
    bool Available,
    string? Error,
    IReadOnlyList<DetectedAudioDevice> Devices);

internal static class AudioDeviceDiscovery
{
    private const int DeviceError = 23;
    private const uint ConfigUnicode = 42;
    private const uint DeviceEnabled = 1;
    private const uint DeviceDefault = 2;
    private const uint DeviceLoopback = 8;
    private const uint DeviceInput = 16;
    private const uint MaximumDevices = 4096;

    private static readonly object WasapiSync = new();
    private static readonly object AsioSync = new();
    private static bool asioUnicodeConfigured;

    public static IReadOnlyList<AudioDeviceDiscoveryResult> Discover()
    {
        return
        [
            DiscoverBackend(AudioBackend.Wasapi, EnumerateWasapi),
            DiscoverBackend(AudioBackend.Asio, EnumerateAsio)
        ];
    }

    private static AudioDeviceDiscoveryResult DiscoverBackend(
        AudioBackend backend,
        Func<IReadOnlyList<DetectedAudioDevice>> enumerate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Unavailable(backend, "Dieses Backend ist nur unter Windows verfügbar.");
        }

        try
        {
            return new AudioDeviceDiscoveryResult(backend, true, null, enumerate());
        }
        catch (DllNotFoundException)
        {
            return Unavailable(backend,
                "Die benötigte BASS-DLL oder eine ihrer Abhängigkeiten wurde nicht gefunden.");
        }
        catch (BadImageFormatException)
        {
            return Unavailable(backend,
                "Die Architektur einer BASS-DLL passt nicht zur Architektur des Engine-Prozesses.");
        }
        catch (EntryPointNotFoundException)
        {
            return Unavailable(backend, "Eine benötigte Funktion fehlt in der BASS-DLL.");
        }
        catch (InvalidOperationException exception)
        {
            return Unavailable(backend, exception.Message);
        }
    }

    private static AudioDeviceDiscoveryResult Unavailable(AudioBackend backend, string error)
    {
        return new AudioDeviceDiscoveryResult(backend, false, error, []);
    }

    private static IReadOnlyList<DetectedAudioDevice> EnumerateWasapi()
    {
        lock (WasapiSync)
        {
            uint unicodeConfig = NativeMethods.BassGetConfig(ConfigUnicode);
            if (unicodeConfig == uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"BASS-Konfiguration konnte nicht gelesen werden (Fehler {NativeMethods.BassErrorGetCode()}).");
            }

            List<DetectedAudioDevice> devices = [];

            for (uint index = 0; index < MaximumDevices; index++)
            {
                if (NativeMethods.WasapiGetDeviceInfo(index, out WasapiDeviceInfo info) == 0)
                {
                    int error = NativeMethods.BassErrorGetCode();
                    if (error == DeviceError)
                    {
                        return devices;
                    }

                    throw new InvalidOperationException($"WASAPI-Geräteerkennung fehlgeschlagen (BASS-Fehler {error}).");
                }

                if ((info.Flags & (DeviceInput | DeviceLoopback)) != 0)
                {
                    continue;
                }

                string? id = ReadBassString(info.Id, unicodeConfig != 0);
                string? name = ReadBassString(info.Name, unicodeConfig != 0);
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException($"WASAPI-Gerät {index} hat keine gültige ID oder keinen Namen.");
                }

                devices.Add(new DetectedAudioDevice(
                    id,
                    name,
                    checked((int)index),
                    (info.Flags & DeviceEnabled) != 0,
                    (info.Flags & DeviceDefault) != 0,
                    checked((int)info.MixChannels),
                    checked((int)info.MixFrequency)));
            }

            throw new InvalidOperationException("Die maximale Anzahl unterstützter WASAPI-Geräte wurde überschritten.");
        }
    }

    private static IReadOnlyList<DetectedAudioDevice> EnumerateAsio()
    {
        lock (AsioSync)
        {
            if (!asioUnicodeConfigured)
            {
                if (NativeMethods.AsioSetUnicode(1) == 0)
                {
                    throw new InvalidOperationException(
                        $"ASIO-Zeichensatz konnte nicht initialisiert werden (Fehler {NativeMethods.AsioErrorGetCode()}).");
                }

                asioUnicodeConfigured = true;
            }

            List<DetectedAudioDevice> devices = [];
            HashSet<string> ids = new(StringComparer.Ordinal);

            for (uint index = 0; index < MaximumDevices; index++)
            {
                if (NativeMethods.AsioGetDeviceInfo(index, out AsioDeviceInfo info) == 0)
                {
                    int error = NativeMethods.AsioErrorGetCode();
                    if (error == DeviceError)
                    {
                        return devices;
                    }

                    throw new InvalidOperationException($"ASIO-Geräteerkennung fehlgeschlagen (BASS-Fehler {error}).");
                }

                string? name = Marshal.PtrToStringUni(info.Name);
                string? driver = Marshal.PtrToStringUni(info.Driver);
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(driver))
                {
                    throw new InvalidOperationException($"ASIO-Gerät {index} hat keinen gültigen Namen oder Treiber.");
                }

                string id = "asio:" + Convert.ToHexString(Encoding.UTF8.GetBytes(name + "\0" + driver));
                if (!ids.Add(id))
                {
                    throw new InvalidOperationException("ASIO-Geräte mit identischem Namen und Treiber können nicht eindeutig zugeordnet werden.");
                }

                devices.Add(new DetectedAudioDevice(id, name, checked((int)index), null, null, null, null));
            }

            throw new InvalidOperationException("Die maximale Anzahl unterstützter ASIO-Geräte wurde überschritten.");
        }
    }

    private static string? ReadBassString(IntPtr value, bool unicode)
    {
        return unicode ? Marshal.PtrToStringUTF8(value) : Marshal.PtrToStringAnsi(value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WasapiDeviceInfo
    {
        public IntPtr Name;
        public IntPtr Id;
        public uint Type;
        public uint Flags;
        public float MinPeriod;
        public float DefaultPeriod;
        public uint MixFrequency;
        public uint MixChannels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AsioDeviceInfo
    {
        public IntPtr Name;
        public IntPtr Driver;
    }

    private static class NativeMethods
    {
        [DllImport("bass.dll", EntryPoint = "BASS_GetConfig")]
        internal static extern uint BassGetConfig(uint option);

        [DllImport("bass.dll", EntryPoint = "BASS_ErrorGetCode")]
        internal static extern int BassErrorGetCode();

        [DllImport("basswasapi.dll", EntryPoint = "BASS_WASAPI_GetDeviceInfo")]
        internal static extern int WasapiGetDeviceInfo(uint device, out WasapiDeviceInfo info);

        [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_SetUnicode")]
        internal static extern int AsioSetUnicode(int unicode);

        [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_GetDeviceInfo")]
        internal static extern int AsioGetDeviceInfo(uint device, out AsioDeviceInfo info);

        [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_ErrorGetCode")]
        internal static extern int AsioErrorGetCode();
    }
}
