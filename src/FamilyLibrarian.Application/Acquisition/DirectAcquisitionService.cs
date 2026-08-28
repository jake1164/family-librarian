using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Fetches a file from a bundled free-source provider (e.g. Project Gutenberg) or a
/// registered external provider, and stages it exactly like a manual upload.
/// </summary>
/// <remarks>
/// The option is re-derived server-side from the provider's own search
/// rather than trusting anything client-supplied: the browser only ever
/// sends <c>providerId</c>/<c>providerResultId</c>, never a download URL —
/// re-resolving by the request's own Work/media type is what keeps a
/// tampered or stale client request from making the host fetch an arbitrary
/// address. A <c>providerId</c> that doesn't match any compile-time
/// (DI-registered) provider falls back to a lookup among admin-registered
/// <see cref="Domain.Providers.ExternalProvider"/> rows — the two are otherwise
/// handled identically once a file is in hand.
/// </remarks>
public sealed class DirectAcquisitionService(
    IRequestRepository requests,
    IEnumerable<IDirectAcquisitionProvider> providers,
    IExternalProviderStore externalProviders,
    IExternalProviderClient externalProviderClient,
    PrivateEgressRouteResolver routeResolver,
    ICredentialProtector protector,
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

        var work = await workLookup.FindAsync(request.WorkId, cancellationToken);

        if (provider is not null)
        {
            var options = await provider.FindDirectAcquisitionsAsync(request.WorkId, format.MediaType, cancellationToken);
            var option = options.FirstOrDefault(option =>
                string.Equals(option.ProviderResultId, providerResultId, StringComparison.Ordinal));
            if (option is null)
            {
                return ManualImportResult.Invalid("That option is no longer available.");
            }

            IReadOnlyList<DirectAcquisitionFile> files;
            try
            {
                files = await provider.FetchAsync(option, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                return ManualImportResult.Invalid($"The file could not be fetched: {exception.Message}");
            }

            if (files.Count == 0)
            {
                return ManualImportResult.Invalid("The provider returned no file for that option.");
            }

            if (files.Count == 1)
            {
                await using var content = files[0].Content;
                return await staging.StageAsync(
                    request, format, content, files[0].Filename, providerId, AuditActions.DirectAcquisitionStaged,
                    candidateTitle: work?.Title, candidateAuthor: work?.PrimaryAuthor, cancellationToken);
            }

            try
            {
                return await staging.StageBundleAsync(
                    request, format, files, providerId, AuditActions.DirectAcquisitionStaged,
                    candidateTitle: work?.Title, candidateAuthor: work?.PrimaryAuthor, cancellationToken);
            }
            finally
            {
                foreach (var file in files)
                {
                    await file.Content.DisposeAsync();
                }
            }
        }

        var externalProvider = await externalProviders.FindByProviderIdAsync(providerId, cancellationToken);
        if (externalProvider is null || !externalProvider.IsEnabled)
        {
            return ManualImportResult.Invalid("That provider is not available.");
        }

        var resolution = routeResolver.Resolve(externalProvider.EffectiveEgressPolicy);
        if (!resolution.IsAllowed)
        {
            return ManualImportResult.Invalid(resolution.BlockedReason!);
        }

        var apiKey = externalProvider.HasApiKey
            ? protector.Unprotect(
                ExternalProviderSecretPurposes.ApiKey, externalProvider.ProtectedApiKey!, externalProvider.ApiKeyFormatVersion)
            : null;

        IReadOnlyList<ExternalProviderCandidate> candidates;
        try
        {
            candidates = await externalProviderClient.SearchAsync(
                externalProvider.BaseUrl,
                apiKey,
                new ExternalProviderSearchRequest(
                    requestId, format.MediaType, work?.Title ?? string.Empty,
                    work?.PrimaryAuthor is null ? [] : [work.PrimaryAuthor], Isbn13: null),
                resolution.Route!,
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return ManualImportResult.Invalid($"The provider could not be reached: {exception.Message}");
        }

        var candidate = candidates.FirstOrDefault(
            candidate => string.Equals(candidate.ProviderReference, providerResultId, StringComparison.Ordinal));
        if (candidate is null)
        {
            return ManualImportResult.Invalid("That option is no longer available.");
        }

        ExternalProviderArtifact artifact;
        try
        {
            artifact = await externalProviderClient.AcquireAsync(
                externalProvider.BaseUrl, apiKey, candidate.ProviderReference, format.MediaType, resolution.Route!,
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            return ManualImportResult.Invalid($"The file could not be fetched: {exception.Message}");
        }

        await using var artifactContent = artifact.Content;
        return await staging.StageAsync(
            request, format, artifactContent, artifact.Filename, providerId,
            AuditActions.ExternalProviderAcquisitionStaged,
            candidateTitle: work?.Title, candidateAuthor: work?.PrimaryAuthor, cancellationToken,
            egressPolicy: externalProvider.EffectiveEgressPolicy);
    }
}
