using System.Reflection;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class FulfilledFormatDownloadCancellationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 22, 0, 0, TimeSpan.Zero);

    // The lab: the job that delivered "The Sum of All Fears" was still finishing when its file reached CWA, and the
    // cancellation marked that very job "cancelled, format fulfilled".
    [TestMethod]
    public async Task TheJobThatDeliveredTheFileIsNotCancelledButARivalDownloadIs()
    {
        var formatId = Guid.NewGuid();
        var deliveredAssetId = Guid.NewGuid();
        var delivering = Job(formatId, "delivering");
        delivering.AddOutput("primary", ProviderOutputKind.File, "primary", "book.epub", "application/epub+zip",
            1, null, null, null, null, Now).LinkAsset(deliveredAssetId);
        var rival = Job(formatId, "rival");
        var store = JobStore.Create([delivering, rival]);
        var service = new FulfilledFormatDownloadCancellation(
            store, Unused<IExternalProviderStore>(), Unused<IExternalProviderClient>(), Unused<ICredentialProtector>(),
            new NullAudit(), new FixedClock());

        await service.CancelRemainingAsync(formatId, [deliveredAssetId], CancellationToken.None);

        Assert.AreNotEqual(ProviderAcquisitionJobLifecycleState.Cancelled, delivering.LifecycleState);
        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Cancelled, rival.LifecycleState);
        Assert.AreEqual(FulfilledFormatDownloadCancellation.ErrorCode, rival.ErrorCode);
    }

    private static ProviderAcquisitionJob Job(Guid formatId, string key) => new(
        Guid.NewGuid(), formatId, Guid.NewGuid(), "example-source", null, key, $"c_{key}", null, null, Now,
        isAutomaticAcquisition: true);

    private static T Unused<T>() where T : class => DispatchProxy.Create<T, UnusedProxy>();

    public class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new AssertFailedException($"{targetMethod?.Name} must not be called when no job has a provider id.");
    }

    public class JobStore : DispatchProxy
    {
        private IReadOnlyList<ProviderAcquisitionJob> jobs = [];

        public static IProviderAcquisitionJobStore Create(IReadOnlyList<ProviderAcquisitionJob> active)
        {
            var proxy = DispatchProxy.Create<IProviderAcquisitionJobStore, JobStore>();
            ((JobStore)(object)proxy).jobs = active;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IProviderAcquisitionJobStore.ListActiveForFormatAsync) =>
                Task.FromResult<IReadOnlyList<ProviderAcquisitionJob>>([.. jobs.Where(job => job.RequestFormatId == (Guid)args![0]!)]),
            nameof(IProviderAcquisitionJobStore.SaveChangesAsync) => Task.CompletedTask,
            _ => throw new AssertFailedException($"{targetMethod?.Name} is not expected.")
        };
    }

    private sealed class NullAudit : IAuditWriter
    {
        public Task WriteAsync(string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
