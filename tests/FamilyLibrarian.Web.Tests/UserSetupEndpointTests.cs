using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Contracts.Communications;
using FamilyLibrarian.Contracts.Delivery;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>What a regular user sees before an administrator has set anything up, and their own format default.</summary>
[TestClass]
public sealed class UserSetupEndpointTests
{
    [TestMethod]
    public async Task AFreshInstallReportsUserFeaturesAsUnavailableAndRefusesANewKindle()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var client = await SignedInUserAsync(fixture);

        Assert.IsFalse((await client.GetFromJsonAsync<KindleDeliveryAvailabilityResponse>(
            "/api/v1/me/delivery/kindle/availability"))!.IsAvailable);
        Assert.IsFalse((await client.GetFromJsonAsync<MatrixLinkStatusResponse>(
            "/api/v1/me/communications/matrix/"))!.IsAvailable);
        Assert.IsFalse((await client.GetFromJsonAsync<QuietHoursResponse>("/api/v1/me/quiet-hours/"))!.IsAvailable);

        var added = await client.PutAsJsonAsync(
            "/api/v1/me/delivery/kindle", new SetKindleAddressRequest("reader@kindle.example", null, true));
        Assert.AreEqual(HttpStatusCode.BadRequest, added.StatusCode);
        StringAssert.Contains(await added.Content.ReadAsStringAsync(), "An administrator needs to set up");
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/me/delivery/kindle")).StatusCode);
    }

    [TestMethod]
    public async Task TheDefaultRequestFormatStartsAsEbookAndIsSavedPerUser()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        using var user = await SignedInUserAsync(fixture);
        using var admin = await fixture.CreateAdminClientAsync();
        const string path = "/api/v1/me/default-request-format/";

        Assert.AreEqual("Ebook", (await user.GetFromJsonAsync<DefaultRequestFormatResponse>(path))!.Format);

        var saved = await user.PutAsJsonAsync(path, new SetDefaultRequestFormatRequest("Both"));
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
        Assert.AreEqual("Both", (await user.GetFromJsonAsync<DefaultRequestFormatResponse>(path))!.Format);
        Assert.AreEqual("Ebook", (await admin.GetFromJsonAsync<DefaultRequestFormatResponse>(path))!.Format);

        foreach (var invalid in new[] { "Paperback", "99" })
        {
            var rejected = await user.PutAsJsonAsync(path, new SetDefaultRequestFormatRequest(invalid));
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, invalid);
        }
    }

    [TestMethod]
    public async Task UpgradingGivesExistingAccountsTheEbookDefault()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrations = database.Database.GetMigrations().ToList();
        var index = migrations.FindIndex(m => m.EndsWith("_AddDefaultRequestFormat", StringComparison.Ordinal));
        Assert.IsTrue(index > 0);
        var accounts = await database.Users.CountAsync();
        Assert.IsTrue(accounts > 0);

        await database.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        await database.Database.MigrateAsync();
        database.ChangeTracker.Clear();

        var formats = await database.Users.Select(user => user.DefaultRequestFormat).ToListAsync();
        Assert.HasCount(accounts, formats);
        Assert.IsTrue(formats.All(format => format == DefaultRequestFormat.Ebook));
        Assert.IsFalse(database.Database.HasPendingModelChanges());
    }

    private static async Task<HttpClient> SignedInUserAsync(WebTestFixture fixture)
    {
        var client = await fixture.CreateUserClientAsync();
        client.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }
}
