using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Web.Client.Authentication;
using FamilyLibrarian.Web.Client.ModelGateway;
using FamilyLibrarian.Web.Client.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Tests;

/// <summary>C1-d on screen: the period selector, the plain status with its reason, speed and tokens, and the trend and effectiveness panels.</summary>
[TestClass]
public sealed class AiInsightRenderTests
{
    private static readonly string[] ExpectedUrls =
    [
        "/api/v1/admin/model-gateway/usage?days=7", "/api/v1/admin/model-gateway/usage?days=1",
        "/api/v1/admin/model-gateway/usage/trend?weeks=26", "/api/v1/admin/model-gateway/usage/effectiveness?days=90"
    ];

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ModelUsageResponse(DateTimeOffset.UtcNow, [], null, null, 7))
            });
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters, HttpMessageHandler? handler = null) where T : IComponent
    {
        var http = new HttpClient(handler ?? new RecordingHandler()) { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<IJSRuntime, NoJs>()
            .AddSingleton(new ModelGatewayApiClient(http, new AntiforgeryTokenProvider(http))).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private static ModelGatewaySettingsResponse Settings() => new(true, null, false, null, false,
        [new("CandidateTriage", true, "Local", null, null), new("SearchGuidance", true, "Local", null, null)],
        new(30, 4000, 100, 1, new(0, 0, 0, 0), new(0, 0, 0, 0), 0, 0));

    [TestMethod]
    public async Task TheUsageTableShowsThePeriodSelectorAPlainStatusWithItsReasonAndSpeedAndTokens()
    {
        var usage = new ModelUsageResponse(DateTimeOffset.UtcNow.AddDays(-30),
            [new("CandidateTriage", "BackgroundValidation", "Succeeded", false, 25, 500, 0, "m1", null, ["CandidateVetoUsed"], ["TriageVetoEligible"]),
             new("SearchGuidance", "BackgroundValidation", "Succeeded", false, 4, 80, 0, "m2")],
            null,
            [new("CandidateTriage", "m1", false, 25, 800, 2_400, 500)], 30);

        var html = await RenderAsync<AiUsageSummary>(new() { ["Settings"] = Settings(), ["Usage"] = usage, ["Days"] = 30 });

        StringAssert.Contains(html, "last 30 days");
        StringAssert.Contains(html, "AI activity period");
        StringAssert.Contains(html, "Doing work");
        StringAssert.Contains(html, "A workflow action was applied in 25 of 25 accepted calls.");
        StringAssert.Contains(html, "Too little data");
        StringAssert.Contains(html, "4 accepted calls; at least 20 are needed");
        StringAssert.Contains(html, "Status is a fixed rule over these counts, not a quality score.");
        StringAssert.Contains(html, "Speed and tokens by model");
        StringAssert.Contains(html, "95th percentile");
        StringAssert.Contains(html, "800 ms");
        StringAssert.Contains(html, "2.4 s");
        Assert.IsFalse(html.Contains("TriageVetoEligible", StringComparison.Ordinal), "Opportunity flags are not shown as if they were results.");
    }

    [TestMethod]
    public async Task TheDefaultPeriodIsTwentyFourHoursAndTheOldWordingStillHolds()
    {
        var usage = new ModelUsageResponse(DateTimeOffset.UtcNow.AddDays(-1), [], null, null, 1);

        var html = await RenderAsync<AiUsageSummary>(new() { ["Settings"] = Settings(), ["Usage"] = usage });

        StringAssert.Contains(html, "last 24 hours");
        Assert.IsFalse(html.Contains("Speed and tokens by model", StringComparison.Ordinal), "Nothing to show without any finished call.");
    }

    [TestMethod]
    public async Task TheInsightPanelsExplainTheirLimitsAndLoadOnlyWhenAsked()
    {
        var handler = new RecordingHandler();

        var html = await RenderAsync<AiInsightPanels>(new() { ["Days"] = 7 }, handler);

        StringAssert.Contains(html, "When the AI changed something, what happened next");
        StringAssert.Contains(html, "counts decisions, not accuracy");
        StringAssert.Contains(html, "favors that model");
        StringAssert.Contains(html, "Earlier weeks (kept as counts after 90 days)");
        StringAssert.Contains(html, "No people, books or");
        Assert.IsEmpty(handler.Urls, "Nothing is fetched until an administrator asks.");
    }

    [TestMethod]
    public async Task TheApiClientAsksForTheChosenPeriodWeeksAndEffectivenessWindow()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ModelGatewayApiClient(http, new AntiforgeryTokenProvider(http));

        _ = await api.GetUsageAsync(7);
        _ = await api.GetUsageAsync();
        _ = await api.GetUsageTrendAsync(26);
        _ = await api.GetEffectivenessAsync(90);

        CollectionAssert.AreEqual(ExpectedUrls, handler.Urls);
    }
}
