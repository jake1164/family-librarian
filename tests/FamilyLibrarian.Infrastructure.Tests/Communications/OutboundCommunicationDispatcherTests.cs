using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Infrastructure.Tests.Communications;

[TestClass]
public sealed class OutboundCommunicationDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task WithNoProvidersACommunicationIsProcessedWithNoDeliveries()
    {
        var store = new FakeOutboundCommunicationStore();
        store.Enqueue(NewCommunication());
        var dispatcher = Create(store);

        var processed = await dispatcher.DispatchPendingAsync(CancellationToken.None);

        Assert.AreEqual(1, processed);
        var communication = store.All.Single();
        Assert.IsNotNull(communication.ProcessedAtUtc);
        Assert.AreEqual(0, communication.Deliveries.Count);
    }

    [TestMethod]
    public async Task AnEnabledProviderThatSucceedsRecordsADelivery()
    {
        var store = new FakeOutboundCommunicationStore();
        store.Enqueue(NewCommunication());
        var provider = new FakeOutboundCommunicationProvider("smtp", enabled: true, SendResult.Success());
        var dispatcher = Create(store, provider);

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        var delivery = store.All.Single().Deliveries.Single();
        Assert.AreEqual("smtp", delivery.ProviderId);
        Assert.IsTrue(delivery.Succeeded);
    }

    [TestMethod]
    public async Task ADisabledProviderIsNeverAttempted()
    {
        var store = new FakeOutboundCommunicationStore();
        store.Enqueue(NewCommunication());
        var provider = new FakeOutboundCommunicationProvider("smtp", enabled: false, SendResult.Success());
        var dispatcher = Create(store, provider);

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        Assert.AreEqual(0, provider.SendCallCount);
        Assert.AreEqual(0, store.All.Single().Deliveries.Count);
    }

    [TestMethod]
    public async Task AProviderThatThrowsRecordsAFailedDeliveryAndDoesNotBlockTheRestOfTheBatch()
    {
        var store = new FakeOutboundCommunicationStore();
        store.Enqueue(NewCommunication());
        store.Enqueue(NewCommunication());
        var provider = new ThrowingOutboundCommunicationProvider();
        var dispatcher = Create(store, provider);

        var processed = await dispatcher.DispatchPendingAsync(CancellationToken.None);

        Assert.AreEqual(2, processed);
        Assert.IsTrue(store.All.All(communication => communication.ProcessedAtUtc is not null));
        Assert.IsTrue(store.All.All(communication => communication.Deliveries.Single().Succeeded == false));
    }

    private static OutboundCommunicationDispatcher Create(
        FakeOutboundCommunicationStore store, params IOutboundCommunicationProvider[] providers) =>
        new(store, providers, new FixedClock());

    private static OutboundCommunication NewCommunication() =>
        new(Guid.NewGuid(), OutboundCommunicationTypes.RequestStatusChanged, "Body", "Subject", null, null, null, Now);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakeOutboundCommunicationStore : IOutboundCommunicationStore
    {
        public List<OutboundCommunication> All { get; } = [];

        public void Enqueue(OutboundCommunication communication) => All.Add(communication);

        public Task EnqueueAsync(OutboundCommunication communication, CancellationToken cancellationToken)
        {
            All.Add(communication);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboundCommunication>> GetUnprocessedBatchAsync(
            int maxCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboundCommunication>>(
                All.Where(communication => communication.ProcessedAtUtc is null).Take(maxCount).ToList());

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeOutboundCommunicationProvider(string providerId, bool enabled, SendResult result)
        : IOutboundCommunicationProvider
    {
        public int SendCallCount { get; private set; }

        public string ProviderId => providerId;

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(enabled);

        public Task<SendResult> SendAsync(OutboundCommunication communication, CancellationToken cancellationToken)
        {
            SendCallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingOutboundCommunicationProvider : IOutboundCommunicationProvider
    {
        public string ProviderId => "broken";

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<SendResult> SendAsync(OutboundCommunication communication, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Provider is broken.");
    }
}
