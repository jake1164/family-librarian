using System.Net.Http.Json;
using System.Net;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class ExternalProviderAcquisitionQuestionEndpointTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnAnswerNeverAuthorizesAChangedWorkOrAnotherLanguage(bool language)
    {
        var changed = false;
        var initial = ExternalProviderCandidate.FromSimple("offered", language ? "Iron Flame" : "The Empyrean Books 1-3", "Rebecca Yarros", "epub", 123456);
        initial = language ? initial with { Edition = new("fr", null, null, []) }
            : initial with { Release = initial.Release! with { IsCollection = true } };
        var replacement = language ? initial with { Edition = initial.Edition! with { Language = "de" } }
            : initial with { Work = initial.Work with { Title = "A different work" } };
        var submitted = await ConfidenceScenario.RunAsync([initial], "Iron Flame", "2", "Strict",
            searchScript: (_, _) => [changed ? replacement : initial],
            exercise: async (services, admin) =>
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var request = await db.BookRequests.Include(r => r.ReviewCandidates).SingleAsync();
                Assert.IsNotNull(QuestionKind(request));
                changed = true;
                (await admin.PostAsJsonAsync($"/api/v1/requests/{request.Id}/needs-review/resolve", new ResolveNeedsReviewRequest(request.ReviewCandidates.Single().Id))).EnsureSuccessStatusCode();
            });
        Assert.IsEmpty(submitted, "Consent authorizes only the offered variant and language.");
    }

    [TestMethod]
    [DataRow("AbridgedOnly")]
    [DataRow("PartsOnly")]
    public async Task ARealProductDifferenceIsAQuestionButNeverAnAutomaticDownload(string kind)
    {
        var candidate = ExternalProviderCandidate.FromSimple("variant", "Onyx Storm", "Rebecca Yarros", "m4b", 123456);
        candidate = candidate with { Release = candidate.Release! with
        { Name = kind == "PartsOnly" ? "Onyx Storm (1 of 2)" : null, IsAbridged = kind == "AbridgedOnly" } };
        var submitted = await ConfidenceScenario.RunAsync([candidate], "Onyx Storm", "", "Strict", "Audiobook",
            inspect: async db => Assert.AreEqual(kind, QuestionKind(await db.BookRequests.SingleAsync())));
        Assert.IsEmpty(submitted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OnlyActiveSharedRequestersCanAnswerAndTheFirstAnswerAppliesToAll(bool withdrawn)
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        var request = await harness.RequestHobbitAsync();
        using var reader = harness.Factory.CreateClient();
        (await reader.PostAsJsonAsync("/api/auth/login", new FamilyLibrarian.Contracts.Authentication.LoginRequest
        { Email = WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword })).EnsureSuccessStatusCode();
        reader.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(reader));
        (await reader.PostAsJsonAsync("/api/v1/requests/", new CreateBookRequestRequest(request.WorkId, ["Ebook"], null, false, false))).EnsureSuccessStatusCode();
        if (withdrawn)
            (await reader.PostAsJsonAsync($"/api/v1/requests/{request.Id}/transitions", new ChangeBookRequestStatusRequest("Cancelled", null))).EnsureSuccessStatusCode();
        var collection = WaitingRecheckClient.Collection("collection");
        harness.Client.Script = _ => [collection with { Work = collection.Work with { Title = "The Hobbit / Another Novel" } }];
        await harness.RecheckAsync();
        var state = await harness.ReadAsync(db => db.BookRequests.Include(r => r.ReviewCandidates).SingleAsync());
        Assert.AreEqual("CollectionContaining", QuestionKind(state));
        Assert.AreEqual(withdrawn ? 1 : 2, await harness.ReadAsync(db => db.OutboundCommunications.CountAsync(c => c.CommunicationType == "request.acquisition_question")));
        var candidate = state.ReviewCandidates.Single();
        var response = await reader.PostAsJsonAsync($"/api/v1/requests/{request.Id}/needs-review/resolve", new ResolveNeedsReviewRequest(candidate.Id));
        Assert.AreEqual(withdrawn ? HttpStatusCode.NotFound : HttpStatusCode.OK, response.StatusCode);
        if (withdrawn) Assert.IsEmpty(harness.Client.SubmittedCandidates);
        else
        {
            Assert.HasCount(1, harness.Client.SubmittedCandidates);
            var stale = await harness.Admin.PostAsJsonAsync($"/api/v1/requests/{request.Id}/needs-review/resolve", new ResolveNeedsReviewRequest(candidate.Id));
            Assert.AreEqual(HttpStatusCode.NotFound, stale.StatusCode);
        }
    }

    [TestMethod]
    public async Task TakingTheReservedDramatizationReverifiesTheExistingBytesWithoutDownloading()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        var request = await harness.RequestHobbitAsync();
        (await harness.Admin.PostAsJsonAsync("/api/v1/requests/", new CreateBookRequestRequest(request.WorkId, ["Audiobook"], null, false, false))).EnsureSuccessStatusCode();
        Guid assetId;
        await using (var scope = harness.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var format = await db.RequestFormats.SingleAsync(f => f.MediaType == RequestMediaType.Audiobook);
            var staging = scope.ServiceProvider.GetRequiredService<IAssetStagingStore>();
            using var bytes = new MemoryStream(M4bTestFixture.Build("The Hobbit [Dramatized Adaptation]", "J. R. R. Tolkien", "eng"));
            var file = await staging.WriteToQuarantineAsync(bytes, "copy.m4b", 1_000_000, CancellationToken.None);
            var asset = new MediaAsset(request.WorkId, null, RequestMediaType.Audiobook, ".m4b", "copy.m4b", file.StoredFilename,
                file.SizeBytes, file.Sha256, file.DetectedMimeType, format.Id, null, DateTimeOffset.UtcNow);
            db.MediaAssets.Add(asset);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<AutomatedSecurityPipeline>().EvaluateAsync(asset.Id, CancellationToken.None);
            Assert.AreEqual("identity.hold.dramatized_adaptation", asset.IdentityHoldCode);
            asset.MarkRuledOut(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }
        await harness.RecheckAsync();
        var state = await harness.ReadAsync(db => db.BookRequests.Include(r => r.ReviewCandidates).SingleAsync());
        Assert.AreEqual("DramatizedOnly", QuestionKind(state));
        var mine = await harness.Admin.GetFromJsonAsync<BookRequestListResponse>("/api/v1/me/requests");
        Assert.AreEqual("AcquisitionQuestionOpen", mine!.Active.Single().Formats.Single(f => f.MediaType == "Audiobook").ProgressCode);
        (await harness.Admin.PostAsJsonAsync($"/api/v1/requests/{request.Id}/needs-review/resolve", new ResolveNeedsReviewRequest(state.ReviewCandidates.Single().Id))).EnsureSuccessStatusCode();
        Assert.AreEqual(MediaAssetStorageState.Trusted, await harness.ReadAsync(async db => (await db.MediaAssets.SingleAsync(a => a.Id == assetId)).StorageState));
        Assert.IsEmpty(harness.Client.SubmittedCandidates);
    }

    [TestMethod]
    public async Task OnlyAReservedDramatizationIsOfferedAndKeepLookingLeavesItRuledOut()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        var requested = await harness.RequestHobbitAsync();
        (await harness.Admin.PostAsJsonAsync("/api/v1/requests/", new CreateBookRequestRequest(requested.WorkId, ["Audiobook"], null, false, false))).EnsureSuccessStatusCode();
        await using (var scope = harness.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var format = await db.RequestFormats.SingleAsync(f => f.MediaType == RequestMediaType.Audiobook);
            var asset = new MediaAsset(requested.WorkId, null, RequestMediaType.Audiobook, ".m4b", "copy.m4b", "stored.m4b", 1,
                new string('a', 64), "audio/mp4", format.Id, null, DateTimeOffset.UtcNow);
            asset.TransitionStorageState(MediaAssetStorageState.Processing, DateTimeOffset.UtcNow);
            asset.TransitionStorageState(MediaAssetStorageState.Unmatched, DateTimeOffset.UtcNow);
            asset.SetIdentityHoldCode("identity.hold.dramatized_adaptation");
            asset.MarkRuledOut(DateTimeOffset.UtcNow);
            db.MediaAssets.Add(asset);
            await db.SaveChangesAsync();
        }
        await harness.RecheckAsync();
        Assert.AreEqual("DramatizedOnly", await harness.ReadAsync(async db => QuestionKind(await db.BookRequests.SingleAsync(r => r.Id == requested.Id))));
        Assert.IsEmpty(harness.Client.SubmittedCandidates);
        (await harness.Admin.PostAsJsonAsync($"/api/v1/requests/{requested.Id}/needs-review/resolve", new ResolveNeedsReviewRequest(null))).EnsureSuccessStatusCode();
        await harness.MakeEverythingDueAsync();
        await harness.RecheckAsync();
        Assert.IsNull(await harness.ReadAsync(async db => QuestionKind(await db.BookRequests.SingleAsync(r => r.Id == requested.Id))));
        Assert.IsTrue(await harness.ReadAsync(async db => (await db.MediaAssets.SingleAsync()).RuledOutAtUtc.HasValue));
    }

    [TestMethod]
    public async Task ALanguageHeldRuledOutCopyCanBeAskedAboutButNeverDownloadsAutomatically()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        var requested = await harness.RequestHobbitAsync();
        var foreign = WaitingRecheckClient.Candidate("foreign", "The Hobbit", 123456) with { Edition = new("fr", null, null, []) };
        harness.Client.Script = _ => [foreign];
        await using (var scope = harness.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var request = await db.BookRequests.Include(r => r.Formats).SingleAsync(r => r.Id == requested.Id);
            var format = request.Formats.Single();
            request.RecordAutomaticCandidateFailure(format.Id, "waiting-source", "foreign", "Language differs.", DateTimeOffset.UtcNow);
            var asset = new MediaAsset(request.WorkId, null, RequestMediaType.Ebook, ".epub", "copy.epub", "stored.epub", 1,
                new string('a', 64), "application/epub+zip", format.Id, null, DateTimeOffset.UtcNow);
            asset.TransitionStorageState(MediaAssetStorageState.Processing, DateTimeOffset.UtcNow);
            asset.TransitionStorageState(MediaAssetStorageState.Unmatched, DateTimeOffset.UtcNow);
            asset.SetIdentityHoldCode("identity.hold.language_conflict");
            asset.MarkRuledOut(DateTimeOffset.UtcNow);
            db.MediaAssets.Add(asset);
            await db.SaveChangesAsync();
        }
        await harness.RecheckAsync();
        Assert.AreEqual("OtherLanguageOnly", await harness.ReadAsync(async db => QuestionKind(await db.BookRequests.SingleAsync(r => r.Id == requested.Id))));
        Assert.IsEmpty(harness.Client.SubmittedCandidates);
        Assert.AreEqual(MediaAssetStorageState.Unmatched, await harness.ReadAsync(async db => (await db.MediaAssets.SingleAsync()).StorageState));
    }

    [TestMethod]
    public async Task AQuestionIsNotRepeatedHasOneReminderAndIsWithdrawnForAStandalone()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        var collection = WaitingRecheckClient.Collection("collection");
        collection = collection with { Work = collection.Work with { Title = "The Hobbit / Another novel" } };
        harness.Client.Script = _ => [collection];
        var request = await harness.RequestHobbitAsync();
        await harness.RecheckAsync();
        Assert.AreEqual("CollectionContaining", await harness.ReadAsync(async db => QuestionKind(await db.BookRequests.SingleAsync(r => r.Id == request.Id))));
        await harness.MakeEverythingDueAsync();
        await harness.RecheckAsync();
        Assert.AreEqual(1, await harness.ReadAsync(db => db.OutboundCommunications.CountAsync(c => c.CommunicationType == "request.acquisition_question")));
        await harness.ReadAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE requests.book_requests SET question_asked_at_utc = now() - interval '8 days'"));
        await harness.RecheckAsync();
        await harness.RecheckAsync();
        Assert.AreEqual(2, await harness.ReadAsync(db => db.OutboundCommunications.CountAsync(c => c.CommunicationType == "request.acquisition_question")));
        harness.Client.Script = query => [WaitingRecheckClient.Identifier("standalone", query)];
        await harness.MakeEverythingDueAsync();
        await harness.RecheckAsync();
        Assert.IsNull(await harness.ReadAsync(async db => QuestionKind(await db.BookRequests.SingleAsync(r => r.Id == request.Id))));
        Assert.Contains("standalone", harness.Client.SubmittedCandidates);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TheWebAnswerFetchesOnlyOnTakeAndTheFirstAnswerClosesTheQuestion(bool take)
    {
        var collection = ExternalProviderCandidate.FromSimple("collection", "The Empyrean Books 1-3", "Rebecca Yarros", "epub", 123456);
        collection = collection with { Release = collection.Release! with { IsCollection = true } };
        var submitted = await ConfidenceScenario.RunAsync([collection], "Iron Flame", "2", "Strict",
            exercise: async (services, admin) =>
            {
                Guid requestId;
                Guid candidateId;
                await using (var scope = services.CreateAsyncScope())
                {
                    var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var request = await database.BookRequests.Include(r => r.ReviewCandidates).SingleAsync();
                    Assert.AreEqual("CollectionContaining", QuestionKind(request));
                    requestId = request.Id;
                    candidateId = request.ReviewCandidates.Single().Id;
                }
                var url = $"/api/v1/requests/{requestId}/needs-review/resolve";
                (await admin.PostAsJsonAsync(url, new ResolveNeedsReviewRequest(take ? candidateId : null))).EnsureSuccessStatusCode();
                var second = await admin.PostAsJsonAsync(url, new ResolveNeedsReviewRequest(candidateId));
                Assert.IsFalse(second.IsSuccessStatusCode, "A stale answer must not fetch another copy.");
                await using var inspection = services.CreateAsyncScope();
                var db = inspection.ServiceProvider.GetRequiredService<AppDbContext>();
                var format = await db.RequestFormats.SingleAsync();
                Assert.AreEqual(take ? "CollectionContaining" : null, typeof(RequestFormat).GetProperty("AcceptedVariantKind")?.GetValue(format)?.ToString());
            });
        Assert.AreEqual(take ? 1 : 0, submitted.Count);
    }

    [TestMethod]
    public async Task OnlyAContainingCollectionAsksOnceAndKeepsARecheckScheduled()
    {
        var collection = ExternalProviderCandidate.FromSimple("collection", "The Empyrean Books 1-3", "Rebecca Yarros", "epub", 123456);
        collection = collection with { Release = collection.Release! with { IsCollection = true } };
        var submitted = await ConfidenceScenario.RunAsync([collection], "Iron Flame", "2", "Strict", inspect: async database =>
        {
            var request = await database.BookRequests.Include(r => r.StatusHistory).SingleAsync();
            Assert.AreEqual(RequestStatus.NeedsReview, request.Status);
            Assert.AreEqual("CollectionContaining", QuestionKind(request));
            Assert.HasCount(1, await database.RequestReviewCandidates.ToArrayAsync());
            Assert.IsTrue((await database.ProviderAttempts.SingleAsync()).NextEligibleCheckAtUtc > DateTimeOffset.UtcNow);
            Assert.HasCount(1, await database.OutboundCommunications.Where(c => c.CommunicationType == "request.acquisition_question").ToArrayAsync());
        });
        Assert.IsEmpty(submitted, "An unanswered question never downloads.");
    }

    [TestMethod]
    public async Task AnAllowedStandalonePreventsAQuestion()
    {
        var collection = ExternalProviderCandidate.FromSimple("collection", "The Empyrean Books 1-3", "Rebecca Yarros", "epub", 123456);
        collection = collection with { Release = collection.Release! with { IsCollection = true } };
        var standalone = ExternalProviderCandidate.FromSimple("standalone", "Iron Flame", "Rebecca Yarros", "epub", 123456);
        var submitted = await ConfidenceScenario.RunAsync([collection, standalone], "Iron Flame", "2", "Strict", inspect: async database =>
        {
            Assert.IsNull(QuestionKind(await database.BookRequests.SingleAsync()));
            Assert.IsEmpty(await database.OutboundCommunications.Where(c => c.CommunicationType == "request.acquisition_question").ToArrayAsync());
        });
        Assert.HasCount(1, submitted);
        Assert.AreEqual("standalone", submitted.Single());
    }

    [TestMethod]
    public async Task OnlyAnotherLanguageAsksWithoutDownloading()
    {
        var candidate = ExternalProviderCandidate.FromSimple("foreign", "Iron Flame", "Rebecca Yarros", "epub", 123456) with
        { Edition = new("fr", null, null, []) };
        var submitted = await ConfidenceScenario.RunAsync([candidate], "Iron Flame", "2", "Strict", inspect: async database =>
        {
            Assert.AreEqual("OtherLanguageOnly", QuestionKind(await database.BookRequests.SingleAsync()));
        });
        Assert.IsEmpty(submitted);
    }

    private static string? QuestionKind(BookRequest request) =>
        typeof(BookRequest).GetProperty("QuestionKind")?.GetValue(request)?.ToString();

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SpellingOnlyAuthorOnATwoPartSetAtStrictStaysOrdinaryReview(bool structuredAuthor)
    {
        ExternalProviderCandidate Part(int number) => new($"part-{number}",
            structuredAuthor ? new("Onyx Storm", null, [new("Rebecca Yaros", "author")], [], []) : ExternalProviderWorkEvidence.Empty,
            Release: new($"Onyx Storm ({number} of 2) by Rebecca Yaros", "m4b", 123456, null, null, null, null, null, [], null, ExternalProviderDrmStatus.None));
        var submitted = await ConfidenceScenario.RunAsync([Part(1), Part(2)], "Onyx Storm", "", "Strict", "Audiobook",
            inspect: async database =>
            {
                var request = await database.BookRequests.SingleAsync();
                var jobs = await database.ProviderAcquisitionJobs.ToArrayAsync();
                Assert.AreEqual(RequestStatus.NeedsReview, request.Status, $"Structured author: {structuredAuthor}; submitted jobs: {jobs.Length}.");
                Assert.IsNull(QuestionKind(request), "Author spelling is ordinary review, not a product-choice question.");
            });
        Assert.IsEmpty(submitted);
    }
}
