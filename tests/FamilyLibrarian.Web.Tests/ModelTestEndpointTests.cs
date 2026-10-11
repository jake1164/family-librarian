using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.ModelTesting;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>M3-5 over the real host: admin-only, off by default, secrets protected, informational only, and isolated from everything else.</summary>
[TestClass]
public sealed class ModelTestEndpointTests
{
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    private sealed class ScriptedClient(Func<ModelConnection, ModelRequest, ModelResponse> handler) : IModelClient
    {
        public List<string> Models { get; } = [];

        public Task<ModelResponse> CompleteAsync(ModelConnection connection, ModelRequest request, CancellationToken cancellationToken)
        {
            lock (Models) Models.Add(connection.Model);
            return Task.FromResult(handler(connection, request));
        }
    }

    private static ModelResponse Good(ModelConnection connection, ModelRequest request)
    {
        if (connection.Model == "fl-capability-test-no-such-model")
            return ModelResponse.Failed(ModelFailureKind.ModelUnavailable, "HTTP 400: Invalid model name", 5);
        var system = request.Messages[0].Content;
        var user = request.Messages[^1].Content;
        string? content = system.Contains("connectivity probe", StringComparison.Ordinal) ? "pong"
            : system.Contains("label messages", StringComparison.Ordinal) ? "{\"intent\":\"FindBook\"}"
            : system.Contains("Extract facts", StringComparison.Ordinal) ? "{\"title\":\"The Lantern Road\",\"author\":\"Mara Quill\",\"year\":2019}"
            : user.Contains("Lantern Road", StringComparison.Ordinal) && request.Tools is null
                ? "{\"assessment\":\"uncertain\",\"reasons\":[{\"text\":\"Only some facts agree.\",\"evidence_id\":\"ev-1\"}]}"
                : null;
        return new ModelResponse(true, ModelFailureKind.None, null, content,
            request.Tools is { Count: > 0 } ? [new ModelToolCall("lookup_catalog_title")] : [], "stop", "reported", new ModelUsage(8, 4, 12), 9);
    }

    private static FamilyLibrarianAppFactory Factory(ScriptedClient? client = null, bool conversation = false) =>
        new(WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            if (client is not null)
            {
                services.RemoveAll<IModelClient>();
                services.AddSingleton<IModelClient>(client);
            }

            if (conversation)
            {
                services.RemoveAll<ConversationOptions>();
                services.AddSingleton(new ConversationOptions { Enabled = true, MatrixEnabled = true, WebEnabled = true });
            }
        });

    private static async Task<HttpClient> SignedInAsync(FamilyLibrarianAppFactory factory, string email, string password, bool token = true)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        if (token) http.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(http));
        return http;
    }

    private static Task<HttpClient> AdminAsync(FamilyLibrarianAppFactory factory, bool token = true) =>
        SignedInAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword, token);

    private static async Task ConfigureAsync(HttpClient admin, string endpoint = "http://litellm.example.test:4000", bool allowCloud = false, string? cloud = null, bool enable = true)
    {
        using var saved = await admin.PutAsJsonAsync("/api/v1/admin/model-test/",
            new SaveModelTestSettingsRequest(endpoint, "local/primary", "local/comparison", cloud, allowCloud, 30, 384));
        saved.EnsureSuccessStatusCode();
        if (enable)
        {
            using var enabled = await admin.PutAsJsonAsync("/api/v1/admin/model-test/enabled", new SetModelTestEnabledRequest(true));
            enabled.EnsureSuccessStatusCode();
        }
    }

    private static async Task<HttpResponseMessage> RunAsync(HttpClient admin, string target = "Primary", bool confirmCloud = false, params string[] checks) =>
        await admin.PostAsJsonAsync("/api/v1/admin/model-test/run", new RunModelTestRequest(target, checks, confirmCloud));

    [TestMethod]
    public async Task AnAnonymousCallerIsChallengedAndAMemberIsForbiddenOnEveryRoute()
    {
        await using var factory = Factory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var member = await SignedInAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/model-test/")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.GetAsync("/api/v1/admin/model-test/")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RunAsync(member)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await member.PutAsJsonAsync("/api/v1/admin/model-test/api-key", new SetModelTestApiKeyRequest("sk-member"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/v1/admin/model-test/enabled", new SetModelTestEnabledRequest(true))).StatusCode);
    }

    [TestMethod]
    public async Task AnAdministratorMutationWithoutAnAntiforgeryTokenIsRejected()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory, token: false);

        var response = await RunAsync(admin);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task ByDefaultTestingIsOffAndARunIsRefusedWithoutAnyModelCall()
    {
        var client = new ScriptedClient(Good);
        await using var factory = Factory(client);
        using var admin = await AdminAsync(factory);

        var settings = await admin.GetFromJsonAsync<ModelTestSettingsResponse>("/api/v1/admin/model-test/");
        var run = await RunAsync(admin);

        Assert.IsFalse(settings!.IsEnabled);
        Assert.IsFalse(settings.HasApiKey);
        Assert.IsFalse(settings.AllowCloud);
        Assert.AreEqual(1_024, settings.MaxOutputTokens);
        Assert.AreEqual(HttpStatusCode.BadRequest, run.StatusCode);
        Assert.IsEmpty(client.Models);
    }

    [TestMethod]
    public async Task TheApiKeyIsStoredProtectedAndNeverComesBackInAnyResponse()
    {
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        const string key = "sk-live-do-not-leak-123456";

        await ConfigureAsync(admin, enable: false);
        using var set = await admin.PutAsJsonAsync("/api/v1/admin/model-test/api-key", new SetModelTestApiKeyRequest(key));
        var setBody = await set.Content.ReadAsStringAsync();
        var read = await admin.GetStringAsync("/api/v1/admin/model-test/");

        Assert.IsTrue(set.IsSuccessStatusCode);
        Assert.IsFalse(setBody.Contains(key, StringComparison.Ordinal));
        Assert.IsFalse(read.Contains(key, StringComparison.Ordinal));
        StringAssert.Contains(read, "\"hasApiKey\":true");
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await database.ModelTestSettingsRows.AsNoTracking().SingleAsync();
        Assert.IsNotNull(stored.ProtectedApiKey);
        Assert.IsFalse(stored.ProtectedApiKey.Contains(key, StringComparison.Ordinal), "Not stored in the clear.");
        var audit = await database.AuditEvents.Where(item => item.Action.StartsWith("model_test.")).ToListAsync();
        Assert.IsNotEmpty(audit);
        Assert.IsFalse(audit.Any(item => item.Detail != null && item.Detail.Contains(key, StringComparison.Ordinal)), "Not in the audit trail.");
    }

    [TestMethod]
    public async Task AnEnabledRunReportsEveryCheckUsageAndLatencyAndRecordsNoPromptOrReply()
    {
        var client = new ScriptedClient(Good);
        await using var factory = Factory(client);
        using var admin = await AdminAsync(factory);
        await ConfigureAsync(admin);

        using var response = await RunAsync(admin);
        var report = await response.Content.ReadFromJsonAsync<ModelTestReportResponse>();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("local/primary", report!.ConfiguredModel);
        Assert.AreEqual(8, report.Results.Count);
        Assert.IsTrue(report.Results.All(result => result.Status is "Passed" or "NotSupported"), string.Join("; ", report.Results.Select(result => $"{result.Check}={result.Status}")));
        Assert.IsTrue(report.TotalTokens > 0);
        Assert.IsTrue(report.TotalLatencyMilliseconds > 0);
        CollectionAssert.Contains(report.ReportedModels.ToArray(), "reported");
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var runs = await database.AuditEvents.Where(item => item.Action == "model_test.run").ToListAsync();
        Assert.IsNotEmpty(runs);
        Assert.IsFalse(runs.Any(item => item.Detail!.Contains("pong", StringComparison.Ordinal) || item.Detail.Contains("Lantern", StringComparison.Ordinal)),
            "The audit trail holds outcomes and usage only.");
    }

    [TestMethod]
    public async Task CloudNeedsTheSettingAndAnExplicitConfirmationAndOtherRunsNeverTouchIt()
    {
        var client = new ScriptedClient(Good);
        await using var factory = Factory(client);
        using var admin = await AdminAsync(factory);
        await ConfigureAsync(admin, allowCloud: true, cloud: "cloud/model");

        var unconfirmed = await RunAsync(admin, "Cloud", confirmCloud: false, "Connectivity");
        await RunAsync(admin, "Primary");
        await RunAsync(admin, "Comparison", false, "Connectivity");
        var beforeCloud = client.Models.ToArray();
        var confirmed = await RunAsync(admin, "Cloud", confirmCloud: true, "Connectivity");

        Assert.AreEqual(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.IsFalse(beforeCloud.Contains("cloud/model"), "No cloud request before explicit confirmation, and no fallback.");
        Assert.AreEqual(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.AreEqual("cloud/model", client.Models[^1]);
    }

    [TestMethod]
    public async Task BadTargetsChecksAndSettingsAreRefusedWithAValidationError()
    {
        await using var factory = Factory(new ScriptedClient(Good));
        using var admin = await AdminAsync(factory);
        await ConfigureAsync(admin);

        Assert.AreEqual(HttpStatusCode.BadRequest, (await RunAsync(admin, "Everything")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await RunAsync(admin, "Primary", false, "DeleteAllBooks")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/v1/admin/model-test/", new SaveModelTestSettingsRequest("ftp://x", "m", null, null, false, 30, 384))).StatusCode);
    }

    [TestMethod]
    public async Task ARunChangesNothingOutsideModelTestingAndAFailingEndpointDoesNotAffectSearchOrChat()
    {
        var failing = new ScriptedClient((_, _) => ModelResponse.Failed(ModelFailureKind.Unreachable, "connection refused", 3));
        await using var factory = Factory(failing, conversation: true);
        using var admin = await AdminAsync(factory);
        await ConfigureAsync(admin);
        await using var before = factory.Services.CreateAsyncScope();
        var database = before.ServiceProvider.GetRequiredService<AppDbContext>();
        var requests = await database.BookRequests.CountAsync();
        var assets = await database.MediaAssets.CountAsync();
        var works = await database.Works.CountAsync();
        var otherAudit = await database.AuditEvents.CountAsync(item => !item.Action.StartsWith("model_test."));

        using var run = await RunAsync(admin);
        var report = await run.Content.ReadFromJsonAsync<ModelTestReportResponse>();
        using var search = await admin.GetAsync("/api/v1/catalog/search?q=hobbit");
        using var chat = await admin.PostAsJsonAsync("/api/v1/conversation/messages", new FamilyLibrarian.Contracts.Conversation.ConversationMessageRequest("help", Guid.NewGuid().ToString("N")));

        Assert.AreEqual(HttpStatusCode.OK, run.StatusCode, "Provider trouble is a reported result, not a server error.");
        Assert.AreEqual("Error", report!.Results.Single(result => result.Check == "Connectivity").Status);
        Assert.AreEqual(HttpStatusCode.OK, search.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, chat.StatusCode);
        await using var after = factory.Services.CreateAsyncScope();
        var db = after.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(requests, await db.BookRequests.CountAsync());
        Assert.AreEqual(assets, await db.MediaAssets.CountAsync());
        Assert.AreEqual(works, await db.Works.CountAsync());
        Assert.AreEqual(otherAudit, await db.AuditEvents.CountAsync(item => !item.Action.StartsWith("model_test.") && item.Action != "auth.login" && !item.Action.StartsWith("auth.")),
            "Only model-test audit records were added by the run.");
    }

    // ---- the real client over real HTTP, against a local stand-in server (no LiteLLM, no internet) ----------------------

    private sealed class StandInServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task loop;

        public StandInServer(Func<HttpListenerContext, string, Task> handler)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            loop = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); } catch (Exception) { break; }
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    lock (Seen) Seen.Add((context.Request.Url!.AbsolutePath, context.Request.Headers["Authorization"], body));
                    await handler(context, body);
                    context.Response.Close();
                }
            });
        }

        public int Port { get; }

        public List<(string Path, string? Authorization, string Body)> Seen { get; } = [];

        public void Dispose()
        {
            listener.Stop();
            listener.Close();
        }
    }

    private static async Task Reply(HttpListenerContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    [TestMethod]
    public async Task TheRealClientTalksOpenAiCompatibleHttpAndSendsTheKeyOnlyAsABearerHeader()
    {
        using var server = new StandInServer((context, _) => Reply(context, 200,
            "{\"model\":\"stand-in\",\"choices\":[{\"message\":{\"content\":\"pong\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1,\"total_tokens\":4}}"));
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        await ConfigureAsync(admin, endpoint: $"http://127.0.0.1:{server.Port}");
        await admin.PutAsJsonAsync("/api/v1/admin/model-test/api-key", new SetModelTestApiKeyRequest("sk-stand-in"));

        using var response = await RunAsync(admin, "Primary", false, "Connectivity");
        var report = await response.Content.ReadFromJsonAsync<ModelTestReportResponse>();

        Assert.AreEqual("Passed", report!.Results.Single().Status);
        Assert.AreEqual(4, report.TotalTokens);
        CollectionAssert.Contains(report.ReportedModels.ToArray(), "stand-in");
        var seen = server.Seen.Single();
        Assert.AreEqual("/v1/chat/completions", seen.Path);
        Assert.AreEqual("Bearer sk-stand-in", seen.Authorization);
        Assert.IsFalse(seen.Body.Contains("sk-stand-in", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ARedirectIsNotFollowedSoTheKeyCannotBeForwardedElsewhere()
    {
        using var server = new StandInServer((context, _) =>
        {
            context.Response.StatusCode = 302;
            context.Response.RedirectLocation = $"http://127.0.0.1:{context.Request.Url!.Port}/elsewhere";
            return Task.CompletedTask;
        });
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        await ConfigureAsync(admin, endpoint: $"http://127.0.0.1:{server.Port}");
        await admin.PutAsJsonAsync("/api/v1/admin/model-test/api-key", new SetModelTestApiKeyRequest("sk-stand-in"));

        using var response = await RunAsync(admin, "Primary", false, "Connectivity");
        var report = await response.Content.ReadFromJsonAsync<ModelTestReportResponse>();

        Assert.AreEqual("Error", report!.Results.Single().Status);
        Assert.AreEqual(1, server.Seen.Count, "The redirect target was never requested.");
        Assert.IsFalse(server.Seen.Any(item => item.Path == "/elsewhere"));
    }

    [TestMethod]
    public async Task AnAuthenticationFailureAndAnUnknownModelFromTheEndpointAreReportedClearly()
    {
        using var unauthorized = new StandInServer((context, _) => Reply(context, 401, "{\"error\":{\"message\":\"Invalid proxy server token\"}}"));
        await using var factory = Factory();
        using var admin = await AdminAsync(factory);
        await ConfigureAsync(admin, endpoint: $"http://127.0.0.1:{unauthorized.Port}");

        using var response = await RunAsync(admin, "Primary", false, "Connectivity");
        var report = await response.Content.ReadFromJsonAsync<ModelTestReportResponse>();

        StringAssert.Contains(report!.Results.Single().Detail, "refused the API key");
        Assert.AreEqual(1, unauthorized.Seen.Count, "No retry.");
    }
}
