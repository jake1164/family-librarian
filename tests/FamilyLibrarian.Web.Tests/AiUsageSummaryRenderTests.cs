using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Web.Client.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor.Services;
using System.Text.RegularExpressions;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class AiUsageSummaryRenderTests
{
    [TestMethod]
    public async Task EveryConfiguredRoleIncludingZeroUseAndDisabledRolesShowsActualCallOutcomes()
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<IJSRuntime, NoJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var settings = new ModelGatewaySettingsResponse(true, null, false, null, false,
            [new("SearchGuidance", true, "Local", null, null), new("CandidateTriage", false, "Local", null, null)],
            new(30, 4000, 100, 1, new(0, 0, 0, 0), new(0, 0, 0, 0), 0, 0));
        var usage = new ModelUsageResponse(DateTimeOffset.UtcNow.AddDays(-1), [
            new("SearchGuidance", "BackgroundValidation", "Succeeded", false, 7, 90, 0),
            new("SearchGuidance", "BackgroundValidation", "Timeout", false, 2, 0, 2),
            new("SearchGuidance", "BackgroundValidation", "SchemaRejected", false, 3, 0, 3, ReasonCode: "RepeatedSearch"),
            new("SearchGuidance", "BackgroundValidation", "TransportFailure", false, 4, 0, 4, ReasonCode: "Cancelled"),
            new("SearchGuidance", "BackgroundValidation", "FutureOutcome", false, 5, 0, 5),
            new("SearchGuidance", "InteractiveChat", "Pending", true, 1, 0, 1)]);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<AiUsageSummary>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Settings"] = settings, ["Usage"] = usage }));
            return output.ToHtmlString();
        });
        StringAssert.Contains(html, "Suggest additional searches");
        StringAssert.Contains(html, "Review candidates before acquisition");
        foreach (var (label, value) in new[] { ("Calls", 22), ("Accepted replies", 7), ("Pending", 1), ("Calls", 0) })
            Assert.IsTrue(Regex.IsMatch(html, $"<td[^>]*data-label=\"{label}\"[^>]*>{value}</td>"), $"Missing {label} = {value}");
        StringAssert.Contains(html, ">Off</td>");
        StringAssert.Contains(html, "last 24 hours");
        StringAssert.Contains(html, "admin/ai-failures?role=SearchGuidance&amp;category=ProviderError");
        StringAssert.Contains(html, "2 provider errors for Suggest additional searches");
        StringAssert.Contains(html, "3 rejected replies for Suggest additional searches");
        StringAssert.Contains(html, "4 cancelled for Suggest additional searches");
        StringAssert.Contains(html, "5 unclassified for Suggest additional searches");
        StringAssert.Contains(html, "data-label=\"Rejected replies\"");
        StringAssert.Contains(html, "data-label=\"Provider errors\"");
        Assert.IsFalse(html.Contains("Unusable responses", StringComparison.Ordinal));
        StringAssert.Contains(html, "they do not mean a book failed");
    }

    [TestMethod]
    public async Task FailureTableShowsReasonsAndEscapesConfiguredModelNames()
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<IJSRuntime, NoJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<AiFailureTable>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Rows"] = new ModelCallFailureResponse[] { new(Guid.NewGuid(), "SearchGuidance", "<script>bad()</script>", false,
                    "Timeout", null, "The model did not answer within the allowed time.", DateTimeOffset.UtcNow, 30000) }
            }));
            return output.ToHtmlString();
        });
        StringAssert.Contains(html, "within the allowed time");
        StringAssert.Contains(html, "&lt;script&gt;");
        Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
        StringAssert.Contains(html, "Suggest additional searches");
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
