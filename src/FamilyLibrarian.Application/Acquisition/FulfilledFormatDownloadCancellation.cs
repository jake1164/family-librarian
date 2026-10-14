using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Once a request format is in the library, any other provider still downloading it is only spending bandwidth (and
/// on a metered source, quota) on a copy the duplicate check would then discard. This cancels and releases every job
/// for that format that has not finished, including one waiting on a person, whose verification nobody needs now.
/// </summary>
/// <remarks>
/// Best effort throughout: the format is already fulfilled, so a provider that cannot be reached only means its copy
/// may still arrive, and the publishing duplicate check handles that.
/// </remarks>
public sealed class FulfilledFormatDownloadCancellation(
    IProviderAcquisitionJobStore jobs,
    IExternalProviderStore providers,
    IExternalProviderClient client,
    ICredentialProtector protector,
    IAuditWriter audit,
    IClock clock,
    ProviderInteractionClaimService? claims = null)
{
    public const string ErrorCode = "FORMAT_FULFILLED";

    public async Task CancelRemainingAsync(Guid requestFormatId, CancellationToken cancellationToken)
    {
        var active = await jobs.ListActiveForFormatAsync(requestFormatId, cancellationToken);
        foreach (var job in active)
        {
            await ReleaseAtProviderAsync(job, cancellationToken);
            job.Cancel("Another copy of this book is already in the library.", clock.UtcNow, ErrorCode);
            await jobs.SaveChangesAsync(cancellationToken);
            if (claims is not null)
            {
                await claims.ReleaseForJobAsync(job.Id, cancellationToken);
            }

            await audit.WriteAsync(
                AuditActions.ProviderJobCancelledFormatFulfilled, AuditSubjectTypes.ProviderInteraction, job.Id.ToString(),
                new { job.Id, job.RequestId, job.RequestFormatId, job.ProviderId }, cancellationToken);
        }
    }

    private async Task ReleaseAtProviderAsync(ProviderAcquisitionJob job, CancellationToken cancellationToken)
    {
        if (job.ProviderJobId is not { } providerJobId)
        {
            return;
        }

        var provider = await providers.FindAsync(job.ExternalProviderId, cancellationToken);
        if (provider is null)
        {
            return;
        }

        try
        {
            var apiKey = provider.HasApiKey
                ? protector.Unprotect(ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
                : null;
            await client.CancelAcquireAsync(provider.BaseUrl, apiKey, providerJobId, cancellationToken);
            await client.TryDeleteAcquireAsync(provider.BaseUrl, apiKey, providerJobId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The local job is still cancelled; a copy that arrives anyway is caught by the publishing duplicate check.
        }
    }
}
