using System.Net;
using System.Text;
using System.Text.Json;
using Playout.Gui;
using Xunit;

public sealed class EngineClientTests
{
    [Fact]
    public async Task RoutingChangesOnlyTheTwoDeckOutputs()
    {
        string? submitted = null;
        TestHandler handler = new(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json("""
                    {"outputs":[{"id":"Program 1","purpose":"Program","device":null},
                    {"id":"Program 2","purpose":"Program","device":null},
                    {"id":"Program 3","purpose":"Program","device":{"backend":"Asio","deviceId":"other","leftChannel":2,"rightChannel":3}},
                    {"id":"PFL","purpose":"Pfl","device":null}],
                    "decks":[{"deckId":"Deck 1","outputId":"Program 1"},{"deckId":"Deck 2","outputId":"Program 2"}]}
                    """);
            Assert.Equal(HttpMethod.Put, request.Method);
            submitted = await request.Content!.ReadAsStringAsync();
            return Json("{}");
        });
        using EngineClient client = Client(handler);

        await client.AssignDeckOutputsAsync(new EngineDeviceMapping("Wasapi", "device", 0, 1));

        using JsonDocument document = JsonDocument.Parse(submitted!);
        JsonElement outputs = document.RootElement.GetProperty("outputs");
        Assert.Equal("device", outputs[0].GetProperty("device").GetProperty("deviceId").GetString());
        Assert.Equal("device", outputs[1].GetProperty("device").GetProperty("deviceId").GetString());
        Assert.Equal("other", outputs[2].GetProperty("device").GetProperty("deviceId").GetString());
        Assert.Equal(JsonValueKind.Null, outputs[3].GetProperty("device").ValueKind);
    }

    [Fact]
    public async Task LoadSendsFullPathAndEncodedDeckId()
    {
        TestHandler handler = new(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/api/decks/Deck%201/load", request.RequestUri!.AbsoluteUri);
            using JsonDocument document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(@"C:\Musik\Ä Lied.wav", document.RootElement.GetProperty("path").GetString());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using EngineClient client = Client(handler);
        await client.LoadAsync("Deck 1", @"C:\Musik\Ä Lied.wav");
    }

    [Fact]
    public async Task EngineValidationErrorsAreShownToCaller()
    {
        TestHandler handler = new(_ => Task.FromResult(Json("{\"errors\":[\"Fehler eins\",\"Fehler zwei\"]}", HttpStatusCode.BadRequest)));
        using EngineClient client = Client(handler);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(client.InitializeAsync);
        Assert.Contains("Fehler eins", error.Message);
        Assert.Contains("Fehler zwei", error.Message);
    }

    private static EngineClient Client(HttpMessageHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
    private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };
    private sealed class TestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
