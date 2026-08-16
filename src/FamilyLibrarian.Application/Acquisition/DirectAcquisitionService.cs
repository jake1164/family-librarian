using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Fetches a file from a bundled free-source provider (e.g. Gutendex) and
/// stages it exactly like a manual upload.
/// </summary>
/// <remarks>
/// The option is re-derived server-side from the provider's own search
/// rather than trusting anything client-supplied: the browser only ever
/// sends <c>providerId</c>/<c>providerResultId</c>, never a download URL —
/// re-resolving by the request's own Work/media type is what keeps a
/// tampered or stale client request from making the host fetch an arbitrary
/// address.
/// </remarks>
public sealed class DirectAcquisitionService(
    IRequestRepository requests,
    IEnumerable<IDirectAcquisitionProvider> providers,
    IWorkLookup workLookup,
    AcquisitionStagingService staging)
{
    public async Task<ManualImportResult> AcquireAsync(
        Guid requestId,
        Guid requestFormatId,
        string providerId,
        string providerResultId,
        CancellationToken cancellationToken)
    {
        var request = await requests.FindRequestForAdminAsync(requestId, cancellationToken);
        if (request is null)
        {
            return ManualImportResult.Invalid("That request does not exist.");
        }

        var format = request.Formats.FirstOrDefault(format => format.Id == requestFormatId);
        if (format is null)
        {
            return ManualImportResult.Invalid("That format is not part of this request.");
        }

        var provider = providers.FirstOrDefault(
            provider => string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return ManualImportResult.Invalid("That provider is not available.");
        }

        var options = await provider.FindDirectAcquisitionsAsync(request.WorkId, format.MediaType, cancellationToken);
        var option = options.FirstOrDefault(option =>
            string.Equals(option.ProviderResultId, providerResultId, StringComparison.Ordinal));
        if (option is null)
        {
            return ManualImportResult.Invalid("That option is no longer available.");
        }

        DirectAcquisitionFile file;
        try
        {
            file = await provider.FetchAsync(option, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            return ManualImportResult.Invalid($"The file could not be fetched: {exception.Message}");
        }

        await using var content = file.Content;
        var work = await workLookup.FindAsync(request.WorkId, cancellationToken);

        return await staging.StageAsync(
            request,
            format,
            content,
            file.Filename,
            providerId,
            AuditActions.DirectAcquisitionStaged,
            candidateTitle: work?.Title,
            candidateAuthor: work?.PrimaryAuthor,
            cancellationToken);
    }
}
