using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Web.Client.Providers;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Provider-authored issue text must reach the admin page as inert text.
/// Rendered with the framework's <see cref="HtmlRenderer"/> (no extra test dependency).
/// </summary>
[TestClass]
public sealed class ProviderHealthIssueListRenderTests
{
    private static async Task<string> RenderAsync(IReadOnlyList<ExternalProviderHealthIssueResponse> issues)
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ProviderHealthIssueList>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Issues"] = issues }));
            return output.ToHtmlString();
        });
    }

    [TestMethod]
    public async Task ReasonsRenderAsEscapedText()
    {
        var html = await RenderAsync(
        [
            new("search", "no-indexers", "<script>alert(1)</script> & <b>bold</b>"),
            new("general", null, "Plain general reason.")
        ]);

        Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("<b>bold", StringComparison.Ordinal));
        StringAssert.Contains(html, "&lt;script&gt;alert(1)&lt;/script&gt;");
        StringAssert.Contains(html, "Search: ");
        StringAssert.Contains(html, "Plain general reason.");
    }

    [TestMethod]
    public async Task NoIssuesRendersNothing()
    {
        var html = await RenderAsync([]);
        Assert.IsFalse(html.Contains("provider-health-issues", StringComparison.Ordinal));
    }
}
