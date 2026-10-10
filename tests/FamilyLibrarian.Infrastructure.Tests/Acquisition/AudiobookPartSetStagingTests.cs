using System.Text;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

/// <summary>
/// An automatic audiobook set stages each numbered part as one file of a shared
/// bundle, so the existing "publish only when every sibling is trusted" rule
/// is the whole all-or-nothing mechanism.
/// </summary>
[TestClass]
public sealed class AudiobookPartSetStagingTests
{
    private static readonly int[] Sequence = [1, 2];
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task APartIsStagedAsTheNumberedTrackOfTheSetsBundle()
    {
        var context = new TestContext();
        var (request, format) = TestContext.SeedRequest();
        var setId = Guid.NewGuid();

        var first = await context.StagePartAsync(request, format, new AudiobookPartSetSlot(setId, 1, 2), "part1.m4b");
        var second = await context.StagePartAsync(request, format, new AudiobookPartSetSlot(setId, 2, 2), "part2.m4b");

        Assert.AreEqual(ManualImportOutcome.Success, first.Outcome);
        Assert.AreEqual(ManualImportOutcome.Success, second.Outcome);
        Assert.HasCount(2, context.Repository.Assets);
        Assert.IsTrue(context.Repository.Assets.All(asset => asset.BundleId == setId));
        CollectionAssert.AreEqual(Sequence, context.Repository.Assets.Select(asset => asset.BundleSequence!.Value).ToArray());
        Assert.IsTrue(context.Repository.Assets.All(asset => asset.BundleTrackCount == 2));
    }

    [TestMethod]
    public async Task APartThatArrivesAsSeveralFilesIsRejectedAndNothingIsKept()
    {
        var context = new TestContext();
        var (request, format) = TestContext.SeedRequest();
        var job = TestContext.NewJob(request, format);

        var result = await context.Staging.StageExternalBundleAsync(
            request, format, Files("a.m4b", "b.m4b"), "example-source", "external.staged", job, CancellationToken.None,
            new AudiobookPartSetSlot(Guid.NewGuid(), 1, 2));

        Assert.AreEqual(ManualImportOutcome.Invalid, result.Outcome);
        StringAssert.Contains(result.Error, "single file");
        Assert.HasCount(0, context.Repository.Assets);
        Assert.AreEqual(2, context.StagingStore.DeleteCount);
    }

    [TestMethod]
    public async Task WithoutASlotASingleFileIsStillAnOrdinaryUnbundledAsset()
    {
        var context = new TestContext();
        var (request, format) = TestContext.SeedRequest();
        var job = TestContext.NewJob(request, format);

        var result = await context.Staging.StageExternalBundleAsync(
            request, format, Files("only.m4b"), "example-source", "external.staged", job, CancellationToken.None);

        Assert.AreEqual(ManualImportOutcome.Success, result.Outcome);
        Assert.IsNull(context.Repository.Assets.Single().BundleId);
    }

    private static async IAsyncEnumerable<DirectAcquisitionFile> Files(params string[] names)
    {
        foreach (var name in names)
        {
            yield return new DirectAcquisitionFile(new MemoryStream(Encoding.UTF8.GetBytes(name)), name);
        }

        await Task.CompletedTask;
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            Repository = new FakeAcquisitionRepository();
            StagingStore = new FakeStagingStore();
            Staging = new AcquisitionStagingService(
                Repository, StagingStore, new AlwaysHealthyBoundaryGuard(), new ManualImportPolicy(),
                new NullAuditWriter(), new FixedClock());
        }

        public FakeAcquisitionRepository Repository { get; }

        public FakeStagingStore StagingStore { get; }

        public AcquisitionStagingService Staging { get; }

        public static (BookRequest Request, RequestFormat Format) SeedRequest()
        {
            var request = new BookRequest(Guid.NewGuid(), Guid.NewGuid(), [RequestMediaType.Audiobook], null, Now);
            return (request, request.Formats.Single());
        }

        public static ProviderAcquisitionJob NewJob(BookRequest request, RequestFormat format) => new(
            request.Id, format.Id, Guid.NewGuid(), "example-source", null, Guid.NewGuid().ToString("N"),
            "ref", null, null, Now);

        public Task<ManualImportResult> StagePartAsync(
            BookRequest request, RequestFormat format, AudiobookPartSetSlot slot, string filename) =>
            Staging.StageExternalBundleAsync(
                request, format, Files(filename), "example-source", "external.staged",
                TestContext.NewJob(request, format), CancellationToken.None, slot);
    }

    private sealed class FakeAcquisitionRepository : IAcquisitionRepository
    {
        public List<AcquisitionJob> Jobs { get; } = [];

        public List<MediaAsset> Assets { get; } = [];

        public Task<bool> ExistsAssetWithChecksumForFormatAsync(
            Guid requestFormatId, string sha256, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<MediaAssetAdminView>> ListActiveAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MediaAssetAdminView>> ListRecentAsync(
            int maximumCount, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddJob(AcquisitionJob job) => Jobs.Add(job);

        public void AddAsset(MediaAsset asset) => Assets.Add(asset);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeStagingStore : IAssetStagingStore
    {
        public int DeleteCount { get; private set; }

        public Task<StagedFile> WriteToQuarantineAsync(
            Stream content, string originalFilename, long maxSizeBytes, CancellationToken cancellationToken) =>
            Task.FromResult(new StagedFile(
                $"{Guid.NewGuid():N}{Path.GetExtension(originalFilename)}", 1024,
                Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), "audio/mp4"));

        public Task<Stream> OpenAsync(
            MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MoveAsync(
            MediaAssetStorageState fromZone, MediaAssetStorageState toZone, string storedFilename,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(
            MediaAssetStorageState zone, string storedFilename, CancellationToken cancellationToken)
        {
            DeleteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysHealthyBoundaryGuard : IAcquisitionBoundaryGuard
    {
        public Task<bool> CanAcceptNewArtifactAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class NullAuditWriter : IAuditWriter
    {
        public Task WriteAsync(
            string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
