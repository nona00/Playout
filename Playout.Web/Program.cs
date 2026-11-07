using System.Net;
using Playout.Web;
using Microsoft.Extensions.Logging.Console;
using System.Net.Http.Headers;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
    options.SingleLine = true;
});
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

WebApplication app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-store"
});

app.MapGet("/api/sources", (WebMusicLibrary library) => Results.Ok(library.GetSources()));
app.MapPost("/api/sources", (SourceRequest request, WebMusicLibrary library) =>
    Run(() => library.AddSource(request.Path ?? string.Empty), () => Results.Ok(library.GetSources())));
app.MapDelete("/api/sources", (string path, WebMusicLibrary library) =>
    Run(() => library.RemoveSource(path), () => Results.NoContent()));
app.MapGet("/api/tracks", (WebMusicLibrary library, CancellationToken cancellationToken) =>
    Results.Ok(library.Scan(cancellationToken)));

app.MapMethods("/api/engine/{**path}", ["GET", "POST", "PUT"], async (
    string path,
    HttpContext context,
    IHttpClientFactory clientFactory,
    CancellationToken cancellationToken) =>
{
    HttpClient client = clientFactory.CreateClient("Engine");
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
        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is not null)
            context.Response.ContentType = response.Content.Headers.ContentType.ToString();
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }
    catch (HttpRequestException)
    {
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

internal sealed record SourceRequest(string? Path);
