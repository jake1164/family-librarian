using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Feedback;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Catalog;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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

    [TestMethod]
    public async Task AFailureBetweenTheTwoSavesRollsBackBothTheRetirementAndTheDeletionOfItsUniqueRows()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var userId = await SeedUserAsync(database);
        var work = await SeedWorkAsync(database, "Dia da Ceifa", "OL45870364W");
        await SeedRequestAsync(database, userId, work, cancel: true);

        // The second save is the one that deletes the retired Work's references and editions.
        var failSecondSave = new FailOnSave(2);
        await using (var failing = Open(connection, failSecondSave))
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await new OrphanedWorkRetirement(failing).RetireAsync(Now, CancellationToken.None));
        }

        await using (var verify = Open(connection))
        {
            Assert.IsFalse((await verify.Works.SingleAsync(w => w.Id == work.Id)).IsRetired, "The retirement rolled back with the failed deletion.");
            Assert.AreEqual(1, await verify.Editions.CountAsync(edition => edition.WorkId == work.Id), "The unique ISBN row survived.");
            Assert.AreEqual(1, await verify.ExternalReferences.CountAsync(reference => reference.EntityId == work.Id));
        }

        // Nothing was stranded half-done: the same sweep now completes atomically.
        await using (var retry = Open(connection))
        {
            Assert.AreEqual(1, await new OrphanedWorkRetirement(retry).RetireAsync(Now, CancellationToken.None));
        }

        await using var final = Open(connection);
        Assert.IsTrue((await final.Works.SingleAsync(w => w.Id == work.Id)).IsRetired);
        Assert.AreEqual(0, await final.Editions.CountAsync(edition => edition.WorkId == work.Id));
        Assert.AreEqual(0, await final.ExternalReferences.CountAsync(reference => reference.EntityId == work.Id));
    }

    [TestMethod]
    public async Task RetirementWaitsForTheCatalogLockAndWritesNothingWhileItIsHeld()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var userId = await SeedUserAsync(database);
        var work = await SeedWorkAsync(database, "Held lock book", "OL-HELD-1");
        await SeedRequestAsync(database, userId, work, cancel: true);
        await using var probe = new CatalogLockProbe(connection);
        await probe.HoldAsync(CancellationToken.None);

        await using var retiring = Open(connection);
        var retire = Task.Run(() => new OrphanedWorkRetirement(retiring).RetireAsync(Now, CancellationToken.None));
        // The retirement shows as a backend waiting on the catalog lock, and the Work is untouched while it waits.
        await WaitUntilAnyWaitingAsync(connection, probe);
        Assert.IsFalse(retire.IsCompleted);
        await using (var peek = Open(connection))
        {
            Assert.IsFalse((await peek.Works.SingleAsync(w => w.Id == work.Id)).IsRetired);
        }

        await probe.ReleaseAsync(CancellationToken.None);

        Assert.AreEqual(1, await retire.WaitAsync(TimeSpan.FromSeconds(60)));
    }

    private static async Task WaitUntilAnyWaitingAsync(string connection, CatalogLockProbe probe)
    {
        // The retirement runs on a context without a distinguishing application name, so observe by the default (empty) one.
        _ = connection;
        await probe.WaitUntilWaitingAsync(string.Empty, TimeSpan.FromSeconds(15));
    }

    /// <summary>Throws from the Nth SaveChanges, as an unexpected storage fault would.</summary>
    private sealed class FailOnSave(int failingOrdinal) : SaveChangesInterceptor
    {
        private int saves;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref saves) == failingOrdinal
                ? throw new InvalidOperationException("Injected storage fault.")
                : ValueTask.FromResult(result);
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

    private static AppDbContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new AppDbContext(options.Options);
    }

    private static Task<string> NewDatabaseAsync()
    {
        if (PostgresFixture.UnavailableReason is not null) Assert.Inconclusive(PostgresFixture.UnavailableReason);
        return PostgresFixture.CreateMigratedDatabaseAsync();
    }
}
