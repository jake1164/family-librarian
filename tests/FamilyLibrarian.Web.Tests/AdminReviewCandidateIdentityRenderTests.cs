using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Web.Client.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class AdminReviewCandidateIdentityRenderTests
{
    [TestMethod]
    [DataRow("[AUDIOBOOK] Chevy Stevens - That Night")]
    [DataRow("After That Night: Will Trent, Book 11 - Karin Slaughter - 2023")]
    [DataRow("Star Wars - The Clone Wars--No Prisoners (NMR")]
    [DataRow("<script>source</script> & another release")]
    [DataRow(null)]
    public async Task FallbackRecordsLeadWithTheActualReleaseAndNeverTheCatalogIdentity(string? release)
    {
        var html = await RenderAsync(new(Guid.NewGuid(), Guid.NewGuid(), "example-source", "opaque", "Requested Title",
            "Requested Author", "en", null, null, release, true, true));
        Assert.IsFalse(html.Contains("Requested Title", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("Requested Author", StringComparison.Ordinal));
        StringAssert.Contains(System.Net.WebUtility.HtmlDecode(html), release ?? "Unnamed source record");
        StringAssert.Contains(html, "identity still need verification");
        StringAssert.Contains(html, "Source language: en");
        Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StructuredClaimsStayDistinctFromTheirRawRelease()
    {
        var html = await RenderAsync(new(Guid.NewGuid(), Guid.NewGuid(), "example-source", "opaque", "Source Title",
            "Source Author", null, null, null, "Raw Release", false, false));
        StringAssert.Contains(html, "Source Title by Source Author");
        StringAssert.Contains(html, "Release: Raw Release");
        StringAssert.Contains(html, "does not identify the requested work");
    }

    [TestMethod]
    public async Task AnExplicitProductQuestionDoesNotClaimItsWorkIdentityIsStillUnverified()
    {
        var html = await RenderAsync(new(Guid.NewGuid(), Guid.NewGuid(), "example-source", "opaque", "Requested Title",
            null, null, null, null, "Source release", true, true), productQuestion: true);
        StringAssert.Contains(html, "separately verified work identity");
        Assert.IsFalse(html.Contains("identity still need verification", StringComparison.Ordinal));
    }

    private static async Task<string> RenderAsync(AdminRequestReviewCandidateResponse candidate, bool productQuestion = false)
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AdminReviewCandidateIdentity>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Candidate"] = candidate, ["IsProductQuestion"] = productQuestion }))).ToHtmlString());
    }
}
