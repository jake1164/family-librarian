using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Web.Acquisition;
using FamilyLibrarian.Contracts.Security;

namespace FamilyLibrarian.Web.Endpoints;

internal static class SecurityQueueEndpoints
{
    public static void MapSecurityQueueEndpoints(this IEndpointRouteBuilder app)
    {
        // The security gate's admin surface: a passed scan is trusted by policy;
        // administrators only decide review-required outcomes. Failed evaluations
        // are never approvable, no matter who attempts it.
        var adminMediaAssets = app.MapGroup("/api/v1/admin/media-assets")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        adminMediaAssets.MapGet("/", ListMediaAssetsAsync);
        adminMediaAssets.MapGet("/recent", ListRecentMediaAssetsAsync);
        adminMediaAssets.MapPost("/{assetId:guid}/evaluate", EvaluateMediaAssetAsync);
        adminMediaAssets.MapPost("/{assetId:guid}/retry-identity", RetryIdentityAsync);
        adminMediaAssets.MapPost("/{assetId:guid}/override-identity", OverrideIdentityAsync);
        adminMediaAssets.MapPost("/{assetId:guid}/approve", ApproveMediaAssetAsync);
        adminMediaAssets.MapPost("/{assetId:guid}/reject", RejectMediaAssetAsync);
        adminMediaAssets.MapDelete("/{assetId:guid}", DiscardMediaAssetAsync);
    }

    private static async Task<IResult> ListRecentMediaAssetsAsync(
        int? limit,
        MediaAssetQueueService queue,
        ISemanticFindingStore semanticFindings,
        CancellationToken cancellationToken)
    {
        var maximumCount = limit ?? 50;
        if (maximumCount is < 1 or > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = ["Choose between 1 and 100 files."]
            });
        }

        var entries = await queue.ListRecentAsync(maximumCount, cancellationToken);
        var findings = await LoadFindingsAsync(semanticFindings, entries, cancellationToken);
        return Results.Ok(new MediaAssetAdminListResponse(entries.Select(entry => ToMediaAssetAdminResponse(entry, findings)).ToArray()));
    }

    private static async Task<IResult> ListMediaAssetsAsync(
        MediaAssetQueueService queue,
        ISemanticFindingStore semanticFindings,
        CancellationToken cancellationToken)
    {
        var entries = await queue.ListAsync(cancellationToken);
        var findings = await LoadFindingsAsync(semanticFindings, entries, cancellationToken);
        return Results.Ok(new MediaAssetAdminListResponse(entries.Select(entry => ToMediaAssetAdminResponse(entry, findings)).ToArray()));
    }

    /// <summary>Advisory model notes for the listed files. Display only: nothing here influences the entries.</summary>
    private static Task<IReadOnlyDictionary<string, SemanticFinding>> LoadFindingsAsync(
        ISemanticFindingStore store, IReadOnlyList<MediaAssetQueueEntry> entries, CancellationToken cancellationToken) =>
        store.FindLatestForSubjectsAsync(
            SemanticSubjectKind.Artifact, [.. entries.Select(entry => entry.Asset.AssetId.ToString("N"))], cancellationToken);

    internal static MediaAssetAdminResponse ToMediaAssetAdminResponse(MediaAssetQueueEntry entry) =>
        ToMediaAssetAdminResponse(entry, null);

    internal static MediaAssetAdminResponse ToMediaAssetAdminResponse(
        MediaAssetQueueEntry entry, IReadOnlyDictionary<string, SemanticFinding>? findings) => new(
        entry.Asset.AssetId,
        entry.Asset.RequestId,
        entry.Asset.WorkId,
        entry.Asset.WorkTitle,
        entry.Asset.MediaType.ToString(),
        entry.Asset.Format,
        entry.Asset.OriginalFilename,
        entry.Asset.SizeBytes,
        entry.Asset.StorageState.ToString(),
        entry.Asset.CreatedAtUtc,
        entry.Asset.UpdatedAtUtc,
        entry.LatestEvaluation is null ? null : new SecurityEvaluationDetailResponse(
            entry.LatestEvaluation.Id,
            entry.LatestEvaluation.Status.ToString(),
            entry.LatestEvaluation.CreatedAtUtc,
            entry.LatestEvaluation.CompletedAtUtc,
            entry.LatestEvaluation.ScanResults.Select(result => new SecurityScanResultResponse(
                result.ScannerId, result.IsRequired, result.Status.ToString(), result.ThreatName, result.ScannedAtUtc))
                .ToArray(),
            entry.LatestEvaluation.ValidationResults.Select(result => new FormatValidationResultResponse(
                result.ValidatorId, result.IsValid, result.Message, result.ValidatedAtUtc))
                .ToArray(),
            entry.LatestEvaluation.Approvals.Select(approval => new SecurityApprovalResponse(
                approval.Decision.ToString(), approval.ActorType.ToString(), approval.Reason, approval.DecidedAtUtc))
                .ToArray()),
        entry.Asset.IdentityMismatchReason,
        entry.Asset.ScanFailureReason,
        ToEvidenceResponse(entry.Evidence),
        entry.Asset.IdentityHoldCode,
        entry.Asset.Origin.ToString(),
        findings is not null && findings.TryGetValue(entry.Asset.AssetId.ToString("N"), out var finding)
            ? new AdminArtifactSemanticFindingResponse(
                finding.Outcome.ToString(), finding.Verdict,
                [.. CandidateSemanticAnalyzer.DeserializeClaims(finding.ClaimsJson)
                    .Select(claim => new AdminArtifactSemanticClaimResponse(claim.Kind, claim.Stance, claim.EvidenceIds, claim.Note))],
                finding.FailureDetail, finding.Model, finding.IsCloud, finding.CreatedAtUtc)
            : null,
        entry.Asset.BundleId,
        entry.Asset.BundleTrackCount);

    private static ArtifactEvidenceResponse ToEvidenceResponse(IReadOnlyList<FamilyLibrarian.Domain.Evidence.ArtifactEvidence> evidence) =>
        evidence.Count == 0
            ? new("NotRecorded", null, null, [])
            : new("Recorded", evidence[0].Sha256, evidence.Max(item => item.EvidenceSchemaVersion),
                evidence.Select(item => new ArtifactEvidenceItemResponse(
                    item.RecordKind, item.RecordId, item.FindingId, item.Dimension.ToString(), item.Code,
                    item.Polarity.ToString(), item.Source, item.Value, item.ToolVersion, item.RecordedAtUtc)).ToArray());

    /// <summary>
    /// Queues the scan and returns at once. A large file takes minutes to
    /// scan, and a scan run inside this request died with it; the page follows
    /// progress through SignalR live updates instead.
    /// </summary>
    private static async Task<IResult> EvaluateMediaAssetAsync(
        Guid assetId,
        ISecurityEvaluationRepository repository,
        SecurityEvaluationScheduler queue,
        CancellationToken cancellationToken)
    {
        var asset = await repository.FindAssetAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return Results.NotFound();
        }

        // Processing is allowed through: a scan that stalled there is recovered
        // by the evaluation service, which re-checks how long it has been idle.
        if (asset.StorageState is not (MediaAssetStorageState.Quarantine or MediaAssetStorageState.Processing))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["asset"] = ["Only a quarantined asset can be evaluated."]
            });
        }

        // Already queued or scanning: the request is satisfied as it stands.
        queue.TryEnqueue(assetId);
        return Results.Accepted();
    }

    private static async Task<IResult> ApproveMediaAssetAsync(
        Guid assetId,
        ApprovalDecisionRequest request,
        ApprovalService approvals,
        CancellationToken cancellationToken) =>
        ToApprovalResult(await approvals.ApproveAsync(assetId, request.Reason, cancellationToken));

    private static async Task<IResult> RetryIdentityAsync(
        Guid assetId,
        AutomatedSecurityPipeline securityPipeline,
        CancellationToken cancellationToken) =>
        ToApprovalResult(await securityPipeline.RetryIdentityAsync(assetId, cancellationToken));

    private static async Task<IResult> OverrideIdentityAsync(
        Guid assetId,
        ApprovalDecisionRequest request,
        ApprovalService approvals,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reason"] = ["Explain why this file is correct despite the identity mismatch."]
            });
        }

        return ToApprovalResult(
            await approvals.OverrideIdentityAndApproveAsync(assetId, request.Reason, cancellationToken));
    }

    private static async Task<IResult> RejectMediaAssetAsync(
        Guid assetId,
        ApprovalDecisionRequest request,
        ApprovalService approvals,
        CancellationToken cancellationToken) =>
        ToApprovalResult(await approvals.RejectAsync(assetId, request.Reason, cancellationToken));

    private static async Task<IResult> DiscardMediaAssetAsync(
        Guid assetId,
        AssetDiscardService discards,
        CancellationToken cancellationToken)
    {
        var result = await discards.DiscardAsync(assetId, cancellationToken);
        return result.Outcome switch
        {
            AssetDiscardOutcome.Success => Results.NoContent(),
            AssetDiscardOutcome.NotFound => Results.NotFound(),
            _ => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["asset"] = [result.Error ?? "That file could not be deleted."]
            })
        };
    }

    private static IResult ToApprovalResult(ApprovalResult result) => result.Outcome switch
    {
        ApprovalOutcome.Success => Results.NoContent(),
        ApprovalOutcome.NotFound => Results.NotFound(),
        _ => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["asset"] = [result.Error ?? "That decision could not be recorded."]
        })
    };
}
