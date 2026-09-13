using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Delivery;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Delivery;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class KindleDeliveryPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ConcurrentInitialReleaseAndRetryHaveOnlyOneDurableWinner()
    {
        var connection = await NewDatabaseAsync();
        await using var seed = Open(connection);
        var target = await SeedTargetAsync(seed);
        var request = await SeedRequestAsync(seed, target);
        await using var first = Open(connection);
        await using var second = Open(connection);
        var firstRepository = new DeliveryAttemptRepository(first);
        var secondRepository = new DeliveryAttemptRepository(second);
        var attempts = new[] { Attempt(target, request.Id), Attempt(target, request.Id) };

        var added = await Task.WhenAll(firstRepository.TryAddAsync(attempts[0], CancellationToken.None),
            secondRepository.TryAddAsync(attempts[1], CancellationToken.None));
        Assert.AreEqual(1, added.Count(value => value));
        var original = attempts[Array.IndexOf(added, true)];
        var retries = new[] { Attempt(target, request.Id, original.DeliveryId, 2), Attempt(target, request.Id, original.DeliveryId, 2) };
        var retryAdded = await Task.WhenAll(firstRepository.TryAddAsync(retries[0], CancellationToken.None),
            secondRepository.TryAddAsync(retries[1], CancellationToken.None));
        Assert.AreEqual(1, retryAdded.Count(value => value));
        Assert.AreEqual(2, await seed.DeliveryAttempts.CountAsync());
    }

    [TestMethod]
    public async Task OnlyOneWorkerCanClaimTheSamePendingAttempt()
    {
        var connection = await NewDatabaseAsync();
        await using var seed = Open(connection);
        var target = await SeedTargetAsync(seed);
        var attempt = Attempt(target);
        seed.DeliveryAttempts.Add(attempt);
        await seed.SaveChangesAsync();
        await using var first = Open(connection);
        await using var second = Open(connection);
        var firstRepository = new DeliveryAttemptRepository(first);
        var secondRepository = new DeliveryAttemptRepository(second);
        var firstCopy = (await firstRepository.FindAsync(attempt.Id, CancellationToken.None))!;
        var secondCopy = (await secondRepository.FindAsync(attempt.Id, CancellationToken.None))!;

        var claimed = await Task.WhenAll(
            firstRepository.TryTransitionAsync(firstCopy, DeliveryAttemptStatus.Submitting, Now, CancellationToken.None),
            secondRepository.TryTransitionAsync(secondCopy, DeliveryAttemptStatus.Submitting, Now, CancellationToken.None));

        Assert.AreEqual(1, claimed.Count(value => value), "Only the winner may call CWA.");
        Assert.AreEqual(DeliveryAttemptStatus.Submitting, firstCopy.Status);
        Assert.AreEqual(DeliveryAttemptStatus.Submitting, secondCopy.Status);
    }

    [TestMethod]
    public async Task RetryQueryUsesTheLatestRowBeforeStatusAndCooldownFilters()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var target = await SeedTargetAsync(database);
        var first = Attempt(target);
        first.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        first.TransitionTo(DeliveryAttemptStatus.Failed, Now, "offline", retryable: true);
        var second = Attempt(target, deliveryId: first.DeliveryId, ordinal: 2);
        second.TransitionTo(DeliveryAttemptStatus.Submitting, Now.AddMinutes(3));
        second.TransitionTo(DeliveryAttemptStatus.Submitted, Now.AddMinutes(3));
        var independent = Attempt(target);
        independent.TransitionTo(DeliveryAttemptStatus.Submitting, Now);
        independent.TransitionTo(DeliveryAttemptStatus.Failed, Now, "offline", retryable: true);
        database.DeliveryAttempts.AddRange(first, second, independent);
        await database.SaveChangesAsync();
        var repository = new DeliveryAttemptRepository(database);

        var candidates = await repository.ListRetryableFailedAsync(Now.AddHours(1), 3, CancellationToken.None);

        Assert.HasCount(1, candidates);
        Assert.AreEqual(independent.Id, candidates[0].Id);
        Assert.AreEqual(second.Id, (await repository.FindLatestAsync(first.DeliveryId, CancellationToken.None))!.Id);
    }

    [TestMethod]
    public async Task EligibilitySeesTargetChangesAccountDeactivationAndWithdrawalFromAnotherContext()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var target = await SeedTargetAsync(database);
        var request = await SeedRequestAsync(database, target);
        var attempt = Attempt(target, request.Id);
        var repository = new DeliveryAttemptRepository(database);
        Assert.IsNotNull(await repository.GetEligibleTargetAsync(attempt, CancellationToken.None));
        await using var editor = Open(connection);
        var editedTarget = await editor.DeliveryTargets.SingleAsync();
        editedTarget.SetEnabled(false, Now);
        await editor.SaveChangesAsync();
        Assert.IsNull(await repository.GetEligibleTargetAsync(attempt, CancellationToken.None));
        editedTarget.SetEnabled(true, Now);
        var user = await editor.Users.SingleAsync();
        user.Status = UserStatus.Disabled;
        await editor.SaveChangesAsync();
        Assert.IsNull(await repository.GetEligibleTargetAsync(attempt, CancellationToken.None));
        user.Status = UserStatus.Active;
        await editor.SaveChangesAsync();
        var editedRequest = await editor.BookRequests.Include(row => row.Participants).Include(row => row.Formats).SingleAsync();
        editedRequest.Withdraw(target.UserId, Now);
        await editor.SaveChangesAsync();
        Assert.IsNull(await repository.GetEligibleTargetAsync(attempt, CancellationToken.None));
    }

    [TestMethod]
    public async Task VerifiedImportsRecoverUnreleasedIntentIncludingLateParticipants()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var target = await SeedTargetAsync(database);
        var request = await SeedRequestAsync(database, target);
        var format = request.Formats.Single();
        request.Join(target.UserId, [RequestMediaType.Audiobook], null, Now);
        var asset = new MediaAsset(request.WorkId, null, RequestMediaType.Ebook, ".epub", "book.epub",
            "book.epub", 100, new string('a', 64), "application/epub+zip", format.Id, null, Now);
        var import = new LibraryImport(asset.Id, Now);
        import.MarkAvailable("42", Now);
        request.MarkFormatAvailable(format.Id, Now);
        database.MediaAssets.Add(asset);
        database.LibraryImports.Add(import);
        await database.SaveChangesAsync();
        var repository = new DeliveryAttemptRepository(database);
        var recovered = await repository.ListUnreleasedAsync(CancellationToken.None);
        Assert.HasCount(1, recovered);
        Assert.AreEqual("42", recovered[0].ExternalBookId);
        Assert.AreEqual(".epub", recovered[0].BookFormat);
        Assert.AreEqual(request.Id, recovered[0].Request.Id);
        await repository.TryAddAsync(Attempt(target, request.Id), CancellationToken.None);
        Assert.IsEmpty(await repository.ListUnreleasedAsync(CancellationToken.None));

        var secondTarget = await SeedTargetAsync(database);
        request.Join(secondTarget.UserId, [RequestMediaType.Ebook], null, Now, secondTarget.Id);
        await database.SaveChangesAsync();
        Assert.HasCount(1, await repository.ListUnreleasedAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task UpgradePreservesLegacyHistoryAndStopsAmbiguousRetries()
    {
        var connection = await NewDatabaseAsync("20260909213025_AddDeliveryAttemptConfirmation");
        await using var database = Open(connection);
        var target = await SeedTargetAsync(database);
        // Raw SQL, not the ORM: book_requests has gained columns since this
        // migration pin (e.g. review_category), so BookRequest's current model
        // no longer matches the schema at this exact point in history --
        // exactly like InsertLegacyAttemptAsync below, which exists for the
        // same reason on delivery_attempts.
        var requestId = await SeedLegacyRequestAsync(database, target);
        var first = Guid.NewGuid();
        var duplicate = Guid.NewGuid();
        var standalone = Guid.NewGuid();
        await InsertLegacyAttemptAsync(database, first, requestId, target, 1, Now, "Failed", true);
        await InsertLegacyAttemptAsync(database, duplicate, requestId, target, 1, Now.AddMinutes(1), "Submitted", false);
        await InsertLegacyAttemptAsync(database, standalone, null, target, 2, Now, "Failed", true);

        await database.Database.MigrateAsync();

        var attempts = await database.DeliveryAttempts.OrderBy(row => row.CreatedAtUtc).ToArrayAsync();
        Assert.HasCount(3, attempts);
        var initial = attempts.Single(row => row.Id == first);
        var later = attempts.Single(row => row.Id == duplicate);
        Assert.AreEqual(initial.DeliveryId, later.DeliveryId);
        Assert.AreEqual(1, initial.AttemptNumber);
        Assert.AreEqual(2, later.AttemptNumber);
        Assert.AreEqual(DeliveryAttemptStatus.SubmissionUnknown, initial.Status);
        Assert.AreEqual(DeliveryAttemptStatus.Submitted, later.Status);
        Assert.IsEmpty(await new DeliveryAttemptRepository(database).ListRetryableFailedAsync(Now.AddDays(1), 3, CancellationToken.None));
        Assert.IsTrue(attempts.All(row => row.DeliveryId != Guid.Empty));
        Assert.IsFalse(database.Database.HasPendingModelChanges());
    }

    /// <summary>
    /// Inserts a minimal book_requests/request_formats/request_participants/
    /// request_status_history row set matching the schema as of
    /// "20260909213025_AddDeliveryAttemptConfirmation" -- equivalent to
    /// <c>new BookRequest(target.UserId, work.Id, [RequestMediaType.Ebook], null, Now, target.Id)</c>,
    /// but via raw SQL since the current <see cref="BookRequest"/> model has
    /// columns (e.g. review_category) that do not exist yet at this pin.
    /// </summary>
    private static async Task<Guid> SeedLegacyRequestAsync(AppDbContext database, DeliveryTarget target)
    {
        var work = new Work("Review book", null, null, null, PublicationStatus.Published, Now);
        database.Works.Add(work);
        await database.SaveChangesAsync();

        var requestId = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO requests.book_requests
                (id, user_id, work_id, status, requester_note, admin_note, requires_manual_fulfillment,
                 version_kind, version_details, requested_at_utc, status_changed_at_utc, created_at_utc, updated_at_utc)
            VALUES ({requestId}, {target.UserId}, {work.Id}, 'PendingAcquisition', NULL, NULL, false,
                NULL, NULL, {Now}, {Now}, {Now}, {Now})
            """);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO requests.request_formats (id, request_id, media_type, status, created_at_utc, updated_at_utc)
            VALUES ({Guid.NewGuid()}, {requestId}, 'Ebook', 'Requested', {Now}, {Now})
            """);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO requests.request_participants
                (request_id, user_id, wants_ebook, wants_audiobook, note, delivery_target_id, joined_at_utc, withdrawn_at_utc)
            VALUES ({requestId}, {target.UserId}, true, false, NULL, {target.Id}, {Now}, NULL)
            """);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO requests.request_status_history (id, request_id, from_status, to_status, actor_user_id, reason, occurred_at_utc)
            VALUES ({Guid.NewGuid()}, {requestId}, NULL, 'PendingAcquisition', {target.UserId}, NULL, {Now})
            """);

        return requestId;
    }

    private static Task<int> InsertLegacyAttemptAsync(AppDbContext database, Guid id, Guid? requestId,
        DeliveryTarget target, int ordinal, DateTimeOffset atUtc, string status, bool retryable) =>
        database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO delivery.delivery_attempts
                (id, request_id, user_id, delivery_target_id, provider, external_book_id, book_format,
                 convert, attempt_number, status, started_at_utc, completed_at_utc, is_retryable, created_at_utc,
                 confirmation_status)
            VALUES ({id}, {requestId}, {target.UserId}, {target.Id}, 'cwa', '42', 'epub', false, {ordinal},
                {status}, {atUtc}, {atUtc}, {retryable}, {atUtc}, 'Unconfirmed')
            """);

    private static DeliveryAttempt Attempt(DeliveryTarget target, Guid? requestId = null, Guid? deliveryId = null, int ordinal = 1) =>
        new(requestId, target.UserId, target.Id, "cwa", "42", "epub", false, ordinal, Now, deliveryId: deliveryId);

    private static async Task<DeliveryTarget> SeedTargetAsync(AppDbContext database)
    {
        var user = new AppUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), Email = "reader@example.test", DisplayName = "Reader" };
        var target = new DeliveryTarget(user.Id, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "reader@kindle.com", Now);
        database.Users.Add(user);
        database.DeliveryTargets.Add(target);
        await database.SaveChangesAsync();
        return target;
    }

    private static async Task<BookRequest> SeedRequestAsync(AppDbContext database, DeliveryTarget target)
    {
        var work = new Work("Review book", null, null, null, PublicationStatus.Published, Now);
        var request = new BookRequest(target.UserId, work.Id, [RequestMediaType.Ebook], null, Now, target.Id);
        database.Works.Add(work);
        database.BookRequests.Add(request);
        await database.SaveChangesAsync();
        return request;
    }

    private static AppDbContext Open(string connection) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(connection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options);

    private static Task<string> NewDatabaseAsync(string? migration = null)
    {
        if (PostgresFixture.UnavailableReason is not null) Assert.Inconclusive(PostgresFixture.UnavailableReason);
        return PostgresFixture.CreateMigratedDatabaseAsync(migration);
    }
}
