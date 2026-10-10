using FamilyLibrarian.Web.Client.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class AdminCandidateOriginRenderTests
{
    private static async Task<string> RenderAsync(string? summary)
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<AdminCandidateOrigin>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["ProviderName"] = "Example source", ["ProviderResultId"] = "c_123",
                    ["SourceSummary"] = summary, ["InspectionUri"] = "https://source.example.test/item/123"
                }));
            return output.ToHtmlString();
        });
    }

    [TestMethod]
    public async Task AdministratorSeesOriginAsEncodedTextAndAVisibleSafeLink()
    {
        var html = await RenderAsync("<script>origin</script> & indexer");
        StringAssert.Contains(html, "From Example source");
        StringAssert.Contains(html, "&lt;script&gt;origin&lt;/script&gt; &amp; indexer");
        Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
        StringAssert.Contains(html, "record c_123");
        StringAssert.Contains(html, "text-decoration: underline");
        StringAssert.Contains(html, "var(--mud-palette-primary)");
        StringAssert.Contains(html, "target=\"_blank\"");
        StringAssert.Contains(html, "rel=\"noopener noreferrer\"");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public async Task AbsentOriginLeavesOnlyTheExistingProvenance(string? summary)
    {
        var html = await RenderAsync(summary);
        Assert.AreEqual(1, System.Net.WebUtility.HtmlDecode(html).Split(" — ").Length - 1);
        StringAssert.Contains(html, "record c_123");
    }
}
