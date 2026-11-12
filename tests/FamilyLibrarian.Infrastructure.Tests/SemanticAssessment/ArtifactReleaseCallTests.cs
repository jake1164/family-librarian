using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;
using static FamilyLibrarian.Domain.Evidence.EvidenceReasonCodes;

namespace FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

[TestClass]
public sealed class ArtifactReleaseCallTests
{
    [TestMethod]
    [DynamicData(nameof(ModelReleaseTests.EveryCode), typeof(ModelReleaseTests))]
    public async Task EveryNegativeCodePreventsModelCallsExceptTheAuthorException(string code)
    {
        var kit = new ReleaseKit();
        var facts = ModelReleaseTests.AuthorFacts.Where(f => f.Polarity != EvidencePolarity.Negative)
            .Append(new IdentityFact("ev-negative", EvidenceDimension.IdentityCorroboration, code, EvidencePolarity.Negative)).ToArray();
        var result = await kit.SeedAsync(facts);
        await kit.Service.GetReleaseAsync(kit.Asset, result, ArtifactDecisionKit.Expected, CancellationToken.None);
        Assert.AreEqual(code == ArtifactIdentityCorroboration.AuthorDiffers ? 1 : 0, kit.Model.Calls, code);
    }

    [TestMethod]
    [DataRow("release", true)]
    [DataRow("off", false)]
    [DataRow("role-off", false)]
    [DataRow("invalid", false)]
    [DataRow("timeout", false)]
    [DataRow("error", false)]
    [DataRow("forged", false)]
    [DataRow("unknown", false)]
    public async Task OnlyEnabledGroundedReleaseProposesAFinding(string mode, bool released)
    {
        var kit = new ReleaseKit();
        if (mode == "off") kit.Settings.SetAllowModelHoldRelease(false, null, kit.Model.UtcNow);
        if (mode == "role-off") kit.Model.Enabled = false;
        if (mode is "invalid" or "timeout" or "error") kit.Model.Outcome = mode;
        if (mode == "invalid") kit.Model.Response = "not JSON";
        if (mode == "forged") kit.Model.Response = ArtifactDecisionKit.Reply("release", "supports_declared_identity", "forged");
        if (mode == "unknown") kit.Model.Response = ArtifactDecisionKit.Reply("release", "supports_declared_identity", "idv.ev-unknown");
        var result = await kit.SeedAsync([.. ModelReleaseTests.AuthorFacts,
            new("ev-unknown", EvidenceDimension.Profile, ArtifactProfile.Heading, EvidencePolarity.Unknown, "Unknown observation")]);
        Assert.AreEqual(released, await kit.Service.GetReleaseAsync(kit.Asset, result, ArtifactDecisionKit.Expected, CancellationToken.None) is not null);
        Assert.AreEqual(mode is "off" or "role-off" ? 0 : 1, kit.Model.Calls);
        Assert.AreEqual(FamilyLibrarian.Domain.Acquisition.MediaAssetStorageState.Unmatched, kit.Asset.StorageState,
            "A proposal never changes storage; only ApprovalService may trust bytes.");
    }

    [TestMethod]
    [DataRow("negative")]
    [DataRow("model-concern")]
    [DataRow("switch-off")]
    public async Task EligibilityAndPermissionAreCheckedAgainAfterTheCall(string race)
    {
        var kit = new ReleaseKit();
        kit.Model = new ArtifactDecisionKit { Response = ReleaseKit.Reply, BeforeParse = _ =>
        {
            if (race == "negative") kit.Rows.Add(new ArtifactEvidence(kit.Asset.Id, kit.Asset.Sha256,
                ArtifactEvidenceRecordKinds.TechnicalInspection, Guid.NewGuid(), "late-title", EvidenceDimension.IdentityCorroboration,
                ArtifactIdentityCorroboration.TitleDiffers, EvidencePolarity.Negative, "test", null, null, kit.Model.UtcNow));
            if (race == "model-concern") kit.Asset.SetIdentityHoldCode(IdentityHold.ModelConcern);
            if (race == "switch-off") kit.Settings.SetAllowModelHoldRelease(false, null, kit.Model.UtcNow);
        }};
        var result = await kit.SeedAsync(ModelReleaseTests.AuthorFacts);
        Assert.IsNull(await kit.Service.GetReleaseAsync(kit.Asset, result, ArtifactDecisionKit.Expected, CancellationToken.None));
        Assert.AreEqual(1, kit.Model.Calls);
    }

    [TestMethod]
    public async Task ModelConcernNeverReachesAReleaseCall()
    {
        var kit = new ReleaseKit(); kit.Asset.SetIdentityHoldCode(IdentityHold.ModelConcern);
        var result = await kit.SeedAsync(ModelReleaseTests.AuthorFacts);
        Assert.IsNull(await kit.Service.GetReleaseAsync(kit.Asset, result, ArtifactDecisionKit.Expected, CancellationToken.None));
        Assert.AreEqual(0, kit.Model.Calls);
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public async Task AcceptedCollectionCanConfirmContentsFromPersistedTierBWithoutTierC(bool accepted, bool released)
    {
        var kit = new ReleaseKit();
        kit.Asset.SetIdentityHoldCode(IdentityHold.CollectionMissingRequestedWork);
        kit.Model.Response = ArtifactDecisionKit.Reply("contains_requested", "supports_declared_identity", "idv.ev-heading");
        var result = await kit.SeedAsync([
            new("ev-title", EvidenceDimension.IdentityCorroboration, ArtifactIdentityCorroboration.TitleDiffers, EvidencePolarity.Negative),
            new("ev-consent", EvidenceDimension.IdentityAttestation, ArtifactIdentityAttestation.CollectionConsentRecorded, EvidencePolarity.Condition),
            new("ev-heading", EvidenceDimension.Profile, ArtifactProfile.Heading, EvidencePolarity.Unknown, "Cinder Gate: a story")]);
        var expected = ArtifactDecisionKit.Expected with { AcceptedAsCollectionContaining = accepted };
        Assert.AreEqual(released, await kit.Service.GetReleaseAsync(kit.Asset, result, expected, CancellationToken.None) is not null);
        Assert.AreEqual(accepted ? 1 : 0, kit.Model.Calls);
    }
}

internal sealed class ReleaseKit : IModelGatewaySettingsStore, IArtifactEvidenceStore
{
    internal const string Reply = "{\"schema\":\"artifact-decision/1\",\"verdict\":\"release\",\"claims\":[{\"kind\":\"supports_declared_identity\",\"evidence_ids\":[\"idv.ev-title\",\"idv.ev-language\",\"idv.ev-series\"],\"note\":\"A corroborated byline variant.\"}]}";
    internal ArtifactDecisionKit Model { get; set; } = new() { Response = Reply };
    internal FamilyLibrarian.Domain.Acquisition.MediaAsset Asset { get; } = ModelReleaseTests.Held();
    internal ModelGatewaySettings Settings { get; } = new(DateTimeOffset.UtcNow);
    internal List<ArtifactEvidence> Rows { get; } = [];
    internal ArtifactDecisionService Service => new(Model, Model, Model.Store, Model, this, this);
    internal ReleaseKit() => Settings.SetAllowModelHoldRelease(true, null, DateTimeOffset.UtcNow);
    internal async Task<AssetIdentityVerificationResult> SeedAsync(IReadOnlyList<IdentityFact> facts)
    {
        var result = AssetIdentityVerificationResult.Unmatched("test", "Held", facts);
        await new ArtifactEvidenceRecorder(this, Model).RecordIdentityVerificationAsync(Asset, result, CancellationToken.None);
        return result;
    }
    public Task<ModelGatewaySettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult<ModelGatewaySettings?>(Settings);
    public Task<ModelGatewaySettings> GetOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Add(IReadOnlyList<ArtifactEvidence> evidence) => Rows.AddRange(evidence);
    public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>> ListLatestAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>>(new Dictionary<Guid, IReadOnlyList<ArtifactEvidence>> { [Asset.Id] = Rows });
}
