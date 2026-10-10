using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Feedback;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Catalog;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class OrphanedWorkRetirementTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AWorkWhoseOnlyRequestsWereCancelledIsRetiredAndItsProviderIdIsFreed()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var userId = await SeedUserAsync(database);
        var work = await SeedWorkAsync(database, "Dia da Ceifa", "OL45870364W");
        await SeedRequestAsync(database, userId, work, cancel: true);

        var retired = await new OrphanedWorkRetirement(database).RetireAsync(Now, CancellationToken.None);

        Assert.AreEqual(1, retired);
        await using var verify = Open(connection);
        Assert.IsTrue((await verify.Works.SingleAsync(w => w.Id == work.Id)).IsRetired);
        var repository = new CatalogRepository(verify);
        Assert.IsNull(await repository.FindWorkByExternalReferenceAsync(
            "openlibrary", "OL45870364W", CancellationToken.None));
        Assert.IsNull(await repository.FindWorkByIsbn13Async(["9788542243659"], CancellationToken.None));

        // A new request for the same book can now create its own Work under the same provider id.
        // Same ISBN as the retired Work's edition: unique catalog-wide, so this
        // only saves if the retired Work let go of it.
        var fresh = new Work("Threshing Day", null, null, null, PublicationStatus.Unknown, Now);
        fresh.AddEdition(new Edition(fresh.Id, "Dia da Ceifa", EditionFormat.Unknown, "9788542243659", null, Now));
        verify.Works.Add(fresh);
        verify.ExternalReferences.Add(new ExternalReference(
            "openlibrary", ExternalReferenceEntityType.Work, fresh.Id, "OL45870364W", Now));
        await verify.SaveChangesAsync();
        var found = await repository.FindWorkByExternalReferenceAsync(
            "openlibrary", "OL45870364W", CancellationToken.None);
        Assert.AreEqual(fresh.Id, found!.Id);
    }

    [TestMethod]
    public async Task AWorkRetiredByAnEarlierVersionStillReleasesItsEditionIsbn()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var userId = await SeedUserAsync(database);
        var work = await SeedWorkAsync(database, "Dia da Ceifa", "OL45870364W");
        await SeedRequestAsync(database, userId, work, cancel: true);
        // Retired but still holding its edition, as the first version left it.
        work.Retire(Now);
        await database.SaveChangesAsync();

        await new OrphanedWorkRetirement(database).RetireAsync(Now, CancellationToken.None);

        await using var verify = Open(connection);
        Assert.AreEqual(0, await verify.Editions.CountAsync(edition => edition.WorkId == work.Id));
        var fresh = new Work("Threshing Day", null, null, null, PublicationStatus.Unknown, Now);
        fresh.AddEdition(new Edition(fresh.Id, "Dia da Ceifa", EditionFormat.Unknown, "9788542243659", null, Now));
        verify.Works.Add(fresh);
        await verify.SaveChangesAsync();
    }

    [TestMethod]
    public async Task AWorkWithALiveRequestIsLeftAlone()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var userId = await SeedUserAsync(database);
        var work = await SeedWorkAsync(database, "Live book", "OL1W");
        await SeedRequestAsync(database, userId, work, cancel: false);

        var retired = await new OrphanedWorkRetirement(database).RetireAsync(Now, CancellationToken.None);

        Assert.AreEqual(0, retired);
        Assert.IsFalse((await database.Works.SingleAsync(w => w.Id == work.Id)).IsRetired);
    }

    [TestMethod]
    public async Task AWorkWithFeedbackIsLeftAloneEvenWhenItsRequestWasCancelled()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var userId = await SeedUserAsync(database);
        var work = await SeedWorkAsync(database, "Loved book", "OL2W");
        await SeedRequestAsync(database, userId, work, cancel: true);
        database.UserWorkFeedback.Add(new UserWorkFeedback(userId, work.Id, new DateOnly(2026, 10, 1), Now));
        await database.SaveChangesAsync();

        var retired = await new OrphanedWorkRetirement(database).RetireAsync(Now, CancellationToken.None);

        Assert.AreEqual(0, retired);
    }

    private static async Task<Guid> SeedUserAsync(AppDbContext database)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(),
            Email = "reader@example.test", DisplayName = "Reader"
        };
        database.Users.Add(user);
        await database.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Work> SeedWorkAsync(AppDbContext database, string title, string externalId)
    {
        var work = new Work(title, null, null, null, PublicationStatus.Unknown, Now);
        work.AddEdition(new Edition(work.Id, title, EditionFormat.Unknown, "9788542243659", null, Now));
        database.Works.Add(work);
        database.ExternalReferences.Add(new ExternalReference(
            "openlibrary", ExternalReferenceEntityType.Work, work.Id, externalId, Now));
        await database.SaveChangesAsync();
        return work;
    }

    private static async Task SeedRequestAsync(AppDbContext database, Guid userId, Work work, bool cancel)
    {
        var request = new BookRequest(userId, work.Id, [RequestMediaType.Ebook], null, Now);
        if (cancel)
        {
            request.Withdraw(userId, Now);
        }

        database.BookRequests.Add(request);
        await database.SaveChangesAsync();
        Assert.AreEqual(cancel, request.Status == RequestStatus.Cancelled);
    }

    private static AppDbContext Open(string connection) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(connection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options);

    private static Task<string> NewDatabaseAsync()
    {
        if (PostgresFixture.UnavailableReason is not null) Assert.Inconclusive(PostgresFixture.UnavailableReason);
        return PostgresFixture.CreateMigratedDatabaseAsync();
    }
}
