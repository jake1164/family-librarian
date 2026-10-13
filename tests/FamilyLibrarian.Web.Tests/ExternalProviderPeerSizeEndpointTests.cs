using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class ExternalProviderPeerSizeEndpointTests
{
    [TestMethod]
    [DataRow(3)]
    [DataRow(2)]
    public async Task AutomaticJobSnapshotsOnlyEnoughReviewableSameFormatPeers(int peerCount)
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var candidates = new List<ExternalProviderCandidate> { Candidate("chosen", true, 600_000, "epub") };
        for (var i = 0; i < peerCount; i++) candidates.Add(Candidate($"peer-{i}", false, 4_000_000, "epub"));
        candidates.Add(Candidate("other-format", false, 20_000_000, "pdf"));
        candidates.Add(Candidate("other-language", false, 30_000_000, "epub", "fr"));
        candidates.Add(VariantSearchProviderClient.Unrelated("unrelated"));
        var client = new VariantSearchProviderClient(_ => candidates);
        await VariantScenario.RunAsync(fixture, client, "example-peer-source",
            afterwards: async (factory, _, request) =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var job = await database.ProviderAcquisitionJobs.SingleAsync(row => row.RequestId == request.Id);
                Assert.IsTrue(job.IsAutomaticAcquisition);
                Assert.AreEqual("chosen", job.CandidateReference);
                var median = typeof(ProviderAcquisitionJob).GetProperty("PeerMedianSizeBytes");
                var count = typeof(ProviderAcquisitionJob).GetProperty("PeerCount");
                Assert.IsNotNull(median, "The durable job must expose the peer-size snapshot.");
                Assert.IsNotNull(count);
                Assert.AreEqual(peerCount >= 3 ? 4_000_000L : (long?)null, median.GetValue(job));
                Assert.AreEqual(peerCount >= 3 ? peerCount : (int?)null, count!.GetValue(job));
            });
    }

    private static ExternalProviderCandidate Candidate(string id, bool author, long size, string format, string language = "en")
    {
        var candidate = VariantSearchProviderClient.Hobbit(id, author, language);
        return candidate with { Release = candidate.Release! with { Name = $"Tolkien - The Hobbit copy {id}", SizeBytes = size, Format = format } };
    }
}
