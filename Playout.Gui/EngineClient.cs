using System.Net.Http.Json;
using System.Text.Json;

namespace Playout.Gui;

internal sealed record EngineDevice(string Id, string Name, int NativeIndex, bool? IsEnabled, bool? IsDefault, int? SharedModeChannels, int? SharedModeSampleRate);
internal sealed record EngineBackend(string Backend, bool Available, string? Error, IReadOnlyList<EngineDevice> Devices);
internal sealed record EngineDeviceStatus(string Backend, string DeviceId, string Name, string? Error);
internal sealed record EngineDeckStatus(string DeckId, string? Path, string State, string? Error);
internal sealed record EngineAudioStatus(bool Initialized, IReadOnlyList<EngineDeviceStatus> Devices, IReadOnlyList<EngineDeckStatus> Decks);
internal sealed record EngineDeviceMapping(string Backend, string DeviceId, int LeftChannel, int RightChannel);
internal sealed record EngineOutput(string Id, string Purpose, EngineDeviceMapping? Device);
internal sealed record EngineDeckAssignment(string DeckId, string OutputId);
internal sealed record EngineRouting(IReadOnlyList<EngineOutput> Outputs, IReadOnlyList<EngineDeckAssignment> Decks);

internal sealed class EngineClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;

    public EngineClient(HttpClient client) => this.client = client;

    public Task<EngineAudioStatus> GetStatusAsync() => GetAsync<EngineAudioStatus>("api/audio/status");
    public Task<IReadOnlyList<EngineBackend>> GetDevicesAsync() => GetAsync<IReadOnlyList<EngineBackend>>("api/audio-devices");
    public Task<EngineRouting> GetRoutingAsync() => GetAsync<EngineRouting>("api/audio-routing");
    public Task InitializeAsync() => SendAsync(HttpMethod.Post, "api/audio/initialize");
    public Task ShutdownAsync() => SendAsync(HttpMethod.Post, "api/audio/shutdown");
    public Task LoadAsync(string deckId, string path) => SendAsync(HttpMethod.Post, DeckUrl(deckId, "load"), new { Path = path });
    public Task PlayAsync(string deckId) => SendAsync(HttpMethod.Post, DeckUrl(deckId, "play"));
    public Task PauseAsync(string deckId) => SendAsync(HttpMethod.Post, DeckUrl(deckId, "pause"));
    public Task StopAsync(string deckId) => SendAsync(HttpMethod.Post, DeckUrl(deckId, "stop"));

    public async Task AssignDeckOutputsAsync(EngineDeviceMapping mapping)
    {
        EngineRouting routing = await GetRoutingAsync();
        string[] outputIds = routing.Decks.Where(deck => deck.DeckId.Equals("Deck 1", StringComparison.OrdinalIgnoreCase) ||
            deck.DeckId.Equals("Deck 2", StringComparison.OrdinalIgnoreCase)).Select(deck => deck.OutputId).ToArray();
        if (outputIds.Length != 2 || outputIds.Any(id => !routing.Outputs.Any(output =>
            output.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && output.Purpose.Equals("Program", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Die Programmausgänge der beiden Decks konnten nicht gefunden werden.");

        EngineOutput[] outputs = routing.Outputs.Select(output => outputIds.Contains(output.Id, StringComparer.OrdinalIgnoreCase)
            ? output with { Device = mapping } : output).ToArray();
        await SendAsync(HttpMethod.Put, "api/audio-routing", routing with { Outputs = outputs });
    }

    private async Task<T> GetAsync<T>(string uri)
    {
        using HttpResponseMessage response = await client.GetAsync(uri);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions) ?? throw new InvalidOperationException("Die Engine lieferte eine leere Antwort.");
    }

    private async Task SendAsync(HttpMethod method, string uri, object? body = null)
    {
        using HttpRequestMessage request = new(method, uri);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        using HttpResponseMessage response = await client.SendAsync(request);
        await EnsureSuccessAsync(response);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        string message = $"Engine-Fehler {(int)response.StatusCode} ({response.ReasonPhrase}).";
        string content = await response.Content.ReadAsStringAsync();
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
                message = error.GetString() ?? message;
            else if (document.RootElement.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array)
                message = string.Join(Environment.NewLine, errors.EnumerateArray().Select(value => value.GetString()));
        }
        catch (JsonException) { }
        throw new InvalidOperationException(message);
    }

    private static string DeckUrl(string deckId, string command) => $"api/decks/{Uri.EscapeDataString(deckId)}/{command}";
    public void Dispose() => client.Dispose();
}
