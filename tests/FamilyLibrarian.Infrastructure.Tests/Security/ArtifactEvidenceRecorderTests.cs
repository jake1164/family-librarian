using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// M1-2: artifact evidence records what the existing checks observed, keeps attestation and content
/// corroboration as separate dimensions, and never alters the decision it describes.
/// </summary>
[TestClass]
public sealed class ArtifactEvidenceRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('b', 64);

    private static MediaAsset Asset(bool preConfirmed = true, Guid? candidateId = null, string format = ".epub") => new(
        Guid.NewGuid(), editionId: null, RequestMediaType.Ebook, format, "book" + format, "stored" + format, 1,
        Hash, "application/epub+zip", Guid.NewGuid(), candidateId, Now, identityPreConfirmed: preConfirmed);

    private static (ArtifactEvidenceRecorder Recorder, FakeEvidenceStore Store) Create(
        Dictionary<Guid, string>? providers = null, bool failingStore = false)
    {
        var store = new FakeEvidenceStore(providers ?? [], failingStore);
        return (new ArtifactEvidenceRecorder(store, [new BuiltIn("gutenberg"), new BuiltIn("librivox")], new FixedClock()), store);
    }

    private static SecurityEvaluation Evaluation(MediaAsset asset, Action<SecurityEvaluation>? configure = null)
    {
        var evaluation = new SecurityEvaluation(asset.Id, "v1", Now);
        configure?.Invoke(evaluation);
        evaluation.Evaluate(Now);
        return evaluation;
    }

    [TestMethod]
    public async Task ASecurityEvaluationIsRecordedWithFullProvenance()
    {
        var asset = Asset();
        var (recorder, store) = Create();
        var evaluation = Evaluation(asset, e =>
        {
            e.RecordScanResult("clamav", true, ScanResultStatus.Clean, null, "ClamAV 1.4", Now);
            e.RecordValidationResult("epub-structure", true, null, Now);
        });

        await recorder.RecordSecurityEvaluationAsync(
            asset, evaluation, new Dictionary<string, string?> { ["epub-structure"] = "1.2.3.0" }, CancellationToken.None);

        Assert.IsNotEmpty(store.Saved);
        Assert.IsTrue(store.Saved.All(item => item.MediaAssetId == asset.Id && item.Sha256 == Hash));
        Assert.IsTrue(store.Saved.All(item => item.EvidenceSchemaVersion == ArtifactEvidence.SchemaVersion));
        Assert.IsTrue(store.Saved.All(item => item.RecordKind == ArtifactEvidenceRecordKinds.SecurityEvaluation));
        Assert.IsTrue(store.Saved.All(item => item.RecordedAtUtc == Now));
        Assert.HasCount(1, store.Saved.Select(item => item.RecordId).Distinct().ToArray());
        var scan = store.Saved.Single(item => item.FindingId == "ev-scan-clamav");
        Assert.AreEqual(EvidenceDimension.Security, scan.Dimension);
        Assert.AreEqual("artifact.security.scan_clean", scan.Code);
        Assert.AreEqual(EvidencePolarity.Positive, scan.Polarity);
        Assert.AreEqual("scanner:clamav", scan.Source);
        Assert.AreEqual("ClamAV 1.4", scan.ToolVersion);
        var structure = store.Saved.Single(item => item.Dimension == EvidenceDimension.Structure);
        Assert.AreEqual("artifact.structure.no_defect_reported", structure.Code);
    }

    [TestMethod]
    public async Task CompletenessIsAlwaysRecordedAsNotAssessedNeverAsComplete()
    {
        var asset = Asset();
        var (recorder, store) = Create();

        await recorder.RecordSecurityEvaluationAsync(asset, Evaluation(asset), new Dictionary<string, string?>(), CancellationToken.None);

        var completeness = store.Saved.Single(item => item.Dimension == EvidenceDimension.Completeness);
        Assert.AreEqual("artifact.completeness.not_assessed", completeness.Code);
        Assert.AreEqual(EvidencePolarity.Unknown, completeness.Polarity);
    }

    [TestMethod]
    public async Task ADetectedThreatAndAStructuralDefectAreRecordedAsNegativeEvidence()
    {
        var asset = Asset();
        var (recorder, store) = Create();
        var evaluation = Evaluation(asset, e =>
        {
            e.RecordScanResult("clamav", true, ScanResultStatus.Detected, "Example.Threat", "v1", Now);
            e.RecordValidationResult("epub-structure", false, "The archive entry '../x' has an unsafe path.", Now);
        });

        await recorder.RecordSecurityEvaluationAsync(asset, evaluation, new Dictionary<string, string?>(), CancellationToken.None);

        var threat = store.Saved.Single(item => item.Code == "artifact.security.threat_detected");
        Assert.AreEqual(EvidencePolarity.Negative, threat.Polarity);
        Assert.AreEqual("Example.Threat", threat.Value);
        var defect = store.Saved.Single(item => item.Code == "artifact.structure.defect_reported");
        Assert.AreEqual(EvidencePolarity.Negative, defect.Polarity);
        Assert.IsFalse(store.Saved.Any(item => item.Code == "artifact.structure.no_defect_reported"));
    }

    [TestMethod]
    public async Task ScannerErrorAndUnavailableAreUnknownNotNegative()
    {
        var asset = Asset();
        var (recorder, store) = Create();
        var evaluation = Evaluation(asset, e =>
        {
            e.RecordScanResult("clamav", true, ScanResultStatus.Error, null, null, Now);
            e.RecordScanResult("second", false, ScanResultStatus.Unavailable, null, null, Now);
        });

        await recorder.RecordSecurityEvaluationAsync(asset, evaluation, new Dictionary<string, string?>(), CancellationToken.None);

        Assert.AreEqual(EvidencePolarity.Unknown, store.Saved.Single(item => item.FindingId == "ev-scan-clamav").Polarity);
        Assert.AreEqual(EvidencePolarity.Unknown, store.Saved.Single(item => item.FindingId == "ev-scan-second").Polarity);
    }

    [TestMethod]
    public async Task HostileOrOversizedValuesAreBoundedAndStripped()
    {
        var asset = Asset();
        var (recorder, store) = Create();
        var hostile = "evil\u0007\u001b[31m" + new string('x', 5_000) + "\nINJECTED";
        var evaluation = Evaluation(asset, e => e.RecordValidationResult("epub-structure", false, hostile, Now));

        await recorder.RecordSecurityEvaluationAsync(asset, evaluation, new Dictionary<string, string?>(), CancellationToken.None);

        var stored = store.Saved.Single(item => item.Code == "artifact.structure.defect_reported").Value!;
        Assert.IsLessThanOrEqualTo(ArtifactEvidence.MaxValueLength, stored.Length);
        Assert.IsFalse(stored.Any(char.IsControl));
    }

    [TestMethod]
    public async Task ReprocessingAppendsANewRecordWithoutCollidingIds()
    {
        var asset = Asset();
        var (recorder, store) = Create();
        var evaluation = Evaluation(asset, e => e.RecordScanResult("clamav", true, ScanResultStatus.Clean, null, "v", Now));

        await recorder.RecordSecurityEvaluationAsync(asset, evaluation, new Dictionary<string, string?>(), CancellationToken.None);
        await recorder.RecordSecurityEvaluationAsync(asset, evaluation, new Dictionary<string, string?>(), CancellationToken.None);

        Assert.HasCount(2, store.Saved.Select(item => item.RecordId).Distinct().ToArray());
        var keys = store.Saved.Select(item => (item.MediaAssetId, item.RecordId, item.FindingId)).ToArray();
        Assert.HasCount(keys.Length, keys.Distinct().ToArray(), "(asset, record, finding) must be unique across repeat processing");
    }

    [TestMethod]
    public async Task FindingIdsAreUniqueWithinOneRecordEvenWhenSourcesSlugAlike()
    {
        var asset = Asset();
        var (recorder, store) = Create();
        var evaluation = Evaluation(asset, e =>
        {
            e.RecordScanResult("clam.av", true, ScanResultStatus.Clean, null, "v", Now);
            e.RecordScanResult("clam-av", true, ScanResultStatus.Clean, null, "v", Now);
        });

        await recorder.RecordSecurityEvaluationAsync(asset, evaluation, new Dictionary<string, string?>(), CancellationToken.None);

        var ids = store.Saved.Select(item => item.FindingId).ToArray();
        Assert.HasCount(ids.Length, ids.Distinct().ToArray());
    }

    [TestMethod]
    public async Task AttestationWithNoContentVerifierIsNeverRecordedAsCorroboration()
    {
        var candidate = Guid.NewGuid();
        var asset = Asset(preConfirmed: true, candidate, ".mp3");
        var (recorder, store) = Create(new() { [candidate] = "librivox" });

        await recorder.RecordIdentityVerificationAsync(
            asset, AssetIdentityVerificationResult.Match(ArtifactEvidenceRecorder.NoVerifierId), CancellationToken.None);

        var attestation = store.Saved.Single(item => item.Dimension == EvidenceDimension.IdentityAttestation);
        Assert.AreEqual("artifact.identity_attestation.built_in_source", attestation.Code);
        Assert.AreEqual(EvidencePolarity.Weak, attestation.Polarity);
        var corroboration = store.Saved.Single(item => item.Dimension == EvidenceDimension.IdentityCorroboration);
        Assert.AreEqual("artifact.identity_corroboration.unavailable", corroboration.Code);
        Assert.AreEqual(EvidencePolarity.Unknown, corroboration.Polarity);
        Assert.IsFalse(store.Saved.Any(item => item.Polarity == EvidencePolarity.Positive),
            "Attestation without a verifier must not produce any positive identity evidence.");
        Assert.AreEqual("artifact.language.not_assessed", store.Saved.Single(item => item.Dimension == EvidenceDimension.Language).Code);
    }

    [TestMethod]
    [DataRow("manual", true, "artifact.identity_attestation.manual_upload")]
    [DataRow("gutenberg", true, "artifact.identity_attestation.built_in_source")]
    [DataRow("example-source", true, "artifact.identity_attestation.external_provider_confirmed")]
    [DataRow("example-source", false, "artifact.identity_attestation.none")]
    [DataRow(null, true, "artifact.identity_attestation.unknown_origin")]
    public async Task AttestationProvenanceIsDerivedFromTheAcquisitionSource(string? provider, bool preConfirmed, string expectedCode)
    {
        var candidate = provider is null ? (Guid?)null : Guid.NewGuid();
        var asset = Asset(preConfirmed, candidate);
        var (recorder, store) = Create(candidate is { } id ? new() { [id] = provider! } : null);

        await recorder.RecordIdentityVerificationAsync(
            asset, AssetIdentityVerificationResult.Match(ArtifactEvidenceRecorder.NoVerifierId), CancellationToken.None);

        Assert.AreEqual(expectedCode, store.Saved.Single(item => item.Dimension == EvidenceDimension.IdentityAttestation).Code);
    }

    [TestMethod]
    public async Task AnExplicitContentContradictionIsRecordedEvenWhenTheSourceAttestedTheIdentity()
    {
        var candidate = Guid.NewGuid();
        var asset = Asset(preConfirmed: true, candidate);
        var (recorder, store) = Create(new() { [candidate] = "manual" });
        var result = AssetIdentityVerificationResult.Unmatched(
            "epub-package-metadata", "differs",
            [new IdentityFact("ev-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleDiffers, EvidencePolarity.Negative)]);

        await recorder.RecordIdentityVerificationAsync(asset, result, CancellationToken.None);

        Assert.AreEqual("artifact.identity_attestation.manual_upload", store.Saved.Single(i => i.Dimension == EvidenceDimension.IdentityAttestation).Code);
        var corroboration = store.Saved.Single(item => item.FindingId == "ev-corroboration");
        Assert.AreEqual("artifact.identity_corroboration.contradicted", corroboration.Code);
        Assert.AreEqual(EvidencePolarity.Negative, corroboration.Polarity);
    }

    [TestMethod]
    public async Task ALanguageOnlyHoldIsRecordedInTheLanguageDimensionWhileTitleAndAuthorStayCorroborated()
    {
        // M1-4a: title and author agree, the declared audio language conflicts, so the verdict is a hold.
        var asset = Asset(preConfirmed: true, format: ".m4b");
        var (recorder, store) = Create();
        var result = AssetIdentityVerificationResult.Unmatched("m4b-tag-metadata", "declared language deu",
        [
            new IdentityFact("ev-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAgrees, EvidencePolarity.Positive),
            new IdentityFact("ev-author", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.AuthorAgrees, EvidencePolarity.Positive),
            new IdentityFact("ev-language", EvidenceDimension.Language, EvidenceReasonCodes.ArtifactLanguage.DeclaredConflicts, EvidencePolarity.Negative, "deu")
        ]);

        await recorder.RecordIdentityVerificationAsync(asset, result, CancellationToken.None);

        var language = store.Saved.Single(item => item.Dimension == EvidenceDimension.Language);
        Assert.AreEqual("artifact.language.declared_conflicts", language.Code);
        Assert.AreEqual(EvidencePolarity.Negative, language.Polarity);
        Assert.AreEqual("deu", language.Value);
        Assert.IsFalse(store.Saved.Any(item => item.Code == "artifact.language.not_assessed"));
        Assert.AreEqual("artifact.identity_corroboration.corroborated", store.Saved.Single(item => item.FindingId == "ev-corroboration").Code,
            "A language-only hold is not a title/author contradiction and not 'inconclusive'.");
    }

    [TestMethod]
    public async Task AnUnreadableFileIsInconclusiveNotContradicted()
    {
        var asset = Asset();
        var (recorder, store) = Create();

        await recorder.RecordIdentityVerificationAsync(
            asset, AssetIdentityVerificationResult.Unmatched("epub-package-metadata", "unreadable"), CancellationToken.None);

        var corroboration = store.Saved.Single(item => item.FindingId == "ev-corroboration");
        Assert.AreEqual("artifact.identity_corroboration.inconclusive", corroboration.Code);
        Assert.AreEqual(EvidencePolarity.Unknown, corroboration.Polarity);
    }

    [TestMethod]
    public async Task VerifierFactsAreRecordedAsTheirOwnDimensions()
    {
        var asset = Asset();
        var (recorder, store) = Create();
        var result = AssetIdentityVerificationResult.Match("epub-package-metadata",
        [
            new IdentityFact("ev-title", EvidenceDimension.IdentityCorroboration, EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleAgrees, EvidencePolarity.Positive),
            new IdentityFact("ev-language", EvidenceDimension.Language, EvidenceReasonCodes.ArtifactLanguage.DeclaredMatchesRequest, EvidencePolarity.Positive, "en")
        ]);

        await recorder.RecordIdentityVerificationAsync(asset, result, CancellationToken.None);

        Assert.AreEqual("artifact.identity_corroboration.corroborated", store.Saved.Single(i => i.FindingId == "ev-corroboration").Code);
        var language = store.Saved.Single(item => item.Dimension == EvidenceDimension.Language);
        Assert.AreEqual("artifact.language.declared_matches_request", language.Code);
        Assert.AreEqual("en", language.Value);
        Assert.IsFalse(store.Saved.Any(item => item.Code == "artifact.language.not_assessed"));
    }

    [TestMethod]
    public async Task AFailingStoreNeverEscapesTheRecorder()
    {
        var asset = Asset();
        var (recorder, store) = Create(failingStore: true);

        await recorder.RecordSecurityEvaluationAsync(asset, Evaluation(asset), new Dictionary<string, string?>(), CancellationToken.None);
        await recorder.RecordIdentityVerificationAsync(asset, AssetIdentityVerificationResult.Match("x"), CancellationToken.None);

        Assert.IsEmpty(store.Saved);
    }

    [TestMethod]
    public void NoStoredColumnCanCarryContentBeyondTheBound()
    {
        var evidence = new ArtifactEvidence(
            Guid.NewGuid(), Hash, "k", Guid.NewGuid(), "f", EvidenceDimension.Structure, "c", EvidencePolarity.Unknown, "s",
            new string('z', 100_000), new string('v', 1_000), Now);

        Assert.AreEqual(ArtifactEvidence.MaxValueLength, evidence.Value!.Length);
        Assert.AreEqual(ArtifactEvidence.MaxToolVersionLength, evidence.ToolVersion!.Length);
    }

    private sealed class FakeEvidenceStore(Dictionary<Guid, string> providers, bool failing) : IArtifactEvidenceStore
    {
        private readonly List<ArtifactEvidence> _pending = [];

        public List<ArtifactEvidence> Saved { get; } = [];

        public void Add(IReadOnlyList<ArtifactEvidence> evidence)
        {
            if (failing) throw new InvalidOperationException("store unavailable");
            _pending.AddRange(evidence);
        }

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken)
        {
            Saved.AddRange(_pending);
            _pending.Clear();
            return Task.FromResult(true);
        }

        public Task<string?> FindAcquisitionProviderIdAsync(Guid? acquisitionCandidateId, CancellationToken cancellationToken) =>
            Task.FromResult(acquisitionCandidateId is { } id && providers.TryGetValue(id, out var provider) ? provider : null);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>> ListLatestAsync(
            IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>>(
                new Dictionary<Guid, IReadOnlyList<ArtifactEvidence>>());
    }

    private sealed class BuiltIn(string id) : IDirectAcquisitionProvider
    {
        public string Id => id;

        public Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
            Guid workId, RequestMediaType mediaType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FulfillmentOption>>([]);

        public Task<IReadOnlyList<FulfillmentOption>> FindDirectAcquisitionsAsync(
            BookIdentity identity, RequestMediaType mediaType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FulfillmentOption>>([]);

        public Task<IReadOnlyList<DirectAcquisitionFile>> FetchAsync(
            FulfillmentOption fulfillmentOption, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DirectAcquisitionFile>>([]);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
