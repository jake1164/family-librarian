using System.Text;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Security;

namespace FamilyLibrarian.Application.Security;

/// <summary>Persistence seam for artifact evidence. Append-only; never edits an earlier record.</summary>
public interface IArtifactEvidenceStore
{
    void Add(IReadOnlyList<ArtifactEvidence> evidence);

    /// <summary>
    /// Saves pending evidence only. Never throws: on failure it discards the pending evidence
    /// (so it cannot poison a later save of an unrelated decision) and returns <see langword="false"/>.
    /// </summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The most recent record of each kind per asset (history is kept; this is the current view).
    /// Assets with no evidence are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>> ListLatestAsync(
        IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken);

    /// <summary>Latest raw audio measurements for these exact bytes, independent of newer comparison records.</summary>
    async Task<IReadOnlyList<ArtifactEvidence>> ListLatestAudioMeasurementsAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var records = await ListLatestAsync([asset.Id], cancellationToken);
        return records.TryGetValue(asset.Id, out var rows)
            ? rows.Where(row => row.Sha256 == asset.Sha256 && row.RecordKind == ArtifactEvidenceRecordKinds.ArtifactProfile &&
                row.Dimension == EvidenceDimension.Profile && row.Code is
                    EvidenceReasonCodes.ArtifactProfile.AudioTotalDuration or EvidenceReasonCodes.ArtifactProfile.AudioChapterTitles).ToArray()
            : [];
    }
}

/// <summary>Records what the existing security and identity checks already observed. Evidence only.</summary>
public interface IArtifactEvidenceRecorder
{
    /// <param name="toolVersions">Validator id to its tool/parser version, where known.</param>
    Task RecordSecurityEvaluationAsync(
        MediaAsset asset, SecurityEvaluation evaluation, IReadOnlyDictionary<string, string?> toolVersions,
        CancellationToken cancellationToken);

    Task RecordIdentityVerificationAsync(
        MediaAsset asset, AssetIdentityVerificationResult result, CancellationToken cancellationToken);

    /// <summary>Records measured technical properties (M1-4c) as their own record. Never part of an identity record.</summary>
    Task RecordTechnicalInspectionAsync(
        MediaAsset asset, IReadOnlyList<IdentityFact> findings, string source, CancellationToken cancellationToken);

    /// <summary>
    /// Records that an administrator approved a held asset on their own judgment, so the evidence no longer reads as
    /// though the hold simply stands. Identifies the administrator by id only (no name or email).
    /// </summary>
    Task RecordIdentityOverrideAsync(
        MediaAsset asset, Guid? administratorUserId, string? overriddenHoldCode, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// Turns the outcomes of the existing scanners, validators and identity verifiers into persisted
/// <see cref="ArtifactEvidence"/>.
/// </summary>
/// <remarks>
/// Strictly after the fact: it runs once the decision it describes has been saved, it adds no check,
/// and any failure here is swallowed so evidence recording can never change an approval, a hold, a
/// scan result or publishing. Each pass uses a fresh record id, so reprocessing appends history.
/// </remarks>
public sealed class ArtifactEvidenceRecorder(
    IArtifactEvidenceStore store,
    IClock clock) : IArtifactEvidenceRecorder
{
    private const string PipelineSource = "pipeline";

    public async Task RecordSecurityEvaluationAsync(
        MediaAsset asset, SecurityEvaluation evaluation, IReadOnlyDictionary<string, string?> toolVersions,
        CancellationToken cancellationToken)
    {
        try
        {
            var recordId = Guid.NewGuid();
            var now = clock.UtcNow;
            var items = new List<ArtifactEvidence>();

            void Add(string findingId, EvidenceDimension dimension, string code, EvidencePolarity polarity,
                string source, string? value, string? toolVersion = null) =>
                items.Add(new ArtifactEvidence(
                    asset.Id, asset.Sha256, ArtifactEvidenceRecordKinds.SecurityEvaluation, recordId,
                    UniqueId(items, findingId), dimension, code, polarity, source, value, toolVersion, now));

            foreach (var scan in evaluation.ScanResults)
            {
                var (code, polarity) = scan.Status switch
                {
                    ScanResultStatus.Clean => (EvidenceReasonCodes.ArtifactSecurity.ScanClean, EvidencePolarity.Positive),
                    ScanResultStatus.Detected => (EvidenceReasonCodes.ArtifactSecurity.ThreatDetected, EvidencePolarity.Negative),
                    ScanResultStatus.Error => (EvidenceReasonCodes.ArtifactSecurity.ScanError, EvidencePolarity.Unknown),
                    _ => (EvidenceReasonCodes.ArtifactSecurity.ScannerUnavailable, EvidencePolarity.Unknown)
                };
                Add($"ev-scan-{Slug(scan.ScannerId)}", EvidenceDimension.Security, code, polarity,
                    $"scanner:{scan.ScannerId}", scan.Status == ScanResultStatus.Detected ? scan.ThreatName : null,
                    scan.ScannerVersion);
            }

            var defects = evaluation.ValidationResults.Where(result => !result.IsValid).ToArray();
            foreach (var defect in defects)
            {
                toolVersions.TryGetValue(defect.ValidatorId, out var version);
                Add($"ev-structure-{Slug(defect.ValidatorId)}", EvidenceDimension.Structure,
                    EvidenceReasonCodes.ArtifactStructure.DefectReported, EvidencePolarity.Negative,
                    $"validator:{defect.ValidatorId}", defect.Message, version);
            }

            if (defects.Length == 0 && evaluation.ValidationResults.Count > 0)
            {
                // Validators that do not apply to a format report "valid", so this says only that
                // no defect was reported by the listed validators, not that every one inspected the file.
                Add("ev-structure", EvidenceDimension.Structure, EvidenceReasonCodes.ArtifactStructure.NoDefectReported,
                    EvidencePolarity.Positive, "validators",
                    string.Join(",", evaluation.ValidationResults.Select(result => result.ValidatorId)));
            }

            Add("ev-completeness", EvidenceDimension.Completeness, EvidenceReasonCodes.ArtifactCompleteness.NotAssessed,
                EvidencePolarity.Unknown, PipelineSource, "no completeness check exists yet");

            store.Add(items);
            await store.TrySaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Evidence is best effort and must never alter the decision it describes.
        }
    }

    public async Task RecordIdentityVerificationAsync(
        MediaAsset asset, AssetIdentityVerificationResult result, CancellationToken cancellationToken)
    {
        try
        {
            var recordId = Guid.NewGuid();
            var now = clock.UtcNow;
            var items = new List<ArtifactEvidence>();

            void Add(string findingId, EvidenceDimension dimension, string code, EvidencePolarity polarity,
                string source, string? value) =>
                items.Add(new ArtifactEvidence(
                    asset.Id, asset.Sha256, ArtifactEvidenceRecordKinds.IdentityVerification, recordId,
                    UniqueId(items, findingId), dimension, code, polarity, source, value, toolVersion: null, now));

            var (attestationCode, attestationPolarity) = Attestation(asset);
            Add("ev-attestation", EvidenceDimension.IdentityAttestation, attestationCode, attestationPolarity,
                PipelineSource, $"preConfirmed={asset.IdentityPreConfirmed};origin={asset.Origin}");

            var noVerifier = result.VerifierId == NoVerifierId;
            // Only a negative observation about identity itself is a contradiction; a completeness or
            // language cue must never be read as one.
            var identityFacts = result.Facts.Where(fact => fact.Dimension == EvidenceDimension.IdentityCorroboration).ToArray();
            var contradicted = identityFacts.Any(fact => fact.Polarity == EvidencePolarity.Negative);
            // A hold for a non-identity reason (for example a conflicting declared language) leaves the identity
            // dimension as the file's title/author actually showed: corroborated, not inconclusive.
            var identityAgrees = !contradicted && identityFacts.Any(fact => fact.Polarity == EvidencePolarity.Positive);
            var (corroborationCode, corroborationPolarity) = result switch
            {
                { IsMatch: true } when noVerifier => (EvidenceReasonCodes.ArtifactIdentityCorroboration.Unavailable, EvidencePolarity.Unknown),
                { IsMatch: true } => (EvidenceReasonCodes.ArtifactIdentityCorroboration.Corroborated, EvidencePolarity.Positive),
                _ when noVerifier => (EvidenceReasonCodes.ArtifactIdentityCorroboration.Unavailable, EvidencePolarity.Unknown),
                _ when contradicted => (EvidenceReasonCodes.ArtifactIdentityCorroboration.Contradicted, EvidencePolarity.Negative),
                _ when identityAgrees => (EvidenceReasonCodes.ArtifactIdentityCorroboration.Corroborated, EvidencePolarity.Positive),
                _ => (EvidenceReasonCodes.ArtifactIdentityCorroboration.Inconclusive, EvidencePolarity.Unknown)
            };
            Add("ev-corroboration", EvidenceDimension.IdentityCorroboration, corroborationCode, corroborationPolarity,
                $"verifier:{result.VerifierId}", null);

            // A profile fact describes the file; it is not part of the identity record and is kept in its own pass.
            foreach (var fact in result.Facts.Where(fact => fact.Dimension != EvidenceDimension.Profile))
            {
                Add(fact.FindingId, fact.Dimension, fact.Code, fact.Polarity, $"verifier:{result.VerifierId}", fact.Value);
            }

            if (!result.Facts.Any(fact => fact.Dimension == EvidenceDimension.Language))
            {
                Add("ev-language", EvidenceDimension.Language, EvidenceReasonCodes.ArtifactLanguage.NotAssessed,
                    EvidencePolarity.Unknown, $"verifier:{result.VerifierId}", null);
            }

            items.AddRange(ProfileItems(
                asset, result.Facts, $"verifier:{result.VerifierId}", now));
            store.Add(items);
            await store.TrySaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Evidence is best effort and must never alter the decision it describes.
        }
    }

    public async Task RecordTechnicalInspectionAsync(
        MediaAsset asset, IReadOnlyList<IdentityFact> findings, string source, CancellationToken cancellationToken)
    {
        try
        {
            if (findings.Count == 0) return;
            var recordId = Guid.NewGuid();
            var now = clock.UtcNow;
            var items = new List<ArtifactEvidence>();
            foreach (var finding in findings.Where(finding => finding.Dimension == EvidenceDimension.AudioTechnical))
            {
                items.Add(new ArtifactEvidence(
                    asset.Id, asset.Sha256, ArtifactEvidenceRecordKinds.TechnicalInspection, recordId,
                    UniqueId(items, finding.FindingId), finding.Dimension, finding.Code, finding.Polarity, source, finding.Value, toolVersion: null, now));
            }

            items.AddRange(ProfileItems(asset, findings, source, now));
            if (items.Count == 0) return;
            store.Add(items);
            await store.TrySaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Evidence is best effort and must never alter the decision it describes.
        }
    }

    public async Task RecordIdentityOverrideAsync(
        MediaAsset asset, Guid? administratorUserId, string? overriddenHoldCode, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var recordId = Guid.NewGuid();
            var now = clock.UtcNow;
            var source = $"admin:{administratorUserId?.ToString() ?? "unknown"}";
            store.Add(
            [
                new ArtifactEvidence(
                    asset.Id, asset.Sha256, ArtifactEvidenceRecordKinds.IdentityOverride, recordId, "ev-override",
                    EvidenceDimension.IdentityAttestation, EvidenceReasonCodes.ArtifactIdentityAttestation.AdminOverride,
                    EvidencePolarity.Weak, source, overriddenHoldCode ?? "none", toolVersion: null, now),
                new ArtifactEvidence(
                    asset.Id, asset.Sha256, ArtifactEvidenceRecordKinds.IdentityOverride, recordId, "ev-override-reason",
                    EvidenceDimension.IdentityAttestation, EvidenceReasonCodes.ArtifactIdentityAttestation.AdminOverrideReason,
                    EvidencePolarity.Weak, source, reason, toolVersion: null, now)
            ]);
            await store.TrySaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Evidence is best effort and must never alter the decision it describes.
        }
    }

    /// <summary>The verifier id used when no format-specific verifier exists.</summary>
    public const string NoVerifierId = "not-applicable";

    /// <summary>
    /// Attestation comes only from the origin persisted when the asset was created. It is never inferred from a
    /// provider id or name, which an external provider chooses freely. A statement about the source, never the
    /// file: Weak at most.
    /// </summary>
    private static (string Code, EvidencePolarity Polarity) Attestation(MediaAsset asset)
    {
        if (asset.Origin == AssetOrigin.Legacy)
            return (EvidenceReasonCodes.ArtifactIdentityAttestation.LegacyUnclassified,
                asset.IdentityPreConfirmed ? EvidencePolarity.Weak : EvidencePolarity.Unknown);
        if (!asset.IdentityPreConfirmed)
            return (EvidenceReasonCodes.ArtifactIdentityAttestation.None, EvidencePolarity.Unknown);

        return asset.Origin switch
        {
            AssetOrigin.ManualUpload => (EvidenceReasonCodes.ArtifactIdentityAttestation.ManualUpload, EvidencePolarity.Weak),
            AssetOrigin.BuiltInProvider => (EvidenceReasonCodes.ArtifactIdentityAttestation.BuiltInSource, EvidencePolarity.Weak),
            AssetOrigin.ExternalProvider => (EvidenceReasonCodes.ArtifactIdentityAttestation.ExternalProviderConfirmed, EvidencePolarity.Weak),
            _ => (EvidenceReasonCodes.ArtifactIdentityAttestation.UnknownOrigin, EvidencePolarity.Weak)
        };
    }

    /// <summary>
    /// The profile-dimension findings of a pass as one <see cref="ArtifactEvidenceRecordKinds.ArtifactProfile"/> record, beside the
    /// pass that observed them. Empty when the pass reported none.
    /// </summary>
    private static List<ArtifactEvidence> ProfileItems(
        MediaAsset asset, IEnumerable<IdentityFact> findings, string source, DateTimeOffset recordedAtUtc)
    {
        var profile = findings.Where(finding => finding.Dimension == EvidenceDimension.Profile).ToArray();
        var items = new List<ArtifactEvidence>(profile.Length);
        if (profile.Length == 0) return items;

        var recordId = Guid.NewGuid();
        foreach (var finding in profile)
        {
            items.Add(new ArtifactEvidence(
                asset.Id, asset.Sha256, ArtifactEvidenceRecordKinds.ArtifactProfile, recordId,
                UniqueId(items, finding.FindingId), EvidenceDimension.Profile, finding.Code, finding.Polarity, source,
                finding.Value, toolVersion: null, recordedAtUtc));
        }

        return items;
    }

    /// <summary>Keeps finding ids unique within one record even if two sources slug to the same text.</summary>
    private static string UniqueId(List<ArtifactEvidence> existing, string findingId)
    {
        var candidate = findingId;
        for (var suffix = 2; existing.Any(item => item.FindingId == candidate); suffix++)
        {
            candidate = $"{findingId}-{suffix}";
        }

        return candidate.Length > ArtifactEvidence.MaxFindingIdLength ? candidate[..ArtifactEvidence.MaxFindingIdLength] : candidate;
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '-');
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }
}
