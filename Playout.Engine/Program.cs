using System.Diagnostics;
using System.Text.Json.Serialization;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string logPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Playout", "Logs", "Engine.log");
using TrayFileLoggerProvider fileLogger = new(logPath);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(fileLogger);

BassNativeLibraries.Register();

// Vorläufig nur lokal erreichbar; noch ohne Authentifizierung.
builder.WebHost.UseUrls("http://127.0.0.1:5187");

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
});

string routingPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Playout",
    "Engine",
    "audio-routing.json");

AudioRoutingStore routingStore = new(routingPath);
builder.Services.AddSingleton(routingStore);
builder.Services.AddSingleton<AudioPlayback>();

WebApplication app = builder.Build();
using TrayHost tray = new("E", "Playout Engine", logPath, app.Lifetime.StopApplication);

AudioPlayback startupPlayback = app.Services.GetRequiredService<AudioPlayback>();
try
{
    startupPlayback.Initialize(routingStore);
}
catch (Exception exception)
{
    app.Logger.LogWarning(exception, "Die Audioausgabe konnte beim Start nicht initialisiert werden.");
}

DateTimeOffset startedAt = DateTimeOffset.UtcNow;
Stopwatch uptime = Stopwatch.StartNew();

app.MapGet("/api/status", (AudioRoutingStore store, AudioPlayback playback) =>
{
    AudioRoutingConfiguration routing = store.Get();

    return Results.Ok(new EngineStatus(
        State: "Running",
        StartedAtUtc: startedAt,
        UptimeSeconds: (long)uptime.Elapsed.TotalSeconds,
        AudioInitialized: playback.IsInitialized,
        Outputs: routing.Outputs!.Select(output => new AudioOutput(
            Name: output.Id!,
            Purpose: output.Purpose.ToString(),
            IsConfigured: output.Device is not null)).ToArray()));
});

app.MapGet("/api/audio-routing", (AudioRoutingStore store) =>
    Results.Ok(store.Get()));

app.MapGet("/api/audio-devices", () =>
    Results.Ok(AudioDeviceDiscovery.Discover()));

app.MapPut("/api/audio-routing", (
    AudioRoutingConfiguration configuration,
    AudioRoutingStore store,
    AudioPlayback playback) =>
{
    IReadOnlyList<string> errors = AudioRoutingValidator.Validate(configuration);

    if (errors.Count > 0)
    {
        return Results.BadRequest(new { Errors = errors });
    }

    return RunAudioCommand(() => playback.SaveRouting(store, configuration), () => Results.Ok(configuration));
});

app.MapGet("/api/decks", (AudioPlayback playback) => Results.Ok(playback.GetDecks()));
app.MapGet("/api/audio/status", (AudioPlayback playback) => Results.Ok(playback.GetStatus()));
app.MapPost("/api/audio/initialize", (AudioRoutingStore store, AudioPlayback playback) =>
    RunAudioCommand(() => playback.Initialize(store)));
app.MapPost("/api/audio/shutdown", (AudioPlayback playback) =>
    RunAudioCommand(playback.Shutdown));
app.MapPost("/api/decks/{deckId}/load", (string deckId, LoadAudioRequest request, AudioPlayback playback) =>
    RunAudioCommand(() => playback.Load(deckId, request.Path ?? string.Empty)));
app.MapPost("/api/decks/{deckId}/play", (string deckId, AudioPlayback playback) =>
    RunAudioCommand(() => playback.Play(deckId)));
app.MapPost("/api/decks/{deckId}/pause", (string deckId, AudioPlayback playback) =>
    RunAudioCommand(() => playback.Pause(deckId)));
app.MapPost("/api/decks/{deckId}/stop", (string deckId, AudioPlayback playback) =>
    RunAudioCommand(() => playback.Stop(deckId)));

app.Run();

static IResult RunAudioCommand(Action action, Func<IResult>? success = null)
{
    try
    {
        action();
        return success?.Invoke() ?? Results.NoContent();
    }
    catch (Exception exception) when (exception is InvalidOperationException or IOException or
        UnauthorizedAccessException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
    {
        return Results.BadRequest(new { Error = exception.Message });
    }
}

internal sealed record LoadAudioRequest(string? Path);

internal sealed record EngineStatus(
    string State,
    DateTimeOffset StartedAtUtc,
    long UptimeSeconds,
    bool AudioInitialized,
    IReadOnlyList<AudioOutput> Outputs);

internal sealed record AudioOutput(
    string Name,
    string Purpose,
    bool IsConfigured);