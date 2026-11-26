using FamilyLibrarian.Application.Concurrency;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Coordinates initial submission and recovery of the same durable job within this host.</summary>
internal static class ProviderAcquisitionSubmissionLock
{
    private static readonly KeyedAsyncLock<Guid> Jobs = new();

    public static Task<IDisposable> AcquireAsync(Guid jobId, CancellationToken cancellationToken) =>
        Jobs.AcquireAsync(jobId, cancellationToken);
}
