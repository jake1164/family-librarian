using System.Net;
using System.Text.Json.Nodes;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Communications;

namespace FamilyLibrarian.Infrastructure.Tests.Communications;

[TestClass]
public sealed class HttpMatrixClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task SendRichMessageAsyncSendsTheDocumentedHtmlPayloadAndParsesTheEventId()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"event_id": "$abc123"}""");
        var client = CreateClient(handler);

        var result = await client.SendRichMessageAsync(
            CreateSettings(), "token", "!room:example.test", "plain body", "<b>html body</b>", CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("$abc123", result.EventId);

        var payload = handler.RequestPayload!;
        Assert.AreEqual("m.text", payload["msgtype"]!.GetValue<string>());
        Assert.AreEqual("plain body", payload["body"]!.GetValue<string>());
        Assert.AreEqual("org.matrix.custom.html", payload["format"]!.GetValue<string>());
        Assert.AreEqual("<b>html body</b>", payload["formatted_body"]!.GetValue<string>());
        var expectedRoomSegment = Uri.EscapeDataString("!room:example.test");
        StringAssert.Contains(handler.RequestUri!.AbsolutePath, $"/rooms/{expectedRoomSegment}/send/m.room.message/");
    }

    [TestMethod]
    public async Task SendRichMessageAsyncFailureDoesNotLeakTheRequestBodyIntoTheErrorMessage()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden, """{"errcode": "M_FORBIDDEN", "error": "no"}""");
        var client = CreateClient(handler);

        var result = await client.SendRichMessageAsync(
            CreateSettings(), "token", "!room:example.test", "plain body with a secret link", "html",
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.EventId);
        StringAssert.Contains(result.Error, "M_FORBIDDEN");
        StringAssert.DoesNotMatch(
            result.Error!, new System.Text.RegularExpressions.Regex("secret link"));
    }

    [TestMethod]
    public async Task EditMessageAsyncSendsTheDocumentedMReplacePayload()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"event_id": "$edit1"}""");
        var client = CreateClient(handler);

        var result = await client.EditMessageAsync(
            CreateSettings(), "token", "!room:example.test", "$original", "new plain", "<i>new html</i>",
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);

        var payload = handler.RequestPayload!;
        Assert.AreEqual("* new plain", payload["body"]!.GetValue<string>());
        Assert.AreEqual("* <i>new html</i>", payload["formatted_body"]!.GetValue<string>());
        Assert.AreEqual("org.matrix.custom.html", payload["format"]!.GetValue<string>());

        var newContent = payload["m.new_content"]!.AsObject();
        Assert.AreEqual("new plain", newContent["body"]!.GetValue<string>());
        Assert.AreEqual("<i>new html</i>", newContent["formatted_body"]!.GetValue<string>());
        Assert.AreEqual("m.text", newContent["msgtype"]!.GetValue<string>());

        var relatesTo = payload["m.relates_to"]!.AsObject();
        Assert.AreEqual("m.replace", relatesTo["rel_type"]!.GetValue<string>());
        Assert.AreEqual("$original", relatesTo["event_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task EditMessageAsyncFailurePathReturnsTheHomeserverError()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound, """{"errcode": "M_NOT_FOUND", "error": "unknown room"}""");
        var client = CreateClient(handler);

        var result = await client.EditMessageAsync(
            CreateSettings(), "token", "!room:example.test", "$original", "plain", "html", CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "M_NOT_FOUND");
    }

    private static HttpMatrixClient CreateClient(HttpMessageHandler handler) =>
        new(new TestHttpClientFactory(handler));

    private static MatrixSettings CreateSettings()
    {
        var settings = new MatrixSettings(Now);
        settings.SetSettings("https://matrix.example.test", "@bot:example.test", null, Now);
        return settings;
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public JsonObject? RequestPayload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                RequestPayload = JsonNode.Parse(body) as JsonObject;
            }

            return new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody) };
        }
    }
}
