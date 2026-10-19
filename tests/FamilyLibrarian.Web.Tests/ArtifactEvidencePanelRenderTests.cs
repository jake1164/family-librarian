using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Web.Client.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// M1-5c: the source-attested / content-not-verified label, the completeness cautions and the override callout.
/// Plain text per docs/07-ui-conventions.md, always HTML-encoded.
/// </summary>
[TestClass]
public sealed class ArtifactEvidencePanelRenderTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ModelDecisionAndShadowNotesHaveDistinctMeaningAndEncodeTheirCitations(bool decision)
    {
        var finding = new AdminArtifactSemanticFindingResponse("Succeeded", decision ? "hold" : "Inconsistent",
            [new("suggests_incomplete", "Concern", ["idv.ev-compare-length"], "Short <script>book</script>.")],
            null, "test-model", false, DateTimeOffset.UtcNow, decision);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AdminArtifactSemanticFinding>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Finding"] = finding }))).ToHtmlString());
        StringAssert.Contains(html, decision ? "AI validation review" : "advisory only");
        StringAssert.Contains(html, decision ? "cannot release or approve" : "never holds, releases, approves or rejects");
        StringAssert.Contains(html, "idv.ev-compare-length"); StringAssert.Contains(html, "&lt;script&gt;");
        Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
    }

    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    private static ArtifactEvidenceItemResponse Item(
        string kind, string finding, string dimension, string code, string polarity, string? value = null) =>
        new(kind, Guid.NewGuid(), finding, dimension, code, polarity, "test", value, null, At);

    private static ArtifactEvidenceResponse Evidence(params ArtifactEvidenceItemResponse[] items) =>
        new("Recorded", new string('a', 64), 1, items);

    private static async Task<string> RenderAsync(ArtifactEvidenceResponse? evidence, string? origin = null)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ArtifactEvidencePanel>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Evidence"] = evidence, ["Origin"] = origin }));
            return output.ToHtmlString();
        });
    }

    private static readonly ArtifactEvidenceItemResponse Attested =
        Item("identity-verification", "ev-attestation", "IdentityAttestation", "artifact.identity_attestation.manual_upload", "Weak");

    private static readonly ArtifactEvidenceItemResponse NoVerifier =
        Item("identity-verification", "ev-corroboration", "IdentityCorroboration", "artifact.identity_corroboration.unavailable", "Unknown");

    [TestMethod]
    public async Task AnAttestedFileWithNoContentCheckIsLabeledAndItsSourceIsNamed()
    {
        var html = await RenderAsync(Evidence(Attested, NoVerifier), "ManualUpload");

        StringAssert.Contains(html, "Trusted on source attestation; content not verified.");
        StringAssert.Contains(html, "Source: uploaded by an administrator");
        Assert.IsFalse(html.Contains("Administrator override", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("BuiltInProvider", "built-in provider")]
    [DataRow("ExternalProvider", "external provider")]
    [DataRow("Legacy", "recorded before source tracking existed")]
    [DataRow("Unknown", "source not established")]
    public async Task TheSourceIsDescribedInPlainLanguage(string origin, string expected)
    {
        var html = await RenderAsync(Evidence(Attested, NoVerifier), origin);

        StringAssert.Contains(html, $"Source: {expected}");
    }

    [TestMethod]
    public async Task AFileWhoseContentWasCorroboratedGetsNoContentNotVerifiedLabel()
    {
        var corroborated = Item("identity-verification", "ev-corroboration", "IdentityCorroboration", "artifact.identity_corroboration.corroborated", "Positive");

        var html = await RenderAsync(Evidence(Attested, corroborated), "ManualUpload");

        Assert.IsFalse(html.Contains("content not verified", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task AnOverriddenAssetShowsTheOverrideAndNeverClaimsToBeTrustedOnAttestation()
    {
        var html = await RenderAsync(Evidence(
            Attested, NoVerifier,
            Item("identity-override", "ev-override", "IdentityAttestation", "artifact.identity_attestation.admin_override", "Weak", "identity.hold.attested_only_external"),
            Item("identity-override", "ev-override-reason", "IdentityAttestation", "artifact.identity_attestation.admin_override_reason", "Weak", "Checked <script>alert(1)</script> by hand")),
            "ExternalProvider");

        StringAssert.Contains(html, "Administrator override.");
        StringAssert.Contains(html, "identity.hold.attested_only_external");
        StringAssert.Contains(html, "&lt;script&gt;alert(1)&lt;/script&gt;");
        Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("Trusted on source attestation", StringComparison.Ordinal),
            "A human override is not source attestation.");
    }

    [TestMethod]
    public async Task CompletenessCautionsAreShownAsPlainTextForTheTwoWarnings()
    {
        var html = await RenderAsync(Evidence(
            Item("identity-verification", "ev-toc", "Completeness", "artifact.completeness.toc_targets_missing", "Condition", "3/10"),
            Item("identity-verification", "ev-dup", "Completeness", "artifact.completeness.duplicate_reading_order_items", "Condition", "2")));

        StringAssert.Contains(html, "Caution:");
        StringAssert.Contains(html, "table-of-contents links point at files that are not in the book (3/10)");
        StringAssert.Contains(html, "listed more than once in the reading order (2)");
        Assert.IsFalse(html.Contains("mud-chip", StringComparison.Ordinal), "No status chips for evidence (docs/07).");
    }

    [TestMethod]
    public async Task ACueLessCorroboratedEpubShowsNoLabelNoCautionAndNoOverride()
    {
        var html = await RenderAsync(Evidence(
            Item("identity-verification", "ev-corroboration", "IdentityCorroboration", "artifact.identity_corroboration.corroborated", "Positive"),
            Item("identity-verification", "ev-completeness-no-cue", "Completeness", "artifact.completeness.no_cue_detected", "Unknown", "absence of cues is not proof of completeness")),
            "ExternalProvider");

        Assert.IsFalse(html.Contains("Caution:", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("content not verified", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("Administrator override", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnAssetWithNoRecordedEvidenceStillSaysNotRecorded()
    {
        var html = await RenderAsync(new ArtifactEvidenceResponse("NotRecorded", null, null, []), "Legacy");

        StringAssert.Contains(html, "NotRecorded");
        Assert.IsFalse(html.Contains("Caution:", StringComparison.Ordinal));
    }
}
