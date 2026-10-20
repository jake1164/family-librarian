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
            new("SearchGuidance", "InteractiveChat", "Pending", true, 1, 0, 1)]);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<AiUsageSummary>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Settings"] = settings, ["Usage"] = usage }));
            return output.ToHtmlString();
        });
        StringAssert.Contains(html, "Suggest additional searches");
        StringAssert.Contains(html, "Review candidates before acquisition");
        foreach (var (label, value) in new[] { ("Calls", 10), ("Succeeded", 7), ("Failed", 2), ("Pending", 1), ("Calls", 0) })
            Assert.IsTrue(Regex.IsMatch(html, $"<td[^>]*data-label=\"{label}\"[^>]*>{value}</td>"), $"Missing {label} = {value}");
        StringAssert.Contains(html, ">Off</td>");
        StringAssert.Contains(html, "last 24 hours");
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
