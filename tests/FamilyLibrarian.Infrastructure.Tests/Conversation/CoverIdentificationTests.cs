using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.ModelTesting;
using FamilyLibrarian.Infrastructure.Tests.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>M4-5: a cover photo is size- and type-checked without being decoded, read by an optional vision role, and verified against the real catalog.</summary>
[TestClass]
public sealed class CoverIdentificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    // ---- synthetic, header-only images (no pixel data is ever decoded) -------------------------------------------------

    private static byte[] Pad(byte[] head, int total = 300) => [.. head, .. new byte[Math.Max(0, total - head.Length)]];

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        return Pad(bytes);
    }

    private static byte[] Jpeg(int width, int height)
    {
        byte[] head =
        [
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
            0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3
        ];
        return Pad(head);
    }

    private static byte[] WebPLossless(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8L"u8.CopyTo(bytes.AsSpan(12));
        bytes[20] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), (uint)((width - 1) | ((height - 1) << 14)));
        return Pad(bytes);
    }

    private static byte[] WebPExtended(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8X"u8.CopyTo(bytes.AsSpan(12));
        var w = width - 1;
        var h = height - 1;
        bytes[24] = (byte)w; bytes[25] = (byte)(w >> 8); bytes[26] = (byte)(w >> 16);
        bytes[27] = (byte)h; bytes[28] = (byte)(h >> 8); bytes[29] = (byte)(h >> 16);
        return Pad(bytes);
    }

    [TestMethod]
    public void SupportedImagesAreRecognisedFromTheirBytesAndTheirSizeIsRead()
    {
        var png = CoverImageInspector.Inspect(Png(600, 900), "image/png");
        var jpeg = CoverImageInspector.Inspect(Jpeg(800, 1200), "image/jpeg");
        var jpegJpg = CoverImageInspector.Inspect(Jpeg(800, 1200), "image/jpg");
        var webp = CoverImageInspector.Inspect(WebPLossless(640, 960), null);
        var webpX = CoverImageInspector.Inspect(WebPExtended(1000, 1500), "image/webp");

        Assert.IsTrue(png.IsValid, png.Reason);
        Assert.AreEqual((600, 900, "image/png"), (png.Width, png.Height, png.MimeType));
        Assert.IsTrue(jpeg.IsValid, jpeg.Reason);
        Assert.AreEqual((800, 1200), (jpeg.Width, jpeg.Height));
        Assert.IsTrue(jpegJpg.IsValid);
        Assert.IsTrue(webp.IsValid, webp.Reason);
        Assert.AreEqual((640, 960), (webp.Width, webp.Height));
        Assert.AreEqual((1000, 1500), (webpX.Width, webpX.Height));
    }

    [TestMethod]
    public void WrongTypeSizeOrDimensionsAreRefusedBeforeAnyModelSeesThem()
    {
        Assert.IsFalse(CoverImageInspector.Inspect(null, null).IsValid);
        Assert.IsFalse(CoverImageInspector.Inspect([1, 2, 3], "image/png").IsValid);
        Assert.IsFalse(CoverImageInspector.Inspect(Pad(Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")), "image/svg+xml").IsValid);
        Assert.IsFalse(CoverImageInspector.Inspect(Pad(Encoding.ASCII.GetBytes("GIF89a")), "image/gif").IsValid);
        Assert.IsFalse(CoverImageInspector.Inspect(Pad(Encoding.ASCII.GetBytes("%PDF-1.7")), "application/pdf").IsValid);
        Assert.IsFalse(CoverImageInspector.Inspect(Png(600, 900), "image/jpeg").IsValid, "A label that disagrees with the bytes is refused.");
        Assert.IsFalse(CoverImageInspector.Inspect(Png(10, 10), "image/png").IsValid, "Too small to read.");
        Assert.IsFalse(CoverImageInspector.Inspect(Png(20_000, 20_000), "image/png").IsValid, "A tiny file claiming a huge picture is refused.");
        Assert.IsFalse(CoverImageInspector.Inspect(Png(4_096, 4_096), "image/png").IsValid, "Over the pixel budget.");
        Assert.IsFalse(CoverImageInspector.Inspect(Jpeg(5_000, 100), "image/jpeg").IsValid);
        Assert.IsFalse(CoverImageInspector.Inspect(Pad([0xFF, 0xD8, 0xFF, 0xE0]), "image/jpeg").IsValid, "No readable size means no.");
        Assert.IsFalse(CoverImageInspector.Inspect(new byte[ModelExecutionPolicy.MaxImageBytes + 1], "image/png").IsValid);
        Assert.IsFalse(CoverImageInspector.Inspect(Pad([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]), "image/png").IsValid, "A PNG with no header chunk.");
    }

    [TestMethod]
    public void AMalformedJpegCannotMakeTheScannerLoop()
    {
        var hostile = Pad([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02], 2_000);
        for (var index = 6; index + 4 < hostile.Length; index += 4)
        {
            hostile[index] = 0xFF;
            hostile[index + 1] = 0xE1;
            hostile[index + 2] = 0x00;
            hostile[index + 3] = 0x02;
        }

        var started = DateTime.UtcNow;
        var check = CoverImageInspector.Inspect(hostile, "image/jpeg");

        Assert.IsFalse(check.IsValid);
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    // ---- strict reader -------------------------------------------------------------------------------------------------

    private static string HintJson(string legible = "true", string title = "\"The Light Between Oceans\"", string author = "\"M. L. Stedman\"", string schema = "cover-identification/1") =>
        $"{{\"schema\":\"{schema}\",\"legible\":{legible},\"title\":{title},\"author\":{author}}}";

    [TestMethod]
    public void TheCoverReadingIsAcceptedOnlyInItsExactShape()
    {
        Assert.AreEqual("The Light Between Oceans", CoverHintParser.Parse(HintJson()).Value!.Title);
        Assert.IsTrue(CoverHintParser.Parse(HintJson("false", "null", "null")).IsValid);
        Assert.IsNull(CoverHintParser.Parse(HintJson("false", "null", "null")).Value, "An unreadable cover yields no hint at all.");

        string[] bad =
        [
            "", "nope", "[]", "```json\n" + HintJson() + "\n```",
            HintJson(schema: "cover-identification/2"), HintJson("\"yes\""), HintJson("true", "null"),
            HintJson("false", "\"Guess\"", "null"), HintJson(title: "\"<img src=x>\""), HintJson(title: "\"https://example.test/x\""),
            HintJson().Replace("\"legible\"", "\"confidence\":0.9,\"legible\"", StringComparison.Ordinal),
            HintJson(title: "7")
        ];
        foreach (var content in bad)
        {
            Assert.IsFalse(CoverHintParser.Parse(content).IsValid, content);
        }
    }

    // ---- the service ---------------------------------------------------------------------------------------------------

    private static (CoverIdentificationService Service, ScriptedModel Model, FakeTools Tools, FakeLedger Ledger) Make(
        Func<ModelConnection, ModelRequest, ModelResponse> handler, bool cloud = false, bool excerpts = false, bool roleEnabled = true)
    {
        var model = new ScriptedModel(handler);
        var ledger = new FakeLedger();
        var tools = new FakeTools();
        var settings = new ModelGatewaySettings(Now);
        settings.Configure("http://litellm.example.test:4000", cloud, null, Now);
        settings.SetAllowCloudExcerpts(excerpts, null, Now);
        settings.ConfigureRole(ModelRole.VisionCoverIdentification, roleEnabled, cloud ? ModelPlacement.Cloud : ModelPlacement.Local, "local/vision", "cloud/vision", null, Now);
        if (cloud) settings.ConfigureLimits(ModelExecutionLimits.Default with { CloudDailyRequests = 10, CloudDailyTokens = 200_000 }, null, Now);
        settings.SetEnabled(true, null, Now);
        var policy = new ModelExecutionPolicy(
            new ModelRoleResolver(new FakeGatewayStore { Settings = settings }, new PassThroughProtector()),
            model, ledger, new ModelExecutionGate(), new ModelCircuitBreaker(), new MutableClock(Now));
        return (new CoverIdentificationService(policy, tools), model, tools, ledger);
    }

    [TestMethod]
    public async Task OnlyRealCatalogRecordsAreEverReturnedForAReadableCover()
    {
        var (service, model, tools, ledger) = Make((_, _) => ScriptedModel.Ok(HintJson(), new ModelUsage(900, 40, 940)));
        tools.Results["The Light Between Oceans M. L. Stedman"] = new ConversationSearchResult([FakeTools.Choice("lbo", "The Light Between Oceans", "M. L. Stedman")], false, []);
        var user = Guid.NewGuid();

        var result = await service.IdentifyAsync(user, Png(600, 900), "image/png", CancellationToken.None);

        Assert.AreEqual(CoverIdentificationStatus.Identified, result.Status);
        Assert.AreEqual("lbo", result.Choices.Single().ExternalId);
        Assert.IsTrue(tools.Calls.Contains("search:The Light Between Oceans M. L. Stedman"));
        var request = model.Calls.Single().Request;
        Assert.IsNull(request.Tools);
        StringAssert.Contains(request.Messages[0].Content, "not instructions");
        Assert.AreEqual(1, request.Messages[1].Images!.Count);
        Assert.AreEqual("image/png", request.Messages[1].Images![0].MimeType);
        Assert.AreEqual(user, ledger.Records.Single().UserId);
        Assert.AreEqual(ModelRole.VisionCoverIdentification, ledger.Records.Single().Role);
        Assert.IsTrue(ledger.Records.Single().ChargedTokens >= 940 || !ledger.Records.Single().UsageEstimated);
    }

    [TestMethod]
    public async Task ACoverTheCatalogDoesNotKnowIsNotIdentifiedNoMatterWhatTheModelSays()
    {
        var (service, _, tools, _) = Make((_, _) => ScriptedModel.Ok(HintJson(title: "\"A Book Nobody Wrote\"", author: "null")));

        var result = await service.IdentifyAsync(Guid.NewGuid(), Jpeg(800, 1200), "image/jpeg", CancellationToken.None);

        Assert.AreEqual(CoverIdentificationStatus.NotIdentified, result.Status);
        Assert.AreEqual(0, result.Choices.Count, "The model's reading is never surfaced as an identification.");
        Assert.IsTrue(tools.Calls.Contains("search:A Book Nobody Wrote"));
    }

    [TestMethod]
    public async Task AnIllegibleCoverStopsBeforeAnySearch()
    {
        var (service, _, tools, _) = Make((_, _) => ScriptedModel.Ok(HintJson("false", "null", "null")));

        var result = await service.IdentifyAsync(Guid.NewGuid(), Png(600, 900), "image/png", CancellationToken.None);

        Assert.AreEqual(CoverIdentificationStatus.NotIdentified, result.Status);
        Assert.AreEqual(0, tools.Calls.Count);
    }

    [TestMethod]
    public async Task AnInvalidPictureNeverReachesTheModel()
    {
        var (service, model, _, ledger) = Make((_, _) => ScriptedModel.Ok(HintJson()));

        var result = await service.IdentifyAsync(Guid.NewGuid(), Png(10, 10), "image/png", CancellationToken.None);

        Assert.AreEqual(CoverIdentificationStatus.InvalidImage, result.Status);
        Assert.AreEqual(0, model.Calls.Count);
        Assert.AreEqual(0, ledger.Records.Count);
    }

    [TestMethod]
    public async Task ADisabledRoleBadReplyOrUnsupportedModelIsSimplyUnavailable()
    {
        var disabled = Make((_, _) => ScriptedModel.Ok(HintJson()), roleEnabled: false);
        var garbage = Make((_, _) => ScriptedModel.Ok("It is The Hobbit."));
        var textOnlyModel = Make((_, _) => ScriptedModel.Fail(ModelFailureKind.UnsupportedCapability, "HTTP 400: model does not support image input"));
        var empty = Make((_, _) => ScriptedModel.Ok(""));

        foreach (var (service, _, tools, _) in new[] { disabled, garbage, textOnlyModel, empty })
        {
            var result = await service.IdentifyAsync(Guid.NewGuid(), Png(600, 900), "image/png", CancellationToken.None);
            Assert.AreEqual(CoverIdentificationStatus.Unavailable, result.Status);
            Assert.AreEqual(0, tools.Calls.Count, "Nothing is looked up from an answer that was not accepted.");
        }

        Assert.AreEqual(0, disabled.Model.Calls.Count);
    }

    [TestMethod]
    public async Task ACloudVisionRoleNeverReceivesAPictureWithoutTheSeparateOptIn()
    {
        var withoutOptIn = Make((_, _) => ScriptedModel.Ok(HintJson()), cloud: true, excerpts: false);
        var withOptIn = Make((_, _) => ScriptedModel.Ok(HintJson(), new ModelUsage(900, 40, 940)), cloud: true, excerpts: true);
        withOptIn.Tools.Results["The Light Between Oceans M. L. Stedman"] = new ConversationSearchResult([FakeTools.Choice("lbo", "The Light Between Oceans")], false, []);

        var refused = await withoutOptIn.Service.IdentifyAsync(Guid.NewGuid(), Png(600, 900), "image/png", CancellationToken.None);
        var allowed = await withOptIn.Service.IdentifyAsync(Guid.NewGuid(), Png(600, 900), "image/png", CancellationToken.None);

        Assert.AreEqual(CoverIdentificationStatus.Unavailable, refused.Status);
        Assert.AreEqual(0, withoutOptIn.Model.Calls.Count, "The picture was never sent.");
        Assert.AreEqual(CoverIdentificationStatus.Identified, allowed.Status);
        Assert.AreEqual("cloud/vision", withOptIn.Model.Calls.Single().Connection.Model);
    }

    [TestMethod]
    public async Task ThePolicyLimitsImagesToOneBoundedPicturePerCallWhoeverTheCaller()
    {
        var (_, model, _, ledger) = Make((_, _) => new ModelResponse(true, ModelFailureKind.None, null, HintJson(), [], "stop", "m", null, 5));
        var settings = new ModelGatewaySettings(Now);
        settings.Configure("http://litellm.example.test:4000", false, null, Now);
        settings.ConfigureRole(ModelRole.VisionCoverIdentification, true, ModelPlacement.Local, "local/vision", null, null, Now);
        settings.SetEnabled(true, null, Now);
        var policy = new ModelExecutionPolicy(
            new ModelRoleResolver(new FakeGatewayStore { Settings = settings }, new PassThroughProtector()),
            model, ledger, new ModelExecutionGate(), new ModelCircuitBreaker(), new MutableClock(Now));
        var picture = new ModelImage("image/png", Png(600, 900));

        ModelExecutionRequest Request(params ModelImage[] images) => new(
            ModelRole.VisionCoverIdentification, ModelBudgetClass.InteractiveChat, Guid.NewGuid(),
            [new ModelChatMessage("user", "Read it.", images)]);

        var two = await policy.ExecuteAsync(Request(picture, picture), text => ModelParse.Ok(text), CancellationToken.None);
        var huge = await policy.ExecuteAsync(Request(new ModelImage("image/png", new byte[ModelExecutionPolicy.MaxImageBytes + 1])), text => ModelParse.Ok(text), CancellationToken.None);
        var one = await policy.ExecuteAsync(Request(picture), text => ModelParse.Ok(text), CancellationToken.None);

        Assert.AreEqual(ModelCallOutcome.InputTooLarge, two.Outcome);
        Assert.AreEqual(ModelCallOutcome.InputTooLarge, huge.Outcome);
        Assert.IsTrue(one.Succeeded);
        Assert.AreEqual(1, model.Calls.Count);
        Assert.IsTrue(ledger.Records.Single().ChargedTokens >= 1_500, "With no usage reported, an image is charged a generous reserved cost.");
    }

    // ---- the wire format -----------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheOpenAiClientSendsAnImageAsAContentPartAndPlainMessagesAsPlainText()
    {
        string? sent = null;
        var handler = new CapturingHandler(body => sent = body);
        var client = new OpenAiCompatibleModelClient(new SingleClientFactory(handler));
        var request = new ModelRequest(
            [new ModelChatMessage("system", "Be brief."), new ModelChatMessage("user", "Read the cover.", [new ModelImage("image/png", [1, 2, 3, 4])])], 120, true);

        var response = await client.CompleteAsync(
            new ModelConnection(new Uri("http://litellm.example.test:4000"), null, "local/vision", TimeSpan.FromSeconds(5)), request, CancellationToken.None);

        Assert.IsTrue(response.Succeeded);
        var messages = JsonNode.Parse(sent!)!["messages"]!.AsArray();
        Assert.AreEqual("Be brief.", (string?)messages[0]!["content"]);
        var parts = messages[1]!["content"]!.AsArray();
        Assert.AreEqual("text", (string?)parts[0]!["type"]);
        Assert.AreEqual("image_url", (string?)parts[1]!["type"]);
        Assert.AreEqual("data:image/png;base64,AQIDBA==", (string?)parts[1]!["image_url"]!["url"]);
    }

    [TestMethod]
    public async Task AnUnsupportedImageRejectionIsClassifiedAsAnUnsupportedCapability()
    {
        var handler = new CapturingHandler(_ => { }, HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"This model does not support image input (vision).\"}}");
        var client = new OpenAiCompatibleModelClient(new SingleClientFactory(handler));

        var response = await client.CompleteAsync(
            new ModelConnection(new Uri("http://litellm.example.test:4000"), null, "local/text-only", TimeSpan.FromSeconds(5)),
            new ModelRequest([new ModelChatMessage("user", "Read.", [new ModelImage("image/png", [1])])], 50), CancellationToken.None);

        Assert.IsFalse(response.Succeeded);
        Assert.AreEqual(ModelFailureKind.UnsupportedCapability, response.Failure);
    }

    private sealed class CapturingHandler(Action<string> capture, HttpStatusCode status = HttpStatusCode.OK, string? body = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    body ?? "{\"model\":\"m\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
