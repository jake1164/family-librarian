using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Web.Client.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class AdminCandidateIdentityEvidenceRenderTests
{
    [TestMethod]
    public async Task EvidenceRendersIdentityConditionsAndUnknownPositionsAsEncodedText()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var evidence = new AdminCandidateIdentityEvidenceResponse("Match", "NeedsCompanionParts",
            "Onyx Storm", "Rebecca Yarros", "Exact", "ONYX STORM", "FirstExactLastFuzzy", "Rebecca Yaros",
            "Unknown", "ONYX STORM 2 OF 2", ["ONYX", "STORM", "2", "OF", "2"],
            [new("Unknown", "The Empyrean", null, "3")], ["<script>descriptor</script>"], [],
            ["Part 2 of 2"], ["Exact normalized title phrase."], 2, 2,
            [new("ev-title", "candidate.title.exact", "Positive", "Exact <b>normalized</b> title phrase."),
             new("ev-condition-1", "candidate.condition.companion_parts", "Condition", "Part 2 of 2")]);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<AdminCandidateIdentityEvidence>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Evidence"] = evidence }));
            return output.ToHtmlString();
        });
        StringAssert.Contains(html, "<details");
        StringAssert.Contains(html, "<summary>");
        StringAssert.Contains(html, "<dd>Match</dd>");
        StringAssert.Contains(html, "NeedsCompanionParts");
        StringAssert.Contains(html, "requested unknown, release 3");
        StringAssert.Contains(html, "&lt;script&gt;descriptor&lt;/script&gt;");
        Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
        StringAssert.Contains(html, "Evidence codes");
        StringAssert.Contains(html, "<code>candidate.title.exact</code> (Positive)");
        StringAssert.Contains(html, "<code>candidate.condition.companion_parts</code> (Condition)");
        StringAssert.Contains(html, "&lt;b&gt;normalized&lt;/b&gt;");
        Assert.IsFalse(html.Contains("<b>", StringComparison.Ordinal));
    }
}
