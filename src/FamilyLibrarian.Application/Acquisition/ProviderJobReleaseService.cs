using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <param name="Cancelled">Downloads that were still running, now cancelled here and at the provider.</param>
/// <param name="Released">Finished or failed downloads whose files the provider was asked to delete.</param>
/// <param name="Failed">Jobs whose provider could not be reached or refused; its own retention cleans those up.</param>
public sealed record ProviderJobReleaseResult(int Cancelled, int Released, int Failed);

/// <summary>
/// Tells every provider to let go of every download Family Librarian knows about: running ones are cancelled, and every
/// job's staged files are deleted through the provider's own protocol (<c>DELETE /acquire/{id}</c>), so each provider
/// removes what it keeps in its own way. An administrator uses it to reset the installation without leaving downloaded but
/// never-approved files behind at the providers.
/// </summary>
/// <remarks>
/// Best effort and idempotent: a provider that is down is counted as failed, a job the provider no longer has counts as
/// released, and running it again is harmless. It never touches files FL itself holds (those belong to the security queue).
/// </remarks>
public sealed class ProviderJobReleaseService(
    IProviderAcquisitionJobStore jobs,
    IExternalProviderStore providers,
    IExternalProviderClient client,
    ICredentialProtector protector,
    IAuditWriter audit,
    IClock clock,
    ProviderInteractionClaimService? claims = null)
{
    public const string ErrorCode = "RELEASED_BY_ADMIN";

    private const int MaximumJobs = 1_000;

    public async Task<ProviderJobReleaseResult> ReleaseAllAsync(CancellationToken cancellationToken)
    {
        int cancelled = 0, released = 0, failed = 0;
        foreach (var job in await jobs.ListWithProviderJobAsync(MaximumJobs, cancellationToken))
        {
            var wasRunning = job.LifecycleState is
                ProviderAcquisitionJobLifecycleState.Queued or ProviderAcquisitionJobLifecycleState.Running or
                ProviderAcquisitionJobLifecycleState.Waiting;
            if (!await ReleaseAtProviderAsync(job, cancelFirst: wasRunning, cancellationToken))
            {
                failed++;
                continue;
            }

            if (wasRunning)
            {
                job.Cancel("All downloads were released by an administrator.", clock.UtcNow, ErrorCode);
                await jobs.SaveChangesAsync(cancellationToken);
                if (claims is not null)
                {
                    await claims.ReleaseForJobAsync(job.Id, cancellationToken);
                }

                cancelled++;
            }
            else
            {
                released++;
            }
        }

        await audit.WriteAsync(
            AuditActions.ProviderJobsReleasedByAdmin, AuditSubjectTypes.ProviderInteraction, null,
            new { cancelled, released, failed }, cancellationToken);
        return new ProviderJobReleaseResult(cancelled, released, failed);
    }

    private async Task<bool> ReleaseAtProviderAsync(
        ProviderAcquisitionJob job, bool cancelFirst, CancellationToken cancellationToken)
    {
        if (job.ProviderJobId is not { } providerJobId)
        {
            return true;
        }

        try
        {
            var provider = await providers.FindAsync(job.ExternalProviderId, cancellationToken);
            if (provider is null)
            {
                return false;
            }

            var apiKey = provider.HasApiKey
                ? protector.Unprotect(ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
                : null;
            if (cancelFirst)
            {
                await client.CancelAcquireAsync(provider.BaseUrl, apiKey, providerJobId, cancellationToken);
            }

            return await client.TryDeleteAcquireAsync(provider.BaseUrl, apiKey, providerJobId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }
}
