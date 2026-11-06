using System.Text.Json;
using System.Text.Json.Serialization;

internal enum AudioBackend
{
    Unknown,
    Wasapi,
    Asio
}

internal enum AudioOutputPurpose
{
    Unknown,
    Program,
    Pfl
}

internal sealed record AudioDeviceMapping(
    AudioBackend Backend,
    string? DeviceId,
    int LeftChannel,
    int RightChannel);

internal sealed record AudioOutputConfiguration(
    string? Id,
    AudioOutputPurpose Purpose,
    AudioDeviceMapping? Device);

internal sealed record DeckOutputAssignment(
    string? DeckId,
    string? OutputId);

internal sealed record AudioRoutingConfiguration(
    IReadOnlyList<AudioOutputConfiguration>? Outputs,
    IReadOnlyList<DeckOutputAssignment>? Decks)
{
    public static AudioRoutingConfiguration CreateDefault()
    {
        return new AudioRoutingConfiguration(
            Outputs:
            [
                new("Program 1", AudioOutputPurpose.Program, null),
                new("Program 2", AudioOutputPurpose.Program, null),
                new("Program 3", AudioOutputPurpose.Program, null),
                new("PFL", AudioOutputPurpose.Pfl, null)
            ],
            Decks:
            [
                new("Deck 1", "Program 1"),
                new("Deck 2", "Program 2"),
                new("PFL", "PFL")
            ]);
    }
}

internal static class AudioRoutingValidator
{
    public static IReadOnlyList<string> Validate(AudioRoutingConfiguration configuration)
    {
        List<string> errors = [];

        if (configuration.Outputs is null)
        {
            errors.Add("Die Ausgangsliste fehlt.");
            return errors;
        }

        if (configuration.Decks is null)
        {
            errors.Add("Die Schachtzuordnungen fehlen.");
            return errors;
        }

        Dictionary<string, AudioOutputConfiguration> outputs =
            new(StringComparer.OrdinalIgnoreCase);

        int programCount = 0;
        int pflCount = 0;

        foreach (AudioOutputConfiguration output in configuration.Outputs)
        {
            if (output is null)
            {
                errors.Add("Ein Ausgang darf nicht null sein.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(output.Id))
            {
                errors.Add("Jeder Ausgang benötigt eine ID.");
                continue;
            }

            if (!outputs.TryAdd(output.Id, output))
            {
                errors.Add($"Die Ausgangs-ID '{output.Id}' ist mehrfach vorhanden.");
            }

            switch (output.Purpose)
            {
                case AudioOutputPurpose.Program:
                    programCount++;
                    break;

                case AudioOutputPurpose.Pfl:
                    pflCount++;
                    break;

                default:
                    errors.Add($"Ausgang '{output.Id}' hat einen ungültigen Zweck.");
                    break;
            }

            AudioDeviceMapping? device = output.Device;

            if (device is null)
            {
                continue;
            }

            if (device.Backend is not AudioBackend.Wasapi and not AudioBackend.Asio)
            {
                errors.Add($"Ausgang '{output.Id}' benötigt WASAPI oder ASIO.");
            }

            if (string.IsNullOrWhiteSpace(device.DeviceId))
            {
                errors.Add($"Ausgang '{output.Id}' benötigt eine Geräte-ID.");
            }

            if (device.LeftChannel < 0 ||
                device.RightChannel < 0 ||
                device.LeftChannel == device.RightChannel)
            {
                errors.Add(
                    $"Ausgang '{output.Id}' benötigt zwei unterschiedliche, " +
                    "nicht negative Kanalnummern.");
            }
        }

        if (programCount < 3)
        {
            errors.Add("Mindestens drei Programmausgänge sind erforderlich.");
        }

        if (pflCount != 1)
        {
            errors.Add("Genau ein PFL-Ausgang ist erforderlich.");
        }

        HashSet<string> deckIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (DeckOutputAssignment deck in configuration.Decks)
        {
            if (deck is null)
            {
                errors.Add("Eine Schachtzuordnung darf nicht null sein.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(deck.DeckId))
            {
                errors.Add("Jede Schachtzuordnung benötigt eine Schacht-ID.");
                continue;
            }

            if (!deckIds.Add(deck.DeckId))
            {
                errors.Add($"Die Schacht-ID '{deck.DeckId}' ist mehrfach vorhanden.");
            }

            bool isPflDeck = string.Equals(deck.DeckId, "PFL", StringComparison.OrdinalIgnoreCase);
            AudioOutputPurpose requiredPurpose = isPflDeck ? AudioOutputPurpose.Pfl : AudioOutputPurpose.Program;
            if (string.IsNullOrWhiteSpace(deck.OutputId) ||
                !outputs.TryGetValue(deck.OutputId, out AudioOutputConfiguration? output) ||
                output.Purpose != requiredPurpose)
            {
                errors.Add(
                    $"Schacht '{deck.DeckId}' muss einem vorhandenen " +
                    $"{requiredPurpose}-Ausgang zugeordnet sein.");
            }
        }

        if (!deckIds.Contains("Deck 1") || !deckIds.Contains("Deck 2") || !deckIds.Contains("PFL"))
        {
            errors.Add("Die Zuordnungen für 'Deck 1', 'Deck 2' und 'PFL' sind erforderlich.");
        }

        return errors;
    }
}

internal sealed class AudioRoutingStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly object sync = new();
    private readonly string path;
    private AudioRoutingConfiguration configuration;

    public AudioRoutingStore(string path)
    {
        this.path = path;
        configuration = Load();
    }

    public AudioRoutingConfiguration Get()
    {
        lock (sync)
        {
            return configuration;
        }
    }

    public void Save(AudioRoutingConfiguration newConfiguration)
    {
        IReadOnlyList<string> errors = AudioRoutingValidator.Validate(newConfiguration);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }

        lock (sync)
        {
            string? directory = Path.GetDirectoryName(path);

            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("Das Konfigurationsverzeichnis fehlt.");
            }

            Directory.CreateDirectory(directory);

            string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

            try
            {
                string json = JsonSerializer.Serialize(newConfiguration, SerializerOptions);
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, path, overwrite: true);
                configuration = newConfiguration;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private AudioRoutingConfiguration Load()
    {
        if (!File.Exists(path))
        {
            return AudioRoutingConfiguration.CreateDefault();
        }

        string json = File.ReadAllText(path);

        AudioRoutingConfiguration loaded =
            JsonSerializer.Deserialize<AudioRoutingConfiguration>(json, SerializerOptions)
            ?? throw new InvalidOperationException("Die Audiokonfiguration ist leer.");

        if (loaded.Decks is not null && !loaded.Decks.Any(deck =>
            string.Equals(deck.DeckId, "PFL", StringComparison.OrdinalIgnoreCase)))
        {
            loaded = loaded with { Decks = [.. loaded.Decks, new DeckOutputAssignment("PFL", "PFL")] };
        }

        IReadOnlyList<string> errors = AudioRoutingValidator.Validate(loaded);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Die Audiokonfiguration ist ungültig: {string.Join(" ", errors)}");
        }

        return loaded;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        options.Converters.Add(
            new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));

        return options;
    }
}