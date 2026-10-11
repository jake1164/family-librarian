using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Infrastructure.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.ModelTesting;

/// <summary>The OpenAI-compatible client against a fake HTTP handler: request shape, parsing, failure classification and bounds.</summary>
[TestClass]
public sealed class OpenAiCompatibleModelClientTests
{
    private const string Key = "sk-test-key-123";

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ModelConnection Connection(string endpoint = "http://litellm.example.test:4000", string? key = Key, int seconds = 5) =>
        new(new Uri(endpoint), key, "local/primary", TimeSpan.FromSeconds(seconds));

    private static ModelRequest Request(bool json = false, bool tools = false) => new(
        [new("system", "Be brief."), new("user", "ping")], 384, json,
        tools ? [new ModelToolDefinition("lookup_catalog_title", "Test only.", "{\"type\":\"object\",\"properties\":{}}")] : null);

    private const string Success =
        "{\"model\":\"qwen-reported\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"pong\"},\"finish_reason\":\"stop\"}]," +
        "\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":2,\"total_tokens\":13}}";

    private static (OpenAiCompatibleModelClient Client, Handler Handler) Make(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new Handler(respond);
        return (new OpenAiCompatibleModelClient(new Factory(handler)), handler);
    }

    [TestMethod]
    public async Task ASuccessfulCallIsParsedWithContentUsageAndModelIdentity()
    {
        var (client, handler) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, Success)));

        var response = await client.CompleteAsync(Connection(), Request(), CancellationToken.None);

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("pong", response.Content);
        Assert.AreEqual("stop", response.FinishReason);
        Assert.AreEqual("qwen-reported", response.ReportedModel);
        Assert.AreEqual(13, response.Usage!.TotalTokens);
        Assert.AreEqual(11, response.Usage.PromptTokens);
        Assert.AreEqual(1, handler.Seen.Count, "One request: no retries.");
    }

    [TestMethod]
    public async Task TheRequestIsABoundedNonStreamingChatCompletionWithBearerAuthentication()
    {
        var (client, handler) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, Success)));

        await client.CompleteAsync(Connection(), Request(json: true, tools: true), CancellationToken.None);

        var (request, body) = handler.Seen.Single();
        Assert.AreEqual("http://litellm.example.test:4000/v1/chat/completions", request.RequestUri!.ToString());
        Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
        Assert.AreEqual(Key, request.Headers.Authorization.Parameter);
        var json = JsonNode.Parse(body)!.AsObject();
        Assert.AreEqual("local/primary", (string?)json["model"]);
        Assert.AreEqual(384, (int?)json["max_tokens"]);
        Assert.AreEqual(0, (int?)json["temperature"]);
        Assert.AreEqual(false, (bool?)json["stream"]);
        Assert.AreEqual("json_object", (string?)json["response_format"]!["type"]);
        Assert.AreEqual("lookup_catalog_title", (string?)json["tools"]![0]!["function"]!["name"]);
        Assert.AreEqual("auto", (string?)json["tool_choice"]);
        Assert.IsFalse(body.Contains(Key, StringComparison.Ordinal), "The key travels in the header only.");
    }

    [TestMethod]
    public async Task NoKeyMeansNoAuthorizationHeaderAndPlainRequestsOmitOptionalFields()
    {
        var (client, handler) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, Success)));

        await client.CompleteAsync(Connection(key: null), Request(), CancellationToken.None);

        var (request, body) = handler.Seen.Single();
        Assert.IsNull(request.Headers.Authorization);
        var json = JsonNode.Parse(body)!.AsObject();
        Assert.IsNull(json["response_format"]);
        Assert.IsNull(json["tools"]);
    }

    [TestMethod]
    [DataRow("http://host:4000", "http://host:4000/v1/chat/completions")]
    [DataRow("http://host:4000/", "http://host:4000/v1/chat/completions")]
    [DataRow("http://host:4000/v1", "http://host:4000/v1/chat/completions")]
    [DataRow("https://gateway.example.test/litellm", "https://gateway.example.test/litellm/v1/chat/completions")]
    public void TheCompletionsUrlIsBuiltFromWhateverBaseTheAdministratorEntered(string endpoint, string expected) =>
        Assert.AreEqual(expected, OpenAiCompatibleModelClient.BuildUri(new Uri(endpoint)).ToString());

    [TestMethod]
    public async Task ToolCallsKeepOnlyTheirNameAndContentMayBeNull()
    {
        const string body = "{\"choices\":[{\"message\":{\"content\":null,\"tool_calls\":[{\"function\":{\"name\":\"lookup_catalog_title\",\"arguments\":\"{\\\"title\\\":\\\"x\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}";
        var (client, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, body)));

        var response = await client.CompleteAsync(Connection(), Request(tools: true), CancellationToken.None);

        Assert.IsTrue(response.Succeeded);
        Assert.IsNull(response.Content);
        Assert.AreEqual("lookup_catalog_title", response.ToolCalls.Single().Name);
        Assert.IsNull(response.Usage, "Usage is absent when the provider does not report it.");
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Invalid API key\"}}", ModelFailureKind.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden, "{}", ModelFailureKind.Unauthorized)]
    [DataRow(HttpStatusCode.NotFound, "{}", ModelFailureKind.ModelUnavailable)]
    [DataRow(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Invalid model name passed in model=x\"}}", ModelFailureKind.ModelUnavailable)]
    [DataRow(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"model not found\"}}", ModelFailureKind.ModelUnavailable)]
    [DataRow(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"messages must be an array\"}}", ModelFailureKind.BadRequest)]
    [DataRow(HttpStatusCode.TooManyRequests, "{}", ModelFailureKind.RateLimited)]
    [DataRow(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}", ModelFailureKind.ServerError)]
    [DataRow(HttpStatusCode.BadGateway, "not json", ModelFailureKind.ServerError)]
    [DataRow(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"no healthy deployments for model x\"}}", ModelFailureKind.ModelUnavailable)]
    public async Task HttpFailuresAreClassifiedWithoutThrowing(HttpStatusCode status, string body, ModelFailureKind expected)
    {
        var (client, handler) = Make(_ => Task.FromResult(Json(status, body)));

        var response = await client.CompleteAsync(Connection(), Request(), CancellationToken.None);

        Assert.IsFalse(response.Succeeded);
        Assert.AreEqual(expected, response.Failure);
        StringAssert.Contains(response.FailureDetail, $"HTTP {(int)status}");
        Assert.AreEqual(1, handler.Seen.Count, "A failure is never retried.");
    }

    [TestMethod]
    public async Task AnErrorMessageThatEchoesTheKeyIsRedacted()
    {
        var (client, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.Unauthorized, $"{{\"error\":{{\"message\":\"bad key {Key} supplied\"}}}}")));

        var response = await client.CompleteAsync(Connection(), Request(), CancellationToken.None);

        Assert.IsFalse(response.FailureDetail!.Contains(Key, StringComparison.Ordinal));
        StringAssert.Contains(response.FailureDetail, "[redacted]");
    }

    [TestMethod]
    public async Task ANetworkFailureIsUnreachableAndItsMessageIsRedactedAndBounded()
    {
        var (client, _) = Make(_ => throw new HttpRequestException($"Connection refused {Key} {new string('x', 500)}"));

        var response = await client.CompleteAsync(Connection(), Request(), CancellationToken.None);

        Assert.AreEqual(ModelFailureKind.Unreachable, response.Failure);
        Assert.IsFalse(response.FailureDetail!.Contains(Key, StringComparison.Ordinal));
        Assert.IsLessThanOrEqualTo(201, response.FailureDetail.Length);
    }

    [TestMethod]
    public async Task AnAnswerThatNeverComesIsATimeoutNotAHangOrAnException()
    {
        var (client, _) = Make(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return Json(HttpStatusCode.OK, Success);
        });
        var connection = Connection() with { Timeout = TimeSpan.FromMilliseconds(100) };

        var response = await client.CompleteAsync(connection, Request(), CancellationToken.None);

        Assert.AreEqual(ModelFailureKind.Timeout, response.Failure);
    }

    [TestMethod]
    public async Task CancellationByTheCallerPropagates()
    {
        var (client, _) = Make(async request =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return Json(HttpStatusCode.OK, Success);
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.CompleteAsync(Connection(seconds: 60), Request(), cancellation.Token));
    }

    [TestMethod]
    [DataRow("not json at all")]
    [DataRow("{}")]
    [DataRow("{\"choices\":[]}")]
    [DataRow("{\"choices\":[{\"nomessage\":1}]}")]
    [DataRow("[1,2,3]")]
    public async Task AnUnintelligibleSuccessBodyIsAnInvalidResponse(string body)
    {
        var (client, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, body)));

        var response = await client.CompleteAsync(Connection(), Request(), CancellationToken.None);

        Assert.AreEqual(ModelFailureKind.InvalidResponse, response.Failure);
    }

    [TestMethod]
    public async Task AnOversizedReplyIsRefusedRatherThanBuffered()
    {
        var huge = "{\"choices\":[{\"message\":{\"content\":\"" + new string('a', OpenAiCompatibleModelClient.MaxResponseBytes + 10) + "\"}}]}";
        var (client, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, huge)));

        var response = await client.CompleteAsync(Connection(), Request(), CancellationToken.None);

        Assert.AreEqual(ModelFailureKind.InvalidResponse, response.Failure);
        StringAssert.Contains(response.FailureDetail, "larger than the allowed size");
    }


    private static string BodyOf((HttpRequestMessage Request, string Body) seen) => seen.Body;

    [TestMethod]
    public async Task InheritSendsNoReasoningParameterAtAll()
    {
        var (client, handler) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, Success)));

        await client.CompleteAsync(Connection(), Request(), CancellationToken.None);

        var body = JsonNode.Parse(BodyOf(handler.Seen.Single()))!.AsObject();
        Assert.IsFalse(body.ContainsKey("reasoning_effort"));
        Assert.IsFalse(body.ContainsKey("think"));
        Assert.IsFalse(body.ContainsKey("chat_template_kwargs"));
    }

    [TestMethod]
    public async Task DisabledSendsOnlyTheVerifiedReasoningEffortNone()
    {
        var (client, handler) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK, Success)));

        await client.CompleteAsync(Connection(), Request() with { Reasoning = FamilyLibrarian.Domain.ModelGateway.ModelReasoningMode.Disabled }, CancellationToken.None);

        var body = JsonNode.Parse(BodyOf(handler.Seen.Single()))!.AsObject();
        Assert.AreEqual("none", (string?)body["reasoning_effort"]);
        Assert.IsFalse(body.ContainsKey("think"));
        Assert.AreEqual(384, (int?)body["max_tokens"], "The output cap is untouched by the reasoning setting.");
    }

    [TestMethod]
    public async Task ARefusedReasoningSettingIsAClassifiedConfigurationErrorAndIsNotRetried()
    {
        var (client, handler) = Make(_ => Task.FromResult(Json(HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"Invalid parameter: reasoning_effort is not accepted by model local/primary\"}}")));

        var response = await client.CompleteAsync(
            Connection(), Request() with { Reasoning = FamilyLibrarian.Domain.ModelGateway.ModelReasoningMode.Disabled }, CancellationToken.None);

        Assert.IsFalse(response.Succeeded);
        Assert.AreEqual(ModelFailureKind.UnsupportedCapability, response.Failure);
        Assert.AreEqual(1, handler.Seen.Count, "No automatic retry without the setting.");
    }

    [TestMethod]
    public async Task TheSameRejectionIsNotBlamedOnReasoningWhenNoneWasRequested()
    {
        var (client, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Unrecognized argument: reasoning_effort\"}}")));

        var response = await client.CompleteAsync(Connection(), Request(), CancellationToken.None);

        Assert.AreEqual(ModelFailureKind.BadRequest, response.Failure);
    }

    [TestMethod]
    [DataRow("length", true)]
    [DataRow("LENGTH", true)]
    [DataRow("max_tokens", true)]
    [DataRow("stop", false)]
    [DataRow("content_filter", false)]
    [DataRow(null, false)]
    public async Task OnlyAProviderSaidLengthStopCountsAsTruncationEvidence(string? finish, bool expected)
    {
        var finishJson = finish is null ? "null" : $"\"{finish}\"";
        var (client, _) = Make(_ => Task.FromResult(Json(HttpStatusCode.OK,
            "{\"model\":\"m\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning_content\":\"hidden thought\"},\"finish_reason\":" + finishJson + "}]," +
            "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":32,\"total_tokens\":33}}")));

        var response = await client.CompleteAsync(Connection(), Request(), CancellationToken.None);

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual(expected, response.IsOutputLimited);
        Assert.AreEqual(string.Empty, response.Content, "Hidden reasoning text is never surfaced as the answer.");
        Assert.AreEqual(32, response.Usage!.CompletionTokens, "Reasoning tokens stay in the reported usage.");
    }

    [TestMethod]
    public async Task AnOllamaStyleThinkValueRefusalIsAConfigurationErrorNotAnUnknownModel()
    {
        // Observed from a real LiteLLM gateway in front of Ollama: it names the model group and says "invalid", which used to
        // look like a missing model alias.
        var refusal = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = "litellm.BadRequestError: Ollama_chatException - invalid think value: \"none\" (must be high, medium, low, true, or false) " +
                              "LiteLLM: model group 'local/primary' failed with the error above. No fallback was attempted."
            }
        }.ToJsonString();
        var (client, handler) = Make(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, refusal)));

        var disabled = await client.CompleteAsync(
            Connection(), Request() with { Reasoning = FamilyLibrarian.Domain.ModelGateway.ModelReasoningMode.Disabled }, CancellationToken.None);
        var inherit = await client.CompleteAsync(Connection(), Request(), CancellationToken.None);

        Assert.AreEqual(ModelFailureKind.UnsupportedCapability, disabled.Failure, "We asked for no reasoning and the model refused that setting.");
        Assert.AreEqual(ModelFailureKind.ModelUnavailable, inherit.Failure, "The identical text without our setting keeps its old meaning.");
        Assert.AreEqual(2, handler.Seen.Count, "One request per call; nothing was retried.");
    }
}
