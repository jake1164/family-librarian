using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class AcquisitionPreferenceEndpointTests
{
    private const string Preferences = "/api/v1/me/acquisition-preferences/";

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task SharedRequestsRequireEveryPreferenceAndOnlyAskMissingRequesters(bool language, bool both)
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        var request = await harness.RequestHobbitAsync();
        using var reader = await ReaderAsync(harness);
        (await reader.PostAsJsonAsync("/api/v1/requests/", new CreateBookRequestRequest(request.WorkId, ["Ebook"], null, false, false))).EnsureSuccessStatusCode();
        var readerId = await harness.ReadAsync(async db => (await db.Users.SingleAsync(u => u.Email == WebTestFixture.UserEmail)).Id);
        var ownerId = await harness.ReadAsync(async db => (await db.BookRequests.SingleAsync()).UserId);
        await SeedAsync(harness, readerId, language);
        if (both) await SeedAsync(harness, ownerId, language);
        Configure(harness, language);
        await harness.RecheckAsync();
        var state = await harness.ReadAsync(db => db.BookRequests.Include(r => r.ReviewCandidates).Include(r => r.StatusHistory).SingleAsync());
        Assert.AreEqual(both ? 1 : 0, harness.Client.SubmittedCandidates.Count);
        Assert.AreEqual(both ? null : language ? AcquisitionQuestionKind.OtherLanguageOnly : AcquisitionQuestionKind.CollectionContaining, state.QuestionKind);
        var communications = await harness.ReadAsync(db => db.OutboundCommunications.Where(c => c.CommunicationType == "request.acquisition_question").ToArrayAsync());
        Assert.AreEqual(both ? 0 : 1, communications.Length);
        Assert.AreEqual(both ? 0 : 1, await harness.ReadAsync(db => db.NotificationEvents.CountAsync(n => n.Category == "request.acquisition_question")));
        if (both)
            Assert.IsTrue(state.StatusHistory.Any(h => h.Reason == "Accepted by your saved preferences."));
        else
        {
            Assert.AreEqual(ownerId, communications.Single().RecipientUserId);
            var mine = await reader.GetFromJsonAsync<JsonElement>("/api/v1/me/requests");
            var review = mine.GetProperty("active")[0].GetProperty("needsReview");
            Assert.IsFalse(review.GetProperty("canAnswer").GetBoolean());
            Assert.AreEqual(1, review.GetProperty("waitingForRequesterCount").GetInt32());
            var candidate = state.ReviewCandidates.Single().Id;
            Assert.AreEqual(HttpStatusCode.Conflict, (await reader.PostAsJsonAsync($"/api/v1/requests/{request.Id}/needs-review/resolve", new ResolveNeedsReviewRequest(candidate))).StatusCode);
            Assert.AreEqual(HttpStatusCode.Conflict, (await reader.PostAsJsonAsync($"/api/v1/requests/{request.Id}/needs-review/resolve", new ResolveNeedsReviewRequest(null))).StatusCode);
            Assert.IsEmpty(harness.Client.SubmittedCandidates);
            (await harness.Admin.PostAsJsonAsync($"/api/v1/requests/{request.Id}/needs-review/resolve", new ResolveNeedsReviewRequest(candidate))).EnsureSuccessStatusCode();
            Assert.HasCount(1, harness.Client.SubmittedCandidates);
        }
    }

    [TestMethod]
    public async Task RemovingAPreferenceWhileOpenMakesTheHolderAskable()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        var request = await harness.RequestHobbitAsync();
        using var reader = await ReaderAsync(harness);
        (await reader.PostAsJsonAsync("/api/v1/requests/", new CreateBookRequestRequest(request.WorkId, ["Ebook"], null, false, false))).EnsureSuccessStatusCode();
        var readerId = await harness.ReadAsync(async db => (await db.Users.SingleAsync(u => u.Email == WebTestFixture.UserEmail)).Id);
        var preferenceId = await SeedAsync(harness, readerId, false);
        Configure(harness, false);
        await harness.RecheckAsync();
        (await reader.DeleteAsync(Preferences + preferenceId)).EnsureSuccessStatusCode();
        var candidate = await harness.ReadAsync(async db => (await db.RequestReviewCandidates.SingleAsync()).Id);
        (await reader.PostAsJsonAsync($"/api/v1/requests/{request.Id}/needs-review/resolve", new ResolveNeedsReviewRequest(candidate))).EnsureSuccessStatusCode();
        Assert.HasCount(1, harness.Client.SubmittedCandidates);
    }

    [TestMethod]
    public async Task AnAdministratorMayAnswerEvenWhenTheirPreferenceAlreadyConsents()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        var request = await harness.RequestHobbitAsync();
        using var reader = await ReaderAsync(harness);
        (await reader.PostAsJsonAsync("/api/v1/requests/", new CreateBookRequestRequest(request.WorkId, ["Ebook"], null, false, false))).EnsureSuccessStatusCode();
        await SeedAsync(harness, await harness.ReadAsync(async db => (await db.BookRequests.SingleAsync()).UserId), false);
        Configure(harness, false);
        await harness.RecheckAsync();
        (await harness.Admin.PostAsJsonAsync($"/api/v1/admin/requests/{request.Id}/needs-review/resolve", new ResolveNeedsReviewRequest(null))).EnsureSuccessStatusCode();
        Assert.IsNull(await harness.ReadAsync(async db => (await db.BookRequests.SingleAsync()).QuestionKind));
        Assert.IsEmpty(harness.Client.SubmittedCandidates);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AlwaysAcceptPersistsOnlyTheAskedChoiceAndIsUsedOnALaterRequest(bool language)
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        var request = await harness.RequestHobbitAsync();
        Configure(harness, language);
        await harness.RecheckAsync();
        var candidate = await harness.ReadAsync(async db => (await db.RequestReviewCandidates.SingleAsync()).Id);
        (await harness.Admin.PostAsJsonAsync($"/api/v1/requests/{request.Id}/needs-review/resolve", new { candidateId = candidate, alwaysAccept = true })).EnsureSuccessStatusCode();
        var preferences = await harness.Admin.GetFromJsonAsync<JsonElement[]>(Preferences);
        Assert.IsNotNull(preferences);
        Assert.HasCount(1, preferences);
        Assert.AreEqual(language ? "AcceptLanguage" : "AcceptCollectionsContaining", preferences[0].GetProperty("kind").GetString());
        Assert.AreEqual(language ? "fr" : "", preferences[0].GetProperty("value").GetString());
        var second = await harness.RequestHobbitAsync(copyOfWork: true);
        await harness.RecheckAsync();
        Assert.IsNull(await harness.ReadAsync(async db => (await db.BookRequests.SingleAsync(r => r.Id == second.Id)).QuestionKind));
        Assert.AreEqual(2, harness.Client.SubmittedCandidates.Count);
        Assert.HasCount(1, (await harness.Admin.GetFromJsonAsync<JsonElement[]>(Preferences))!);
    }

    [TestMethod]
    [DataRow("AbridgedOnly")]
    [DataRow("PartsOnly")]
    [DataRow("DramatizedOnly")]
    public async Task OtherKindsCannotBeSavedEvenWithAForgedAlwaysFlag(string kind)
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        var requested = await harness.RequestHobbitAsync();
        Guid candidateId;
        await using (var scope = harness.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var request = await db.BookRequests.Include(r => r.Formats).SingleAsync();
            request.AskQuestion(Enum.Parse<AcquisitionQuestionKind>(kind), request.Formats.Single().Id, "Choice?",
                [new(request.Formats.Single().Id, "waiting-source", "copy", "The Hobbit", "J. R. R. Tolkien", "en", null, null, null, false)], DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            candidateId = request.ReviewCandidates.Single().Id;
        }
        Assert.AreEqual(HttpStatusCode.BadRequest, (await harness.Admin.PostAsJsonAsync($"/api/v1/requests/{requested.Id}/needs-review/resolve", new { candidateId, alwaysAccept = true })).StatusCode);
        Assert.AreEqual(kind, (await harness.ReadAsync(async db => (await db.BookRequests.SingleAsync()).QuestionKind))?.ToString());
        Assert.IsEmpty(harness.Client.SubmittedCandidates);
    }

    [TestMethod]
    public async Task PreferencesAreUniquePrivateAndRemovalRequiresAntiforgery()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var harness = await WaitingHarness.CreateAsync(fixture);
        await harness.RequestHobbitAsync();
        var owner = await harness.ReadAsync(async db => (await db.BookRequests.SingleAsync()).UserId);
        var readerId = await harness.ReadAsync(async db => (await db.Users.SingleAsync(u => u.Email == WebTestFixture.UserEmail)).Id);
        var ownerPreference = await SeedAsync(harness, owner, false);
        var readerPreference = await SeedAsync(harness, readerId, true);
        using var reader = await ReaderAsync(harness);
        Assert.HasCount(1, (await reader.GetFromJsonAsync<JsonElement[]>(Preferences))!);
        Assert.AreEqual(HttpStatusCode.NotFound, (await reader.DeleteAsync(Preferences + ownerPreference)).StatusCode);
        reader.DefaultRequestHeaders.Remove(AntiforgeryTokenEndpoint.HeaderName);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await reader.DeleteAsync(Preferences + readerPreference)).StatusCode);
        reader.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(reader));
        (await reader.DeleteAsync(Preferences + readerPreference)).EnsureSuccessStatusCode();
        Assert.IsEmpty((await reader.GetFromJsonAsync<JsonElement[]>(Preferences))!);
        Assert.HasCount(1, (await harness.Admin.GetFromJsonAsync<JsonElement[]>(Preferences))!);
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => SeedAsync(harness, owner, false));
    }

    private static void Configure(WaitingHarness harness, bool language)
    {
        harness.Client.Script = search =>
        {
            var candidate = language ? WaitingRecheckClient.Identifier("foreign", search) : WaitingRecheckClient.Collection("collection");
            return [candidate with { Work = candidate.Work with { Title = language ? "The Hobbit" : "The Hobbit / Another Novel" },
                Edition = language ? new("fra", null, null, []) : candidate.Edition }];
        };
    }

    private static async Task<HttpClient> ReaderAsync(WaitingHarness harness)
    {
        var reader = harness.Factory.CreateClient();
        (await reader.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword })).EnsureSuccessStatusCode();
        reader.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(reader));
        return reader;
    }

    private static async Task<Guid> SeedAsync(WaitingHarness harness, Guid userId, bool language)
    {
        var type = typeof(BookRequest).Assembly.GetType("FamilyLibrarian.Domain.Requests.RequesterAcquisitionPreference");
        Assert.IsNotNull(type, "Saved preferences must be domain rows.");
        var kindType = typeof(BookRequest).Assembly.GetType("FamilyLibrarian.Domain.Requests.RequesterAcquisitionPreferenceKind");
        Assert.IsNotNull(kindType);
        var row = Activator.CreateInstance(type, userId, Enum.Parse(kindType, language ? "AcceptLanguage" : "AcceptCollectionsContaining"), language ? "fr" : null, DateTimeOffset.UtcNow)!;
        await using var scope = harness.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Add(row); await db.SaveChangesAsync();
        return (Guid)type.GetProperty("Id")!.GetValue(row)!;
    }
}
