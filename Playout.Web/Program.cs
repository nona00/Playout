using System.Net;
using Playout.Web;
using Playout;
using System.Net.Http.Headers;
using System.Text.Json;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string logPath = PlayoutLog.GetLogPath("Web");
using TrayFileLoggerProvider fileLogger = new(logPath);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(fileLogger);
builder.WebHost.UseUrls("http://127.0.0.1:5188");
builder.Services.AddHttpClient("Engine", client =>
{
    client.BaseAddress = new Uri("http://127.0.0.1:5187/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
string sourcesPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Playout", "Gui", "music-sources.json");
builder.Services.AddSingleton(new WebMusicLibrary(sourcesPath));
string playlistSettingsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Playout", "Gui", "music-playlist-folder.json");
builder.Services.AddSingleton(new PlaylistLibrary(playlistSettingsPath));

WebApplication app = builder.Build();
using TrayHost tray = new("W", "Playout Web", logPath, app.Lifetime.StopApplication, "http://127.0.0.1:5188");
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-store"
});

app.MapGet("/api/sources", (WebMusicLibrary library) => Results.Ok(library.GetSources()));
app.MapPost("/api/sources", (SourceRequest request, WebMusicLibrary library) =>
    Run(() =>
    {
        library.AddSource(request.Path ?? string.Empty);
        app.Logger.LogInformation("Musikquelle hinzugefügt: {Path}", request.Path);
    }, () => Results.Ok(library.GetSources())));
app.MapDelete("/api/sources", (string path, WebMusicLibrary library) =>
    Run(() =>
    {
        library.RemoveSource(path);
        app.Logger.LogInformation("Musikquelle entfernt: {Path}", path);
    }, () => Results.NoContent()));
app.MapGet("/api/tracks", (WebMusicLibrary library, CancellationToken cancellationToken) =>
    Results.Ok(library.Scan(cancellationToken)));
app.MapGet("/api/playlists/folder", (PlaylistLibrary library) =>
    Run(() => { }, () => Results.Ok(new { path = library.LoadFolder() })));
app.MapPut("/api/playlists/folder", (PlaylistFolderRequest request, PlaylistLibrary library) =>
    Run(() => library.SaveFolder(request.Path), () => Results.Ok(new { path = library.LoadFolder() })));
app.MapGet("/api/playlists", (PlaylistLibrary library) =>
    Run(() => { }, () => Results.Ok(library.GetPlaylists())));
app.MapGet("/api/playlists/content", (string name, PlaylistLibrary library) =>
    Run(() => { }, () => Results.Ok(library.LoadPlaylist(name))));
app.MapGet("/api/playlists/current", (PlaylistLibrary library) =>
    Run(() => { }, () => Results.Ok(new { name = library.LoadCurrentPlaylist() })));
app.MapPut("/api/playlists/current", (PlaylistSelectionRequest request, PlaylistLibrary library) =>
    Run(() => library.SaveCurrentPlaylist(request.Name), () => Results.Ok(new { name = library.LoadCurrentPlaylist() })));

app.MapMethods("/api/engine/{**path}", ["GET", "POST", "PUT"], async (
    string path,
    HttpContext context,
    IHttpClientFactory clientFactory,
    CancellationToken cancellationToken) =>
{
    string? trackPath = null;
    if (HttpMethods.IsPost(context.Request.Method) && path.EndsWith("/load", StringComparison.OrdinalIgnoreCase))
    {
        context.Request.EnableBuffering();
        try
        {
            LoadAudioRequest? loadRequest = await context.Request.ReadFromJsonAsync<LoadAudioRequest>(cancellationToken);
            trackPath = loadRequest?.Path;
        }
        catch (System.Text.Json.JsonException)
        {
        }
        finally
        {
            if (context.Request.Body.CanSeek)
                context.Request.Body.Position = 0;
        }
    }

    HttpClient client = clientFactory.CreateClient("Engine");
    string? action = DescribeEngineAction(context.Request.Method, path, trackPath);
    if (action is not null && trackPath is null && TryGetDeckId(path, out string? deckId))
    {
        string? currentTrack = await ReadDeckTrackTitleAsync(client, deckId!, cancellationToken);
        action = DescribeEngineAction(context.Request.Method, path, currentTrack: currentTrack);
    }

    string query = context.Request.QueryString.Value ?? string.Empty;
    using HttpRequestMessage request = new(new HttpMethod(context.Request.Method), "api/" + path + query);
    if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        request.Content = new StreamContent(context.Request.Body);
        if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out MediaTypeHeaderValue? contentType))
            request.Content.Headers.ContentType = contentType;
    }
    try
    {
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (action is not null)
        {
            if (response.IsSuccessStatusCode)
                app.Logger.LogInformation("Aktion ausgeführt: {Action}", action);
            else
                app.Logger.LogWarning("Aktion fehlgeschlagen: {Action}; HTTP {StatusCode}", action, (int)response.StatusCode);
        }
        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is not null)
            context.Response.ContentType = response.Content.Headers.ContentType.ToString();
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }
    catch (HttpRequestException exception)
    {
        if (action is not null)
            app.Logger.LogError(exception, "Aktion fehlgeschlagen: {Action}", action);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { Error = "Die lokale Playout-Engine ist nicht erreichbar." }, cancellationToken);
    }
});

app.Run();

static IResult Run(Action action, Func<IResult> result)
{
    try
    {
        action();
        return result();
    }
    catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
    {
        return Results.BadRequest(new { Error = exception.Message });
    }
}

static string? DescribeEngineAction(string method, string path, string? trackPath = null, string? currentTrack = null)
{
    string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (HttpMethods.IsPut(method) && path.Equals("audio-routing", StringComparison.OrdinalIgnoreCase))
        return "Audio-Routing geändert.";
    if (!HttpMethods.IsPost(method))
        return null;
    if (path.Equals("audio/initialize", StringComparison.OrdinalIgnoreCase))
        return "Audioausgabe initialisiert.";
    if (path.Equals("audio/shutdown", StringComparison.OrdinalIgnoreCase))
        return "Audioausgabe beendet.";
    if (segments.Length < 3 || !segments[0].Equals("decks", StringComparison.OrdinalIgnoreCase))
        return null;

    string deck = Uri.UnescapeDataString(segments[1]);
    return segments[2].ToLowerInvariant() switch
    {
        "load" => $"Titel geladen: {(string.IsNullOrWhiteSpace(trackPath) ? "unbekannt" : Path.GetFileName(trackPath))} in {deck}.",
        "play" => $"Wiedergabe gestartet: {currentTrack ?? "kein Titel geladen"} ({deck}).",
        "pause" => $"Wiedergabe pausiert: {currentTrack ?? "kein Titel geladen"} ({deck}).",
        "stop" => $"Wiedergabe gestoppt: {currentTrack ?? "kein Titel geladen"} ({deck}).",
        _ => null
    };
}

static bool TryGetDeckId(string path, out string? deckId)
{
    string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length >= 3 && segments[0].Equals("decks", StringComparison.OrdinalIgnoreCase))
    {
        deckId = Uri.UnescapeDataString(segments[1]);
        return true;
    }

    deckId = null;
    return false;
}

static async Task<string?> ReadDeckTrackTitleAsync(HttpClient client, string deckId, CancellationToken cancellationToken)
{
    try
    {
        using HttpResponseMessage response = await client.GetAsync("api/audio/status", cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        JsonElement status = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!status.TryGetProperty("decks", out JsonElement decks) || decks.ValueKind != JsonValueKind.Array)
            return null;

        foreach (JsonElement deck in decks.EnumerateArray())
        {
            if (deck.TryGetProperty("deckId", out JsonElement id) &&
                string.Equals(id.GetString(), deckId, StringComparison.OrdinalIgnoreCase) &&
                deck.TryGetProperty("path", out JsonElement path) &&
                path.ValueKind == JsonValueKind.String)
            {
                string? fullPath = path.GetString();
                return fullPath is null ? null : Path.GetFileName(fullPath);
            }
        }
    }
    catch (Exception exception) when (exception is HttpRequestException or JsonException)
    {
    }

    return null;
}

internal sealed record SourceRequest(string? Path);
internal sealed record LoadAudioRequest(string? Path);
internal sealed record PlaylistFolderRequest(string? Path);
internal sealed record PlaylistSelectionRequest(string? Name);
