using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Client.Catalog;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class CandidateVersionSummaryRenderTests
{
    [TestMethod]
    public async Task ReaderSeesVersionLanguageAndMissingAuthorWithoutMarkupInjection()
    {
        var candidate = new CatalogBookCandidateResponse("source", "Source", "id", "Fahrenheit 451", [], null, null, null, [], [], null, null, [], null,
            VersionLabel: "Study guide <script>", VersionDescription: "Material about the book.");
        await using var services = new ServiceCollection().AddLogging().AddMudServices().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<CandidateVersionSummary>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Candidate"] = candidate }))).ToHtmlString());
        StringAssert.Contains(html, "Study guide &lt;script&gt;");
        StringAssert.Contains(html, "Language unknown");
        StringAssert.Contains(html, "Author not reported");
        StringAssert.Contains(html, "Material about the book.");
        Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
    }
}
