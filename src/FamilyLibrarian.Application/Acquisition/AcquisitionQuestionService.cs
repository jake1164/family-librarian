using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

public sealed class AcquisitionQuestionService(IRequestRepository requests, ISecurityEvaluationRepository assets,
    NotificationService notifications, OutboundCommunicationService communications, IClock clock,
    IAssetIdentityVerificationService identity, AutomatedSecurityPipeline pipeline, AcquisitionPreferenceService? preferences = null)
{
    public const string ReservedProvider = "reserved-copy";

    public async Task<bool> AskAsync(BookRequest request, RequestFormat format, string workTitle, AcquisitionQuestionOffer offer, CancellationToken cancellationToken)
    {
        if (request.QuestionKind is not null || request.ReviewCandidates.Count > 0 || request.RequiresManualFulfillment) return false;
        var candidate = RequestReviewCandidateRecord.WithCatalogFallback(
            RequestReviewCandidateRecord.From(format.Id, offer.Candidate, offer.SearchVariant), workTitle, null);
        request.AskQuestion(offer.Kind, format.Id, AcquisitionQuestionPolicy.Text(offer.Kind, workTitle, candidate.Title, candidate.Language), [candidate], clock.UtcNow);
        await requests.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task RemindAsync(BookRequest request, string workTitle, CancellationToken cancellationToken)
    {
        if (!request.MarkQuestionReminded(clock.UtcNow)) return;
        await requests.SaveChangesAsync(cancellationToken);
        await NotifyAsync(request, workTitle, cancellationToken);
    }

    public async Task NotifyAsync(BookRequest request, string workTitle, CancellationToken cancellationToken)
    {
        if (request.QuestionKind is not { } kind) return;
        var candidate = request.ReviewCandidates.Single(c => c.RequestFormatId == request.QuestionFormatId);
        var text = AcquisitionQuestionPolicy.Text(kind, workTitle, candidate.Title, candidate.Language);
        var recipients = preferences is null ? request.ActiveRequesterIds.ToArray() :
            await preferences.MissingAsync(request.ActiveRequesterIds, kind, candidate.Language, cancellationToken);
        foreach (var userId in recipients)
        {
            await notifications.RecordAcquisitionQuestionAsync(userId, request.Id, workTitle, text, cancellationToken);
            await communications.EnqueueAsync(userId, OutboundCommunicationTypes.AcquisitionQuestionAsked, text,
                "A choice about your request", "BookRequest", request.Id, cancellationToken);
        }
    }

    public async Task<AcquisitionQuestionOffer?> FindReserveAsync(BookRequest request, RequestFormat format, string title, CancellationToken cancellationToken)
    {
        if (format.MediaType != RequestMediaType.Audiobook) return null;
        foreach (var asset in await assets.ListReservedDramatizationsAsync(format.Id, cancellationToken))
        {
            if (request.DeclinedCandidates.Any(c => c.RequestFormatId == format.Id && c.ProviderId == ReservedProvider && c.ProviderResultId == asset.Id.ToString())) continue;
            if (await ReservedAssetsAsync(asset.Id, format.Id, cancellationToken) is null) continue;
            return new(AcquisitionQuestionKind.DramatizedOnly, new(ReservedProvider, asset.Id.ToString(), request.WorkId, null,
                format.MediaType, Catalog.OptionKind.DirectAcquisition, Catalog.AcquisitionMethod.DirectDownload, asset.Format,
                format.AcceptedLanguage, null, null, null, null, null, null, null, null,
                Title: title));
        }
        return null;
    }

    public async Task<IReadOnlyList<MediaAsset>?> ReservedAssetsAsync(Guid assetId, Guid formatId, CancellationToken cancellationToken)
    {
        var asset = await assets.FindAssetAsync(assetId, cancellationToken);
        if (asset is null) return null;
        var siblings = asset.BundleId is { } bundle ? await assets.FindAssetsByBundleIdAsync(bundle, cancellationToken) : [asset];
        return siblings.Count == (asset.BundleTrackCount ?? 1) && siblings.All(a => a.AssociatedRequestFormatId == formatId &&
            a.StorageState == MediaAssetStorageState.Unmatched && a.RuledOutAtUtc is not null &&
            a.IdentityHoldCode == EvidenceReasonCodes.IdentityHold.DramatizedAdaptation) ? siblings : null;
    }

    public async Task RetryReserveAsync(IReadOnlyList<MediaAsset> reserved, CancellationToken cancellationToken)
    {
        var allMatch = true;
        foreach (var asset in reserved) allMatch &= (await identity.RetryUnmatchedAsync(asset.Id, cancellationToken)).IsMatch;
        if (!allMatch) return;
        // The existing bundle pipeline checks every sibling and repeats identity before any approval.
        await pipeline.EvaluateBundleAsync(reserved.Select(a => a.Id).ToArray(), cancellationToken);
    }
}
