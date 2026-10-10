using System.Text;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
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

    /// <summary>The provider id of the acquisition candidate an asset came from, or <see langword="null"/> when unknown.</summary>
    Task<string?> FindAcquisitionProviderIdAsync(Guid? acquisitionCandidateId, CancellationToken cancellationToken);

    /// <summary>
    /// The most recent record of each kind per asset (history is kept; this is the current view).
    /// Assets with no evidence are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<ArtifactEvidence>>> ListLatestAsync(
        IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken);
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
    IEnumerable<IDirectAcquisitionProvider> builtInProviders,
    IClock clock) : IArtifactEvidenceRecorder
{
    private const string ManualProviderId = "manual";
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

            var provider = await store.FindAcquisitionProviderIdAsync(asset.SourceAcquisitionCandidateId, cancellationToken);
            var (attestationCode, attestationPolarity) = Attestation(asset, provider);
            Add("ev-attestation", EvidenceDimension.IdentityAttestation, attestationCode, attestationPolarity,
                PipelineSource, $"preConfirmed={asset.IdentityPreConfirmed};provider={provider ?? "unknown"}");

            var noVerifier = result.VerifierId == NoVerifierId;
            // Only a negative observation about identity itself is a contradiction; a completeness or
            // language cue must never be read as one.
            var contradicted = result.Facts.Any(fact =>
                fact.Dimension == EvidenceDimension.IdentityCorroboration && fact.Polarity == EvidencePolarity.Negative);
            var (corroborationCode, corroborationPolarity) = result switch
            {
                { IsMatch: true } when noVerifier => (EvidenceReasonCodes.ArtifactIdentityCorroboration.Unavailable, EvidencePolarity.Unknown),
                { IsMatch: true } => (EvidenceReasonCodes.ArtifactIdentityCorroboration.Corroborated, EvidencePolarity.Positive),
                _ when noVerifier => (EvidenceReasonCodes.ArtifactIdentityCorroboration.Unavailable, EvidencePolarity.Unknown),
                _ when contradicted => (EvidenceReasonCodes.ArtifactIdentityCorroboration.Contradicted, EvidencePolarity.Negative),
                _ => (EvidenceReasonCodes.ArtifactIdentityCorroboration.Inconclusive, EvidencePolarity.Unknown)
            };
            Add("ev-corroboration", EvidenceDimension.IdentityCorroboration, corroborationCode, corroborationPolarity,
                $"verifier:{result.VerifierId}", null);

            foreach (var fact in result.Facts)
            {
                Add(fact.FindingId, fact.Dimension, fact.Code, fact.Polarity, $"verifier:{result.VerifierId}", fact.Value);
            }

            if (!result.Facts.Any(fact => fact.Dimension == EvidenceDimension.Language))
            {
                Add("ev-language", EvidenceDimension.Language, EvidenceReasonCodes.ArtifactLanguage.NotAssessed,
                    EvidencePolarity.Unknown, $"verifier:{result.VerifierId}", null);
            }

            store.Add(items);
            await store.TrySaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Evidence is best effort and must never alter the decision it describes.
        }
    }

    /// <summary>The verifier id used when no format-specific verifier exists.</summary>
    public const string NoVerifierId = "not-applicable";

    private (string Code, EvidencePolarity Polarity) Attestation(MediaAsset asset, string? providerId)
    {
        // Attestation is a statement about the source, never about the file: Weak at most.
        if (!asset.IdentityPreConfirmed)
            return (EvidenceReasonCodes.ArtifactIdentityAttestation.None, EvidencePolarity.Unknown);
        if (providerId is null)
            return (EvidenceReasonCodes.ArtifactIdentityAttestation.UnknownOrigin, EvidencePolarity.Weak);
        if (string.Equals(providerId, ManualProviderId, StringComparison.Ordinal))
            return (EvidenceReasonCodes.ArtifactIdentityAttestation.ManualUpload, EvidencePolarity.Weak);
        return builtInProviders.Any(provider => string.Equals(provider.Id, providerId, StringComparison.Ordinal))
            ? (EvidenceReasonCodes.ArtifactIdentityAttestation.BuiltInSource, EvidencePolarity.Weak)
            : (EvidenceReasonCodes.ArtifactIdentityAttestation.ExternalProviderConfirmed, EvidencePolarity.Weak);
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
