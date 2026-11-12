using System.Net.Http.Json;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class ExternalProviderReviewIdentityEndpointTests
{
    private static readonly string[] ExpectedReferences = ["first", "second"];

    [TestMethod]
    [DataRow("title")]
    [DataRow("author")]
    [DataRow("release")]
    public async Task DifferentSourceIdentitiesAreRetainedEvenWhenTheirMediaFactsAgree(string differingField)
    {
        var first = NoAcceptableCopyScenario.Result("first", "The Hobbit", null);
        var second = first with { ProviderReference = "second" };
        second = differingField switch
        {
            "title" => second with { Work = second.Work with { Title = "The Hobbit: Illustrated Edition" } },
            "author" => second with { Work = second.Work with { Authors = [new BookAuthor("Tolkien", "author")] } },
            _ => second with { Release = second.Release! with { Name = "The Hobbit - Another Reading" } }
        };
        var client = new VariantSearchProviderClient(_ => [first, second]);
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await VariantScenario.RunAsync(fixture, client, "example-source", afterwards: async (factory, admin, request) =>
        {
            var view = await admin.GetFromJsonAsync<AdminBookRequestResponse>($"/api/v1/admin/requests/{request.Id}");
            Assert.IsNotNull(view);
            Assert.HasCount(2, view.ReviewCandidates!);
            CollectionAssert.AreEquivalent(ExpectedReferences, view.ReviewCandidates!.Select(c => c.ProviderResultId).ToArray());
            Assert.IsFalse(view.Request.NeedsReview!.Reason!.Contains("Several matching copies", StringComparison.Ordinal),
                "An unconfirmed author must not be described as a confirmed matching copy.");
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.AreEqual(2, await database.RequestReviewCandidates.CountAsync(c => c.RequestId == request.Id));
            Assert.AreEqual(0, await database.ProviderAcquisitionJobs.CountAsync(j => j.RequestId == request.Id));
            Assert.AreEqual(0, await database.MediaAssets.CountAsync(a => a.AssociatedRequestFormatId == request.Formats.Single().FormatId));
        });
        Assert.IsEmpty(client.SubmittedCandidates);
    }
}
