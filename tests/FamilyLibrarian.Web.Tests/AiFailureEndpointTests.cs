using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class AiFailureEndpointTests
{
    [TestMethod]
    public async Task DiagnosticCategoriesAreDisjointAndFilteredBeforePaginationIncludingLegacyReasonsAndCancellation()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var admin = await fixture.CreateAdminClientAsync();
        using var scope = fixture.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IModelUsageLedger>();
        var now = DateTimeOffset.UtcNow.AddMinutes(-2);
        async Task Add(ModelCallOutcome outcome, ModelFailureReason? reason, DateTimeOffset time)
        {
            var call = new ModelUsageRecord(ModelRole.SearchGuidance, ModelBudgetClass.BackgroundValidation, null, "example-model", false, 100, time);
            await ledger.AddAsync(call, CancellationToken.None);
            if (outcome != ModelCallOutcome.Pending)
                await ledger.CompleteAsync(call.Id, outcome, 123, null, CancellationToken.None, reason);
        }
        for (var i = 0; i < 52; i++)
        {
            await Add(ModelCallOutcome.SchemaRejected, i == 0 ? null : ModelFailureReason.RepeatedSearch, now);
            await Add(ModelCallOutcome.Timeout, null, now.AddMinutes(1));
        }
        await Add(ModelCallOutcome.TransportFailure, ModelFailureReason.Cancelled, now);
        await Add(ModelCallOutcome.Timeout, ModelFailureReason.Cancelled, now);
        await Add((ModelCallOutcome)999, null, now);
        await Add(ModelCallOutcome.Succeeded, null, now);
        await Add(ModelCallOutcome.Pending, null, now);
        const string path = "/api/v1/admin/model-gateway/failures?role=SearchGuidance";
        var allIds = new HashSet<Guid>();
        foreach (var (category, expected) in new[] { ("RejectedReply", 52), ("ProviderError", 52), ("Cancelled", 2), ("Unknown", 1) })
        {
            var first = (await admin.GetFromJsonAsync<ModelCallFailuresResponse>($"{path}&category={category}"))!;
            var rows = first.Rows.ToList();
            if (first.HasMore)
            {
                Assert.AreEqual(50, rows.Count);
                var second = (await admin.GetFromJsonAsync<ModelCallFailuresResponse>(
                    $"{path}&category={category}&page=2&until={Uri.EscapeDataString(first.UntilUtc.ToString("O"))}"))!;
                Assert.IsFalse(second.HasMore);
                rows.AddRange(second.Rows);
            }
            Assert.AreEqual(expected, rows.Count, $"Filter {category} must precede pagination, even when other categories are newer.");
            foreach (var row in rows)
            {
                Assert.AreEqual(category, ModelCallCategories.Classify(row.Outcome, row.ReasonCode));
                Assert.IsTrue(allIds.Add(row.Id), "Categories must never overlap.");
            }
        }
        Assert.AreEqual(107, allIds.Count);
        foreach (var category in new[] { "Failed", "Succeeded", "AcceptedReply", "Pending", "rejectedreply", "999" })
            Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.GetAsync($"{path}&category={category}")).StatusCode);
    }

    internal static async Task<MediaAsset> SeedTrackAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var work = new Work("Diagnostic track fixture", null, null, null, PublicationStatus.Published, now);
        var request = new BookRequest(await database.Users.Select(user => user.Id).FirstAsync(), work.Id, [RequestMediaType.Audiobook], null, now);
        var asset = new MediaAsset(work.Id, null, RequestMediaType.Audiobook, ".mp3", "private-file-name.mp3", "private-stored-name.mp3",
            600000, new string('a', 64), "audio/mpeg", request.Formats.Single().Id, null, now,
            bundleId: Guid.NewGuid(), bundleSequence: 12, bundleTrackCount: 80, origin: AssetOrigin.ExternalProvider);
        database.Works.Add(work);
        database.BookRequests.Add(request);
        database.MediaAssets.Add(asset);
        await database.SaveChangesAsync();
        return asset;
    }

    [TestMethod]
    public async Task FileFailuresExposeTrustedBookAndTrackReferencesWithoutFilenamesAndSurviveMissingFiles()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var admin = await fixture.CreateAdminClientAsync();
        var asset = await SeedTrackAsync(fixture.Services);
        using var scope = fixture.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IModelUsageLedger>();
        var record = new ModelUsageRecord(ModelRole.ArtifactValidationDecision, ModelBudgetClass.BackgroundValidation, null,
            "example-model", false, 100, DateTimeOffset.UtcNow, asset.Id);
        await ledger.AddAsync(record, CancellationToken.None);
        await ledger.CompleteAsync(record.Id, ModelCallOutcome.SchemaRejected, 123, null, CancellationToken.None, ModelFailureReason.UngroundedConcern);
        const string path = "/api/v1/admin/model-gateway/failures?role=ArtifactValidationDecision";
        var row = (await admin.GetFromJsonAsync<ModelCallFailuresResponse>(path))!.Rows.Single();
        Assert.AreEqual(asset.Id, row.MediaAssetId);
        Assert.AreEqual(asset.WorkId, row.Artifact!.WorkId);
        Assert.AreEqual("MP3", row.Artifact.Format);
        Assert.AreEqual(12, row.Artifact.TrackNumber);
        Assert.AreEqual(80, row.Artifact.TrackCount);
        Assert.AreEqual(asset.BundleId, row.Artifact.BundleId);
        Assert.AreEqual("UngroundedConcern", row.ReasonCode);
        Assert.IsFalse((await admin.GetStringAsync(path)).Contains("private-", StringComparison.Ordinal));
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets.Where(item => item.Id == asset.Id).ExecuteDeleteAsync();
        row = (await admin.GetFromJsonAsync<ModelCallFailuresResponse>(path))!.Rows.Single();
        Assert.AreEqual(asset.Id, row.MediaAssetId);
        Assert.IsNull(row.Artifact);
    }

    [TestMethod]
    public async Task FailureHistoryIsAdminOnlyFilteredBoundedPaginatedAndPreservesLegacyUnknownReasons()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var admin = await fixture.CreateAdminClientAsync();
        using var member = await fixture.CreateUserClientAsync();
        using var anonymous = fixture.CreateAnonymousClient();
        const string path = "/api/v1/admin/model-gateway/failures";
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.GetAsync(path)).StatusCode);
        using var scope = fixture.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IModelUsageLedger>();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var expected = new HashSet<Guid>();
        for (var i = 0; i < 52; i++)
        {
            var record = new ModelUsageRecord(ModelRole.SearchGuidance, ModelBudgetClass.BackgroundValidation, null,
                "example-model", false, 100, now);
            await ledger.AddAsync(record, CancellationToken.None);
            await ledger.CompleteAsync(record.Id, ModelCallOutcome.SchemaRejected, 123, null, CancellationToken.None,
                i == 0 ? null : ModelFailureReason.RepeatedSearch);
            expected.Add(record.Id);
        }
        foreach (var (role, outcome, time) in new[] {
            (ModelRole.CandidateTriage, ModelCallOutcome.Timeout, now),
            (ModelRole.SearchGuidance, ModelCallOutcome.Succeeded, now),
            (ModelRole.SearchGuidance, ModelCallOutcome.Pending, now),
            (ModelRole.SearchGuidance, ModelCallOutcome.Timeout, now.AddDays(-2)) })
        {
            var record = new ModelUsageRecord(role, ModelBudgetClass.BackgroundValidation, null, "other", false, 100, time);
            await ledger.AddAsync(record, CancellationToken.None);
            if (outcome != ModelCallOutcome.Pending)
                await ledger.CompleteAsync(record.Id, outcome, 123, null, CancellationToken.None);
        }
        var first = (await admin.GetFromJsonAsync<ModelCallFailuresResponse>($"{path}?role=SearchGuidance"))!;
        Assert.AreEqual(50, first.Rows.Count);
        Assert.IsTrue(first.HasMore);
        var until = Uri.EscapeDataString(first.UntilUtc.ToString("O"));
        var next = (await admin.GetFromJsonAsync<ModelCallFailuresResponse>($"{path}?role=SearchGuidance&page=2&until={until}"))!;
        Assert.AreEqual(2, next.Rows.Count);
        Assert.IsFalse(next.HasMore);
        var all = first.Rows.Concat(next.Rows).ToArray();
        Assert.AreEqual(52, all.Select(row => row.Id).Distinct().Count(), "Tied timestamps must have deterministic unique ordering.");
        Assert.IsTrue(expected.SetEquals(all.Select(row => row.Id)));
        StringAssert.Contains(all.Single(row => row.ReasonCode is null).Reason, "not recorded");
        Assert.AreEqual(51, all.Count(row => row.ReasonCode == "RepeatedSearch" && row.Reason.Contains("already tried", StringComparison.Ordinal)));
        var week = (await admin.GetFromJsonAsync<ModelCallFailuresResponse>($"{path}?role=SearchGuidance&days=7&page=2"))!;
        Assert.AreEqual(3, week.Rows.Count);
        foreach (var query in new[] { "role=999", "role=2", "role=invalid", "page=0", "page=2001", "days=0", "days=91", "until=1900-01-01" })
            Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.GetAsync($"{path}?{query}")).StatusCode, query);
        Assert.AreEqual(HttpStatusCode.OK, (await admin.GetAsync($"{path}?until={Uri.EscapeDataString(first.UntilUtc.ToOffset(TimeSpan.FromHours(-4)).ToString("O"))}")).StatusCode);
        var raw = await admin.GetStringAsync(path);
        Assert.IsFalse(raw.Contains("userId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(raw.Contains("prompt", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(raw.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
    }
}
