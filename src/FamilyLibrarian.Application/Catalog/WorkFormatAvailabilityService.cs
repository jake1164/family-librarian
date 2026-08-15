using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Catalog;

public interface IWorkFormatAvailabilityService
{
    /// <summary>
    /// One row per <see cref="RequestMediaType"/> for this Work, from the calling
    /// user's perspective.
    /// </summary>
    Task<IReadOnlyList<WorkFormatAvailability>> GetAsync(
        Guid userId,
        Guid workId,
        CancellationToken cancellationToken);
}

public sealed class WorkFormatAvailabilityService(IRequestRepository requests)
    : IWorkFormatAvailabilityService
{
    public async Task<IReadOnlyList<WorkFormatAvailability>> GetAsync(
        Guid userId,
        Guid workId,
        CancellationToken cancellationToken)
    {
        var activeRequests = await requests.GetActiveRequestsForWorkAsync(
            userId,
            workId,
            cancellationToken);

        var formatByMediaType = activeRequests
            .SelectMany(request => request.Formats)
            .GroupBy(format => format.MediaType)
            .ToDictionary(group => group.Key, group => group.First());

        return Enum.GetValues<RequestMediaType>()
            .Select(mediaType =>
            {
                formatByMediaType.TryGetValue(mediaType, out var format);
                return new WorkFormatAvailability(
                    workId,
                    mediaType,
                    // No MediaAsset exists until M9, so nothing can be Owned yet.
                    OwnershipState.NotOwned,
                    PreferredAssetId: null,
                    PreferredEditionId: null,
                    AcquisitionState: format is null ? null : ToAcquisitionState(format.Status));
            })
            .ToArray();
    }

    private static AcquisitionState ToAcquisitionState(RequestFormatStatus status) => status switch
    {
        RequestFormatStatus.Requested => Catalog.AcquisitionState.Requested,
        RequestFormatStatus.NotAvailable => Catalog.AcquisitionState.NotAvailable,
        RequestFormatStatus.Cancelled => Catalog.AcquisitionState.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown request format status.")
    };
}
