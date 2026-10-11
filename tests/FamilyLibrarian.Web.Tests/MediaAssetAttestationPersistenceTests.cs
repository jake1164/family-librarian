using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// M1-4b: attestation is explicit at creation and survives a real database round trip. The column keeps
/// its historical database default of true, so EF must still send an explicit false.
/// </summary>
[TestClass]
public sealed class MediaAssetAttestationPersistenceTests
{
    [TestMethod]
    public async Task AnAssetCreatedWithoutTheFlagIsStoredAsNotPreConfirmedAndExplicitTrueSurvives()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await database.Users.Select(user => user.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;
        var work = new Work("Attestation round trip", null, null, null, PublicationStatus.Published, now);
        database.Works.Add(work);
        var request = new BookRequest(userId, work.Id, [RequestMediaType.Ebook], null, now);
        database.BookRequests.Add(request);
        var formatId = request.Formats.Single().Id;

        MediaAsset Create(string name, bool? flag) => flag is { } value
            ? new(work.Id, null, RequestMediaType.Ebook, ".epub", name, name, 1, new string('d', 64), "application/epub+zip", formatId, null, now, identityPreConfirmed: value)
            : new(work.Id, null, RequestMediaType.Ebook, ".epub", name, name, 1, new string('d', 64), "application/epub+zip", formatId, null, now);

        var unspecified = Create("unspecified.epub", null);
        var attested = Create("attested.epub", true);
        var notAttested = Create("not-attested.epub", false);
        database.MediaAssets.AddRange(unspecified, attested, notAttested);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        Assert.IsFalse((await database.MediaAssets.SingleAsync(a => a.Id == unspecified.Id)).IdentityPreConfirmed,
            "A creation path that says nothing must not attest identity (and the database default of true must not override it).");
        Assert.IsTrue((await database.MediaAssets.SingleAsync(a => a.Id == attested.Id)).IdentityPreConfirmed);
        Assert.IsFalse((await database.MediaAssets.SingleAsync(a => a.Id == notAttested.Id)).IdentityPreConfirmed);
    }

    [TestMethod]
    public async Task TheDatabaseDefaultForExistingRowsIsUnchanged()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        await using var scope = fixture.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var defaultExpression = await database.Database.SqlQuery<string>(
            $"""
             SELECT column_default AS "Value" FROM information_schema.columns
             WHERE table_schema = 'acquisition' AND table_name = 'media_assets' AND column_name = 'identity_pre_confirmed'
             """).SingleAsync();

        Assert.AreEqual("true", defaultExpression, "Rows created before M1-4b keep their historical value; no migration changed it.");
    }
}
