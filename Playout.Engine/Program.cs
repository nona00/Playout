using System.Diagnostics;
using System.Text.Json.Serialization;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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

WebApplication app = builder.Build();

DateTimeOffset startedAt = DateTimeOffset.UtcNow;
Stopwatch uptime = Stopwatch.StartNew();

app.MapGet("/api/status", (AudioRoutingStore store) =>
{
    AudioRoutingConfiguration routing = store.Get();

    return Results.Ok(new EngineStatus(
        State: "Running",
        StartedAtUtc: startedAt,
        UptimeSeconds: (long)uptime.Elapsed.TotalSeconds,
        AudioInitialized: false,
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
    AudioRoutingStore store) =>
{
    IReadOnlyList<string> errors = AudioRoutingValidator.Validate(configuration);

    if (errors.Count > 0)
    {
        return Results.BadRequest(new { Errors = errors });
    }

    store.Save(configuration);
    return Results.Ok(configuration);
});

app.Run();

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