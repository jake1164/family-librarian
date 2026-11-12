using System.Net;
using System.Text;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.ModelTesting;

/// <summary>The model list used by the administrator's pickers: bounded, sorted names only; a failure never blocks typing a name.</summary>
[TestClass]
public sealed class ModelCatalogTests
{
    private const string Key = "sk-list-key-456";
    private static readonly string[] SortedNames = ["Alpha", "beta", "desktop/qwen3.5:9b"];
    private static readonly string[] OnlyA = ["a"];

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeConnections(ModelTestConnectionInfo info) : IModelTestTargetSource
    {
        public Task<ModelTestConnectionInfo> GetConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(info);

        public Task<ModelTestRoleTarget?> FindRoleTargetAsync(ModelRole role, CancellationToken cancellationToken) =>
            Task.FromResult<ModelTestRoleTarget?>(null);
    }

    private sealed class FakeCatalog(ModelListResult result) : IModelCatalogClient
    {
        public int Calls { get; private set; }

        public Task<ModelListResult> ListModelsAsync(Uri endpoint, string? apiKey, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (OpenAiCompatibleModelClient Client, Handler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Handler(respond);
        return (new OpenAiCompatibleModelClient(new Factory(handler)), handler);
    }

    [TestMethod]
    [DataRow("http://litellm.example.test:4000", "http://litellm.example.test:4000/v1/models")]
    [DataRow("http://litellm.example.test:4000/", "http://litellm.example.test:4000/v1/models")]
    [DataRow("http://litellm.example.test:4000/v1", "http://litellm.example.test:4000/v1/models")]
    public void TheModelsAddressFollowsTheSameRuleAsChatCompletions(string endpoint, string expected) =>
        Assert.AreEqual(expected, OpenAiCompatibleModelClient.BuildModelsUri(new Uri(endpoint)).ToString());

    [TestMethod]
    public async Task ModelNamesAreSortedDeduplicatedAndSentWithTheKey()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"desktop/qwen3.5:9b\"},{\"id\":\"Alpha\"},{\"id\":\"desktop/qwen3.5:9b\"},{\"id\":\"beta\"}],\"object\":\"list\"}"));

        var result = await client.ListModelsAsync(new Uri("http://litellm.example.test:4000"), Key, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(SortedNames, result.ModelIds.ToArray());
        var request = handler.Seen.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
        Assert.AreEqual(Key, request.Headers.Authorization.Parameter);
    }

    [TestMethod]
    public void UnusableNamesAreDroppedAndTheListIsBounded()
    {
        var tooMany = string.Join(",", Enumerable.Range(0, OpenAiCompatibleModelClient.MaxListedModels + 25).Select(i => $"{{\"id\":\"m{i:D4}\"}}"));
        var body = "{\"data\":[{\"id\":\"\"},{\"id\":null},{\"id\":42},{\"name\":\"x\"},\"text\",{\"id\":\"bad\\u0007name\"},{\"id\":\"" + new string('a', 201) + "\"}," + tooMany + "]}";

        var result = OpenAiCompatibleModelClient.ParseModelList(body);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(OpenAiCompatibleModelClient.MaxListedModels, result.ModelIds.Count);
        Assert.IsTrue(result.ModelIds.All(id => id.StartsWith('m')));
    }

    [TestMethod]
    public async Task AReplyThatIsNotAModelListIsReportedWithoutThrowing()
    {
        var (notList, _) = Make(_ => Json(HttpStatusCode.OK, "{\"hello\":1}"));
        var (notJson, _) = Make(_ => Json(HttpStatusCode.OK, "<html>"));

        var first = await notList.ListModelsAsync(new Uri("http://x.example.test"), null, TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = await notJson.ListModelsAsync(new Uri("http://x.example.test"), null, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(ModelFailureKind.InvalidResponse, first.Failure);
        Assert.AreEqual(ModelFailureKind.InvalidResponse, second.Failure);
    }

    [TestMethod]
    public async Task ARejectedKeyIsClassifiedAndNeverEchoedBack()
    {
        var (client, _) = Make(_ => Json(HttpStatusCode.Unauthorized, $"{{\"error\":{{\"message\":\"bad key {Key}\"}}}}"));

        var result = await client.ListModelsAsync(new Uri("http://x.example.test"), Key, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(ModelFailureKind.Unauthorized, result.Failure);
        Assert.IsFalse(result.FailureDetail!.Contains(Key, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnUnreachableEndpointIsAFailureNotAnException()
    {
        var handler = new Handler(_ => throw new HttpRequestException($"connection refused {Key}"));
        var client = new OpenAiCompatibleModelClient(new Factory(handler));

        var result = await client.ListModelsAsync(new Uri("http://x.example.test"), Key, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(ModelFailureKind.Unreachable, result.Failure);
        Assert.IsFalse(result.FailureDetail!.Contains(Key, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OnlyAnAdministratorCanListModelsAndNothingIsCalledOtherwise()
    {
        var catalog = new FakeCatalog(new ModelListResult(true, ModelFailureKind.None, null, OnlyA));
        var connections = new FakeConnections(new ModelTestConnectionInfo(new Uri("http://x.example.test"), Key, false, null));

        var member = await new ModelCatalogService(connections, catalog, new FakeUser(Guid.NewGuid()), new FakeAdmin(false)).ListAsync(CancellationToken.None);
        var anonymous = await new ModelCatalogService(connections, catalog, new FakeUser(null), new FakeAdmin(true)).ListAsync(CancellationToken.None);
        var admin = await new ModelCatalogService(connections, catalog, new FakeUser(Guid.NewGuid()), new FakeAdmin(true)).ListAsync(CancellationToken.None);

        Assert.IsTrue(member.Forbidden);
        Assert.IsTrue(anonymous.Forbidden);
        Assert.IsFalse(admin.Forbidden);
        CollectionAssert.AreEqual(OnlyA, admin.Models.ToArray());
        Assert.AreEqual(1, catalog.Calls);
    }

    [TestMethod]
    public async Task WithoutAnEndpointOrOnFailureTheAnswerIsAFriendlyProblemAndAnEmptyList()
    {
        var noEndpoint = new FakeCatalog(new ModelListResult(true, ModelFailureKind.None, null, OnlyA));
        var unreachable = new FakeCatalog(ModelListResult.Failed(ModelFailureKind.Unreachable, "connection refused sk-secret"));

        var none = await new ModelCatalogService(
            new FakeConnections(new ModelTestConnectionInfo(null, null, false, "Set the endpoint first.")), noEndpoint,
            new FakeUser(Guid.NewGuid()), new FakeAdmin(true)).ListAsync(CancellationToken.None);
        var failed = await new ModelCatalogService(
            new FakeConnections(new ModelTestConnectionInfo(new Uri("http://x.example.test"), null, false, null)), unreachable,
            new FakeUser(Guid.NewGuid()), new FakeAdmin(true)).ListAsync(CancellationToken.None);

        Assert.AreEqual(0, noEndpoint.Calls);
        Assert.IsEmpty(none.Models);
        Assert.AreEqual("Set the endpoint first.", none.Problem);
        Assert.IsEmpty(failed.Models);
        StringAssert.Contains(failed.Problem, "could not be reached");
        StringAssert.Contains(failed.Problem, "still type");
        Assert.IsFalse(failed.Problem!.Contains("sk-secret", StringComparison.Ordinal), "A provider's detail text is not passed to the page.");
    }
}
