using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Following;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Failure and recovery boundaries of the catalog write scope, against real PostgreSQL. Faults are injected at the real EF
/// boundaries (transaction start, save, commit) by interceptors, and every conflict is a genuine database conflict produced by a
/// second, lock-ignoring writer. Each case asserts the rows PostgreSQL actually holds, not only the reported outcome.
/// </summary>
[TestClass]
public sealed class CatalogWriteFaultTests
{
    private const string Provider = "demo";
    private static readonly DateTimeOffset Past = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class CountingProvider(params BookCandidate[] books) : IBookMetadataProvider
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public string Id => Provider;

        public string DisplayName => "Counting demo";

        public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new BookCandidateSearchPage([], false));

        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(books.FirstOrDefault(book => book.ExternalId == externalId));
        }
    }

    /// <summary>Faults at the transaction boundaries. The ordinal counts transactions across the whole host, starting at 1.</summary>
    private sealed class TransactionHooks : DbTransactionInterceptor
    {
        private int started;
        private int committing;
        private int committed;

        public Func<int, Task>? OnStarting { get; set; }

        public Func<DbTransaction, int, Task>? BeforeCommit { get; set; }

        public Func<int, Task>? AfterCommit { get; set; }

        public int Started => Volatile.Read(ref started);

        public int Commits => Volatile.Read(ref committed);

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref started);
            if (OnStarting is not null) await OnStarting(ordinal);
            return result;
        }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref committing);
            if (BeforeCommit is not null) await BeforeCommit(transaction, ordinal);
            return result;
        }

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref committed);
            if (AfterCommit is not null) await AfterCommit(ordinal);
        }
    }

    /// <summary>Runs just before a save reaches the database, so a second writer can slip in between evaluation and save.</summary>
    private sealed class SaveHooks : SaveChangesInterceptor
    {
        private int saves;

        public Func<int, Task>? BeforeSave { get; set; }

        public int Saves => Volatile.Read(ref saves);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref saves);
            if (BeforeSave is not null) await BeforeSave(ordinal);
            return result;
        }
    }

    private sealed class Rig(
        FamilyLibrarianAppFactory host, string connection, CountingProvider source, TransactionHooks transactions, SaveHooks saves) : IAsyncDisposable
    {
        public FamilyLibrarianAppFactory Host => host;

        public string Connection => connection;

        public CountingProvider Source => source;

        public TransactionHooks Transactions => transactions;

        public SaveHooks Saves => saves;

        public ValueTask DisposeAsync() => host.DisposeAsync();
    }

    private static async Task<Rig> NewRigAsync(params BookCandidate[] books)
    {
        if (PostgresFixture.UnavailableReason is not null) Assert.Inconclusive(PostgresFixture.UnavailableReason);
        var connection = await PostgresFixture.CreateSeededDatabaseAsync();
        var source = new CountingProvider(books);
        var transactions = new TransactionHooks();
        var saves = new SaveHooks();
        var host = new FamilyLibrarianAppFactory(connection, services =>
        {
            services.RemoveAll<IBookMetadataProvider>();
            services.AddSingleton<IBookMetadataProvider>(source);
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(transactions, saves));
        });
        return new Rig(host, connection, source, transactions, saves);
    }

    private static AppDbContext Outsider(string connection) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(connection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options);

    private static string Unique(string stem) => $"{stem} {Guid.NewGuid():N}";

    private static BookCandidate Book(string externalId, string title, string author, string? isbn, int? pages = null) =>
        new(Provider, "Counting demo", externalId, title, [author], "Fictional.", null, new DateOnly(2020, 1, 1),
            isbn is null ? [] : [new BookEditionCandidate(title, isbn, "Ebook", new DateOnly(2020, 1, 1))], [], PageCount: pages);

    private static async Task<CatalogWorkResolution> ResolveAsync(Rig rig, string externalId)
    {
        await using var scope = rig.Host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogWorkResolver>().ResolveAsync(Provider, externalId, CancellationToken.None);
    }

    private static async Task<CatalogResolutionException> ResolveFaultAsync(Rig rig, string externalId)
    {
        await using var scope = rig.Host.Services.CreateAsyncScope();
        return await Assert.ThrowsExactlyAsync<CatalogResolutionException>(async () =>
            await scope.ServiceProvider.GetRequiredService<CatalogWorkResolver>().ResolveAsync(Provider, externalId, CancellationToken.None));
    }

    private static async Task<T> InDatabaseAsync<T>(Rig rig, Func<AppDbContext, Task<T>> action)
    {
        await using var outsider = Outsider(rig.Connection);
        return await action(outsider);
    }

    private static async Task<HttpClient> SignedInAsync(Rig rig)
    {
        var client = rig.Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }

    private static NpgsqlException TransportFailure() => new("Injected connection loss.", new IOException("Injected transport failure."));

    /// <summary>A reader following an author, so a duplicated or wrongly emitted "added to the catalog" notice would be visible.</summary>
    private static async Task FollowAuthorAsync(Rig rig, string authorName)
    {
        await using var db = Outsider(rig.Connection);
        var reader = await db.Users.Where(user => user.Email == WebTestFixture.UserEmail).Select(user => user.Id).SingleAsync();
        var author = new Author(authorName, null, Past);
        db.Authors.Add(author);
        db.Follows.Add(new Follow(reader, FollowSubjectType.Author, author.Id, Past));
        await db.SaveChangesAsync();
    }

    private static Task<int> NoticesAsync(Rig rig) =>
        InDatabaseAsync(rig, db => db.NotificationEvents.CountAsync(item => item.Category == NotificationCategories.AuthorNewWorkDetected));

    // ---------------------------------------------------------------- unique-index loss (a writer that ignores the lock)

    [TestMethod]
    public async Task ALostIsbnRaceIsAConflictOverHttpWithNoRowsAndNoSecondProviderFetch()
    {
        var title = Unique("Lost isbn race");
        var author = Unique("Lost Race Author");
        const string isbn = "9780000810015";
        await using var rig = await NewRigAsync(Book("lost-isbn", title, author, isbn));
        using var client = await SignedInAsync(rig);
        // Armed only after sign-in, which itself saves through the same context type.
        var savesBefore = rig.Saves.Saves;
        rig.Saves.BeforeSave = async ordinal =>
        {
            if (ordinal != savesBefore + 1) return;
            // A writer that does not take the catalog lock takes the ISBN between this attempt's evaluation and its save.
            await using var other = Outsider(rig.Connection);
            var work = new Work(Unique("Outsider"), null, null, null, PublicationStatus.Published, Past);
            work.AddEdition(new Edition(work.Id, "Outsider", EditionFormat.Ebook, isbn, null, Past));
            other.Works.Add(work);
            await other.SaveChangesAsync();
        };

        using var response = await client.PostAsync($"/api/v1/catalog/candidates/{Provider}/lost-isbn/resolve", null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("catalog.conflict", problem.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(1, rig.Source.Calls, "The provider is asked once; a retry never refetches.");
        Assert.AreEqual(savesBefore + 1, rig.Saves.Saves, "The retry stopped before any second save: the contested ISBN was not adopted.");
        await InDatabaseAsync(rig, async db =>
        {
            Assert.AreEqual(0, await db.Works.CountAsync(work => work.CanonicalTitle == title), "Nothing of the abandoned attempt remains.");
            Assert.AreEqual(0, await db.ExternalReferences.CountAsync(reference => reference.ExternalId == "lost-isbn"));
            Assert.AreEqual(0, await db.Authors.CountAsync(item => item.CanonicalName == author), "The author created by the abandoned attempt was rolled back.");
            Assert.AreEqual(1, await db.Editions.CountAsync(edition => edition.Isbn13 == isbn), "Only the winner's edition holds the ISBN.");
            return 0;
        });
    }

    [TestMethod]
    public async Task ALostReferenceRaceIsAnIdempotentResultWithFreshNoticesAndNoDuplicateRows()
    {
        var title = Unique("Lost reference race");
        var author = Unique("Reference Race Author");
        await using var rig = await NewRigAsync(Book("lost-reference", title, author, null));
        await FollowAuthorAsync(rig, author);
        Guid winner = Guid.Empty;
        rig.Saves.BeforeSave = async ordinal =>
        {
            if (ordinal != 1) return;
            await using var other = Outsider(rig.Connection);
            var work = new Work(Unique("Reference winner"), null, null, null, PublicationStatus.Published, Past);
            other.Works.Add(work);
            other.ExternalReferences.Add(new ExternalReference(Provider, ExternalReferenceEntityType.Work, work.Id, "lost-reference", Past));
            await other.SaveChangesAsync();
            winner = work.Id;
        };

        var result = await ResolveAsync(rig, "lost-reference");

        Assert.AreEqual(winner, result.Work.Id, "An exact existing provider reference supplies the idempotent result.");
        Assert.IsFalse(result.WasCreated);
        Assert.AreEqual(1, rig.Source.Calls);
        Assert.AreEqual(0, await NoticesAsync(rig), "The abandoned attempt's notice intents were discarded, and a reused Work notifies no one.");
        await InDatabaseAsync(rig, async db =>
        {
            Assert.AreEqual(0, await db.Works.CountAsync(work => work.CanonicalTitle == title));
            Assert.AreEqual(1, await db.ExternalReferences.CountAsync(reference => reference.ExternalId == "lost-reference"));
            Assert.AreEqual(1, await db.Authors.CountAsync(item => item.CanonicalName == author), "Only the pre-existing, followed author; no duplicate.");
            return 0;
        });
    }

    [TestMethod]
    public async Task AnOptimisticUpdateConflictIsRetriedOnceAgainstFreshStateAndTheDatabaseValueWins()
    {
        const string isbn = "9780000810022";
        var title = Unique("Update conflict");
        await using var rig = await NewRigAsync(Book("update-conflict", title, Unique("Update Author"), isbn, pages: 300));
        Guid existing;
        await using (var seed = Outsider(rig.Connection))
        {
            var work = new Work(title, null, null, null, PublicationStatus.Published, Past);
            work.AddEdition(new Edition(work.Id, title, EditionFormat.Ebook, isbn, null, Past));
            seed.Works.Add(work);
            await seed.SaveChangesAsync();
            existing = work.Id;
        }

        rig.Saves.BeforeSave = async ordinal =>
        {
            if (ordinal != 1) return;
            // Another writer fills the page count first, changing the Work row's version under this attempt.
            await using var other = Outsider(rig.Connection);
            var work = await other.Works.SingleAsync(item => item.Id == existing);
            work.SetCatalogLength(111, null, "outsider", DateTimeOffset.UtcNow);
            await other.SaveChangesAsync();
        };

        var result = await ResolveAsync(rig, "update-conflict");

        Assert.AreEqual(existing, result.Work.Id);
        Assert.AreEqual(CatalogResolutionOutcome.Attached, result.Outcome);
        Assert.AreEqual(1, rig.Source.Calls);
        Assert.AreEqual(2, rig.Saves.Saves, "Exactly one retry: one failed save, one successful save.");
        await InDatabaseAsync(rig, async db =>
        {
            var work = await db.Works.SingleAsync(item => item.Id == existing);
            Assert.AreEqual(111, work.CatalogPageCount, "Fill-only: the other writer's committed length is kept, not overwritten.");
            Assert.AreEqual("outsider", work.CatalogLengthSource);
            Assert.AreEqual(1, await db.ExternalReferences.CountAsync(reference => reference.ExternalId == "update-conflict" && reference.EntityId == existing));
            return 0;
        });
    }

    [TestMethod]
    public async Task TheScopeRetriesAtMostOnceAndEveryAttemptGetsFreshRepositoriesAndEntities()
    {
        await using var rig = await NewRigAsync();
        var host = rig.Host;
        await using (var seed = Outsider(rig.Connection))
        {
            var work = new Work(Unique("Blind target"), null, null, null, PublicationStatus.Published, Past);
            seed.Works.Add(work);
            seed.ExternalReferences.Add(new ExternalReference(Provider, ExternalReferenceEntityType.Work, work.Id, "blind-duplicate", Past));
            await seed.SaveChangesAsync();
        }

        var sessions = new List<ICatalogWriteSession>();
        var repositories = new List<ICatalogMutationRepository>();
        var loaded = new List<Work>();
        var causes = new List<CatalogWriteRetryCause>();
        var scope = host.Services.GetRequiredService<ICatalogWriteScope>();

        // A mutation that blindly re-adds an existing reference: a genuine unique violation on every attempt.
        var result = await scope.ExecuteAsync<object>(
            async (session, token) =>
            {
                sessions.Add(session);
                repositories.Add(session.Catalog);
                causes.Add(session.RetryCause);
                loaded.Add((await session.Catalog.FindWorkByExternalReferenceAsync(Provider, "blind-duplicate", token))!);
                session.Catalog.AddExternalReference(new ExternalReference(
                    Provider, ExternalReferenceEntityType.Work, loaded[^1].Id, "blind-duplicate", Past));
                await session.Catalog.SaveChangesAsync(token);
                return new object();
            },
            (_, _, _) => Task.FromResult<object?>(null),
            CancellationToken.None);

        Assert.AreEqual(CatalogWriteStatus.Conflict, result.Status);
        Assert.HasCount(2, sessions, "The first attempt plus exactly one retry; never a retry loop.");
        CollectionAssert.AreEqual(new[] { CatalogWriteRetryCause.None, CatalogWriteRetryCause.UniqueViolation }, causes);
        Assert.AreNotSame(sessions[0], sessions[1]);
        Assert.AreNotSame(repositories[0], repositories[1], "A retry never reuses the abandoned attempt's repository.");
        Assert.AreNotSame(loaded[0], loaded[1], "A retry re-reads: it never reuses a tracked entity from the abandoned transaction.");
        Assert.AreEqual(1, await InDatabaseAsync(rig, db => db.ExternalReferences.CountAsync(reference => reference.ExternalId == "blind-duplicate")));
    }

    // ---------------------------------------------------------------- begin, commit and unknown outcomes

    [TestMethod]
    public async Task ATransactionThatCannotBeginIsUnavailableWithNothingWritten()
    {
        var title = Unique("Cannot begin");
        await using var rig = await NewRigAsync(Book("cannot-begin", title, Unique("Begin Author"), "9780000810039"));
        rig.Transactions.OnStarting = ordinal => ordinal == 1 ? throw TransportFailure() : Task.CompletedTask;

        var fault = await ResolveFaultAsync(rig, "cannot-begin");

        Assert.AreEqual(CatalogResolutionFault.Unavailable, fault.Fault);
        Assert.AreEqual(1, rig.Source.Calls);
        Assert.AreEqual(0, await InDatabaseAsync(rig, db => db.Works.CountAsync(work => work.CanonicalTitle == title)));
    }

    [TestMethod]
    public async Task ACommitThatFailedBeforeReachingTheServerIsVerifiedAbsentThenRetriedOnceAndNotifiesOnce()
    {
        var title = Unique("Commit never sent");
        var author = Unique("Commit Author");
        await using var rig = await NewRigAsync(Book("commit-never-sent", title, author, "9780000810046"));
        await FollowAuthorAsync(rig, author);
        rig.Transactions.BeforeCommit = (_, ordinal) => ordinal == 1 ? throw TransportFailure() : Task.CompletedTask;

        var result = await ResolveAsync(rig, "commit-never-sent");

        Assert.IsTrue(result.WasCreated, "Verified missing, so the one retry committed it.");
        Assert.AreEqual(1, rig.Source.Calls);
        Assert.AreEqual(1, await NoticesAsync(rig), "One creation, one notice: the abandoned attempt's intents were not replayed.");
        await InDatabaseAsync(rig, async db =>
        {
            Assert.AreEqual(1, await db.Works.CountAsync(work => work.CanonicalTitle == title));
            Assert.AreEqual(1, await db.ExternalReferences.CountAsync(reference => reference.ExternalId == "commit-never-sent"));
            Assert.AreEqual(1, await db.Authors.CountAsync(item => item.CanonicalName == author));
            return 0;
        });
    }

    [TestMethod]
    public async Task ACommitThatSucceededButReportedFailureIsRecoveredAndNeverReplayedOrReNotified()
    {
        var title = Unique("Commit lost reply");
        var author = Unique("Lost Reply Author");
        await using var rig = await NewRigAsync(Book("commit-lost-reply", title, author, "9780000810053"));
        await FollowAuthorAsync(rig, author);
        rig.Transactions.AfterCommit = ordinal => ordinal == 1 ? throw TransportFailure() : Task.CompletedTask;

        var result = await ResolveAsync(rig, "commit-lost-reply");

        Assert.IsFalse(result.WasCreated, "A recovered result is a reuse: it is never reported as a second creation.");
        Assert.AreEqual(CatalogResolutionOutcome.Existing, result.Outcome);
        Assert.AreEqual(1, rig.Source.Calls, "Recovery never refetches.");
        Assert.AreEqual(0, await NoticesAsync(rig), "A recovered result emits nothing, so a replay cannot duplicate a notice (the accepted at-most-once limit).");
        await InDatabaseAsync(rig, async db =>
        {
            Assert.AreEqual(1, await db.Works.CountAsync(work => work.CanonicalTitle == title), "Not replayed.");
            Assert.AreEqual(1, await db.ExternalReferences.CountAsync(reference => reference.ExternalId == "commit-lost-reply"));
            return 0;
        });
    }

    [TestMethod]
    public async Task ABackendTerminatedAtCommitLeavesNoPartialRowsAndTheReportedOutcomeMatchesThem()
    {
        var title = Unique("Backend terminated");
        await using var rig = await NewRigAsync(Book("backend-terminated", title, Unique("Terminated Author"), "9780000810060"));
        rig.Transactions.BeforeCommit = async (transaction, ordinal) =>
        {
            if (ordinal != 1) return;
            int pid;
            await using (var ask = transaction.Connection!.CreateCommand())
            {
                ask.Transaction = transaction;
                ask.CommandText = "SELECT pg_backend_pid()";
                pid = (int)(await ask.ExecuteScalarAsync())!;
            }

            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(rig.Connection) { Pooling = false }.ConnectionString);
            await admin.OpenAsync();
            await using var kill = admin.CreateCommand();
            kill.CommandText = "SELECT pg_terminate_backend(@pid)";
            kill.Parameters.AddWithValue("pid", pid);
            await kill.ExecuteScalarAsync();
        };

        CatalogWorkResolution? result = null;
        CatalogResolutionException? fault = null;
        try
        {
            result = await ResolveAsync(rig, "backend-terminated");
        }
        catch (CatalogResolutionException exception)
        {
            fault = exception;
        }

        var works = await InDatabaseAsync(rig, db => db.Works.CountAsync(work => work.CanonicalTitle == title));
        var references = await InDatabaseAsync(rig, db => db.ExternalReferences.CountAsync(reference => reference.ExternalId == "backend-terminated"));
        Assert.AreEqual(works, references, "Never a Work without its reference, or the reverse.");
        Assert.IsLessThanOrEqualTo(1, works, "Never duplicated.");
        Assert.AreEqual(1, rig.Source.Calls);
        if (result is not null)
        {
            Assert.AreEqual(1, works, "A reported success agrees with the committed state.");
        }
        else
        {
            Assert.IsNotNull(fault);
            Assert.AreNotEqual(CatalogResolutionFault.IsbnCollision, fault.Fault);
        }

        // The server dropped the session before it read COMMIT, so the deterministic expectation is: verified absent, retried, created.
        Assert.IsNotNull(result, fault?.Message);
        Assert.IsTrue(result.WasCreated);
        Assert.AreEqual(1, works);
    }

    [TestMethod]
    public async Task AnUnverifiableCommitIsReportedAsUnknownNeverAsRolledBack()
    {
        var title = Unique("Unverifiable commit");
        await using var rig = await NewRigAsync(Book("unverifiable", title, Unique("Unverifiable Author"), "9780000810077"));
        await using var probe = new CatalogLockProbe(rig.Connection);
        rig.Transactions.AfterCommit = async ordinal =>
        {
            if (ordinal != 1) return;
            // The commit really happened, but the reply is lost, and the verification cannot get the lock to look.
            await probe.HoldAsync(CancellationToken.None);
            throw TransportFailure();
        };

        var fault = await ResolveFaultAsync(rig, "unverifiable");

        Assert.AreEqual(CatalogResolutionFault.OutcomeUnknown, fault.Fault, "Neither 'created' nor 'rolled back' is claimed.");
        Assert.AreEqual(1, rig.Source.Calls);
        await probe.ReleaseAsync(CancellationToken.None);
        Assert.AreEqual(1, await InDatabaseAsync(rig, db => db.Works.CountAsync(work => work.CanonicalTitle == title)),
            "The change was in fact committed, which is why an unknown outcome must not be reported as a rollback.");
        // A later identical request resolves to that Work provider-free.
        var later = await ResolveAsync(rig, "unverifiable");
        Assert.IsFalse(later.WasCreated);
        Assert.AreEqual(1, rig.Source.Calls);
    }

    [TestMethod]
    public async Task AnUnconfirmedCommitMapsToARetryable503OverHttp()
    {
        await using var rig = await NewRigAsync(Book("unconfirmed-http", Unique("Unconfirmed over http"), Unique("Http Author"), "9780000810084"));
        await using var probe = new CatalogLockProbe(rig.Connection);
        using var client = await SignedInAsync(rig);
        var commitsBefore = rig.Transactions.Commits;
        rig.Transactions.AfterCommit = async ordinal =>
        {
            if (ordinal != commitsBefore + 1) return;
            await probe.HoldAsync(CancellationToken.None);
            throw TransportFailure();
        };

        using var response = await client.PostAsync($"/api/v1/catalog/candidates/{Provider}/unconfirmed-http/resolve", null);
        await probe.ReleaseAsync(CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.IsTrue(response.Headers.RetryAfter is not null, "A retryable failure says so.");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("catalog.outcome_unknown", problem.RootElement.GetProperty("code").GetString());
        StringAssert.DoesNotMatch(problem.RootElement.ToString(), new System.Text.RegularExpressions.Regex("SELECT|Npgsql|pg_advisory", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "No SQL or driver detail leaks.");
    }

    [TestMethod]
    public async Task ABusyCatalogIsARetryable503OverHttpAndWritesNothing()
    {
        var title = Unique("Busy over http");
        await using var rig = await NewRigAsync(Book("busy-http", title, Unique("Busy Http Author"), "9780000810091"));
        using var client = await SignedInAsync(rig);
        await using var probe = new CatalogLockProbe(rig.Connection);
        await probe.HoldAsync(CancellationToken.None);

        using var response = await client.PostAsync($"/api/v1/catalog/candidates/{Provider}/busy-http/resolve", null);
        await probe.ReleaseAsync(CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.IsTrue(response.Headers.RetryAfter is not null);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("catalog.busy", problem.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(0, await InDatabaseAsync(rig, db => db.Works.CountAsync(work => work.CanonicalTitle == title)));
    }
}
