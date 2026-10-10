using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class AudiobookPartSetAcquisitionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly int[] Numbers = [1, 2, 3];
    private static readonly string[] ResultIds = ["part-1", "part-2", "part-3"];
    private static readonly Guid RequestId = Guid.NewGuid();
    private static readonly Guid FormatId = Guid.NewGuid();

    [TestMethod]
    public async Task EveryPartIsSubmittedInOrderAsAMemberOfOneSet()
    {
        var context = new TestContext();

        var result = await context.Service.StartAsync(
            RequestId, FormatId, "example-source", Selection(3), CancellationToken.None);

        Assert.IsTrue(result.Started);
        Assert.IsNotNull(result.SetId);
        Assert.HasCount(3, context.Acquirer.Members);
        Assert.IsTrue(context.Acquirer.Members.All(member => member.SetId == result.SetId));
        CollectionAssert.AreEqual(Numbers, context.Acquirer.Members.Select(member => member.Number).ToArray());
        Assert.IsTrue(context.Acquirer.Members.All(member => member.Total == 3));
        CollectionAssert.AreEqual(ResultIds, context.Acquirer.Members[0].MemberResultIds.ToArray());
        Assert.IsTrue(context.Jobs.Jobs.All(job => job.LifecycleState == ProviderAcquisitionJobLifecycleState.Queued));
    }

    [TestMethod]
    public async Task APartThatCannotBeStartedCancelsThePartsAlreadySubmitted()
    {
        var context = new TestContext { FailOnPart = 3 };

        var result = await context.Service.StartAsync(
            RequestId, FormatId, "example-source", Selection(3), CancellationToken.None);

        Assert.IsFalse(result.Started);
        StringAssert.Contains(result.Error, "Part 3 of 3");
        Assert.HasCount(2, context.Jobs.Jobs);
        Assert.IsTrue(context.Jobs.Jobs.All(job => job.LifecycleState == ProviderAcquisitionJobLifecycleState.Cancelled));
        Assert.IsTrue(context.Jobs.Jobs.All(job => job.NextPollAtUtc is null));
        Assert.IsTrue(context.Jobs.Jobs.All(job => job.ErrorCode == "PART_SET_ABANDONED"));
    }

    [TestMethod]
    public async Task AnUnexpectedFailureAlsoCancelsTheSubmittedPartsAndRethrows()
    {
        var context = new TestContext { ThrowOnPart = 2 };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.Service.StartAsync(RequestId, FormatId, "example-source", Selection(2), CancellationToken.None));

        Assert.HasCount(1, context.Jobs.Jobs);
        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Cancelled, context.Jobs.Jobs[0].LifecycleState);
    }

    [TestMethod]
    public void CancellingAFinishedJobLeavesItAlone()
    {
        var job = NewJob(Guid.NewGuid(), 1, 2);
        job.RecordFailure("X", "already failed", false, null, null, Now);

        job.Cancel("set abandoned", Now);

        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Failed, job.LifecycleState);
        Assert.AreEqual("X", job.ErrorCode);
    }

    [TestMethod]
    public void AJobCannotBeAssignedAnImpossiblePosition()
    {
        var job = NewJob(null, 0, 0);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => job.AssignToPartSet(Guid.NewGuid(), 3, 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => job.AssignToPartSet(Guid.NewGuid(), 1, 1));
        Assert.ThrowsExactly<ArgumentException>(() => job.AssignToPartSet(Guid.Empty, 1, 2));
    }

    private static AudiobookPartSetSelection Selection(int total)
    {
        var parts = Enumerable.Range(1, total).Select(number =>
        {
            var name = $"Book.{number}.of.{total}";
            return new FulfillmentOption(
                "example-source", $"part-{number}", Guid.Empty, null, RequestMediaType.Audiobook,
                OptionKind.DirectAcquisition, AcquisitionMethod.DirectDownload, "m4b", null, null, null, 0m, null,
                null, null, null, null,
                MatchBasis: BookMatchBasis.StrictTitle, ReleaseName: name, HasPlausibleTitle: true,
                AudiobookPart: ExternalAudiobookPartEvidence.Parse(name), FragmentOnlyConcern: true);
        }).ToArray();
        return new AudiobookPartSetSelection(parts);
    }

    private static ProviderAcquisitionJob NewJob(Guid? setId, int number, int total)
    {
        var job = new ProviderAcquisitionJob(
            RequestId, FormatId, Guid.NewGuid(), "example-source", null, Guid.NewGuid().ToString("N"),
            $"part-{number}", null, null, Now);
        if (setId is { } id)
        {
            job.AssignToPartSet(id, number, total);
        }

        return job;
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            Jobs = new FakeJobStore();
            Acquirer = new FakeAcquirer(this);
            Service = new AudiobookPartSetAcquisitionService(Acquirer, Jobs, new FixedClock());
        }

        public int? FailOnPart { get; init; }

        public int? ThrowOnPart { get; init; }

        public FakeJobStore Jobs { get; }

        public FakeAcquirer Acquirer { get; }

        public AudiobookPartSetAcquisitionService Service { get; }
    }

    private sealed class FakeAcquirer(TestContext context) : IAudiobookPartSetMemberAcquirer
    {
        public List<AudiobookPartSetMember> Members { get; } = [];

        public Task<ManualImportResult> AcquireMemberAsync(
            Guid requestId, Guid requestFormatId, string providerId, string providerResultId,
            AudiobookPartSetMember member, CancellationToken cancellationToken)
        {
            Members.Add(member);
            if (member.Number == context.ThrowOnPart)
            {
                throw new InvalidOperationException("provider exploded");
            }

            if (member.Number == context.FailOnPart)
            {
                return Task.FromResult(ManualImportResult.Invalid("That option is no longer available."));
            }

            var job = NewJob(member.SetId, member.Number, member.Total);
            context.Jobs.Add(job);
            return Task.FromResult(ManualImportResult.AcquisitionInProgress(job.Id));
        }
    }

    private sealed class FakeJobStore : IProviderAcquisitionJobStore
    {
        public List<ProviderAcquisitionJob> Jobs { get; } = [];

        public Task<ProviderAcquisitionJob?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Jobs.FirstOrDefault(job => job.Id == id));

        public Task<ProviderAcquisitionJob?> FindByIdempotencyKeyAsync(
            Guid externalProviderId, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProviderAcquisitionJob>> ListDueForPollAsync(
            DateTimeOffset asOfUtc, int maximumCount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProviderAcquisitionJob>> ListWaitingForInteractionAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> HasLeftWaitingSinceAsync(
            Guid externalProviderId, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProviderAcquisitionJob>> ListByPartSetAsync(
            Guid partSetId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderAcquisitionJob>>(
                Jobs.Where(job => job.PartSetId == partSetId).OrderBy(job => job.PartNumber).ToArray());

        public void Add(ProviderAcquisitionJob job) => Jobs.Add(job);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
