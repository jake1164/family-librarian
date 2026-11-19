using System.Net.Http.Json;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// P4-B over the real host and database: the web endpoint maps the shared answer's cards and qualifications, a Matrix turn carries
/// the same answer, and an informational question leaves every durable table except the conversation's own bookkeeping untouched.
/// The answer service is scripted, so nothing here reaches a metadata source: these are routing, transport and write-freedom checks.
/// </summary>
[TestClass]
public sealed class ConversationAnswerEndpointTests
{
    private static WebTestFixture? fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null) await fixture.DisposeAsync();
    }

    /// <summary>A deterministic answer facade: it counts what was asked and never touches a provider, a table or a clock.</summary>
    private sealed class ScriptedAnswers : ICatalogAnswerService
    {
        public int Calls { get; private set; }

        public string LastText { get; private set; } = string.Empty;

        /// <summary>Runs while an answer is being prepared, so a test can change the world during the awaited turn.</summary>
        public Func<Task>? DuringAnswer { get; set; }

        public async Task<CatalogAnswer> AnswerAsync(CatalogAnswerCaller caller, CatalogAnswerRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (DuringAnswer is not null) await DuringAnswer();
            LastText = request.Text ?? string.Empty;
            return new CatalogAnswer
            {
                Operation = request.Operation,
                Outcome = CatalogAnswerOutcome.Qualified,
                Position = request.Position,
                Subject = CatalogSubjectRef.FromSource(
                    CatalogSubjectKind.Series, "The Lantern Road", new("openlibrary", "OL-SAGA", Domain.Catalog.ExternalReferenceEntityType.Series)),
                Entries =
                [
                    new CatalogAnswerEntry
                    {
                        Source = new("openlibrary", "OL1W", Domain.Catalog.ExternalReferenceEntityType.Work),
                        Title = "The Lantern Road",
                        Positions = [new("1", 1m, null, new("openlibrary", "OL-SAGA", Domain.Catalog.ExternalReferenceEntityType.Series))],
                        Publication = Application.Catalog.Discovery.PublicationEvidence.Dated(
                            new DateOnly(2001, 5, 1), Domain.Catalog.DatePrecision.Day, Domain.Catalog.DateBasis.Original),
                        ProviderId = "openlibrary",
                        SeriesLabel = "The Lantern Road"
                    },
                    new CatalogAnswerEntry
                    {
                        Source = new("openlibrary", "OL2W", Domain.Catalog.ExternalReferenceEntityType.Work),
                        Title = "Lanterns at Dusk",
                        Positions = [new("2", 2m, null, new("openlibrary", "OL-SAGA", Domain.Catalog.ExternalReferenceEntityType.Series))],
                        Publication = Application.Catalog.Discovery.PublicationEvidence.Dated(
                            new DateOnly(2005, 3, 9), Domain.Catalog.DatePrecision.Day, Domain.Catalog.DateBasis.Original),
                        ProviderId = "openlibrary",
                        SeriesLabel = "The Lantern Road"
                    }
                ],
                Qualifiers = [CatalogAnswerCodes.ListNotExhaustive],
                Today = new DateOnly(2026, 11, 18)
            };
        }

        public Task<CatalogEditionsAnswer> EditionsAsync(
            CatalogAnswerCaller caller, Guid contextRootId, CatalogSubjectRef entry, CancellationToken cancellationToken) =>
            throw new NotSupportedException("No edition expansion is expected in these checks.");
    }

    private static FamilyLibrarianAppFactory Factory(ScriptedAnswers answers, ConversationOptions? options = null) =>
        new(WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            services.RemoveAll<ConversationOptions>();
            services.AddSingleton(options ?? new ConversationOptions { Enabled = true, MatrixEnabled = true, WebEnabled = true });
            services.RemoveAll<ICatalogAnswerService>();
            services.AddSingleton<ICatalogAnswerService>(answers);
        });

    private static readonly string[] DurableTables =
    [
        "catalog.authors", "catalog.works", "catalog.work_authors", "catalog.editions", "catalog.series",
        "catalog.series_entries", "catalog.external_references", "requests.book_requests", "requests.request_participants",
        "requests.request_formats", "requests.request_status_history", "following.follows", "notifications.notification_events",
        "notifications.notification_receipts", "communications.outbound_communications",
        "communications.outbound_communication_deliveries", "audit.audit_events"
    ];

    private static async Task<Dictionary<string, string>> SnapshotAsync()
    {
        await using var connection = new NpgsqlConnection(WebTestFixture.Require(fixture).ConnectionString);
        await connection.OpenAsync();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in DurableTables)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT count(*)::text || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM {table} t", connection);
            result[table] = (string)(await command.ExecuteScalarAsync())!;
        }

        return result;
    }

    [TestMethod]
    public async Task TheWebEndpointReturnsTheAnswersCardsAndItsQualifications()
    {
        var answers = new ScriptedAnswers();
        await using var factory = Factory(answers);
        using var client = await ConversationEndpointTestsHarness.ReaderAsync(factory);

        var reply = await ConversationEndpointTestsHarness.SayAsync(client, "reading order of Lantern Road");

        Assert.AreEqual(1, answers.Calls);
        Assert.AreEqual("Lantern Road", answers.LastText);
        Assert.AreEqual("Answer", reply.Kind);
        var card = reply.Answers.Single();
        Assert.AreEqual("answer", card.Kind);
        StringAssert.Contains(card.Heading, "in order");
        Assert.AreEqual("1. The Lantern Road", card.Lines[0].Title);
        Assert.AreEqual("2. Lanterns at Dusk", card.Lines[1].Title);
        Assert.IsTrue(card.Notes.Any(note => note.Contains("not a complete list", StringComparison.Ordinal)));
        StringAssert.Contains(reply.Text, "The Lantern Road");
    }

    [TestMethod]
    public async Task AWebTurnAndAMatrixTurnOfTheSameQuestionCarryTheSameAnswerText()
    {
        var answers = new ScriptedAnswers();
        await using var factory = Factory(answers);
        using var client = await ConversationEndpointTestsHarness.ReaderAsync(factory);
        var reader = await ConversationHostTests.CreateLinkedUserAsync(factory);

        var web = await ConversationEndpointTestsHarness.SayAsync(client, "reading order of Lantern Road");
        var matrix = await ConversationHostTests.SayAsMatrixAsync(factory, reader, "reading order of Lantern Road", "$answer1");

        Assert.AreEqual(2, answers.Calls);
        Assert.AreEqual(web.Text, matrix.Reply.Text, "Both transports show the same answer, from the same contract.");
        Assert.AreEqual(web.Kind, matrix.Reply.Kind.ToString());
        Assert.IsLessThanOrEqualTo(1500, matrix.Reply.Text.Length, "The Matrix wording fits its presentation bound.");
    }

    [TestMethod]
    public async Task AButtonDrawnForAnEarlierListDoesNothingOnceANewerListWasShownAndTheCurrentOneStillWorks()
    {
        var answers = new ScriptedAnswers();
        await using var factory = Factory(answers);
        using var client = await ConversationEndpointTestsHarness.ReaderAsync(factory);

        var first = await ConversationEndpointTestsHarness.SayAsync(client, "reading order of Lantern Road");
        var second = await ConversationEndpointTestsHarness.SayAsync(client, "reading order of Lantern Road");

        Assert.IsFalse(string.IsNullOrEmpty(first.ListId), "A reply with numbered choices names its list.");
        Assert.IsFalse(string.IsNullOrEmpty(second.ListId));
        Assert.AreNotEqual(first.ListId, second.ListId, "A new list is a new identity.");
        var asked = answers.Calls;

        var stale = await ConversationEndpointTestsHarness.SayAsync(client, "1", listId: first.ListId);
        var staleRequest = await ConversationEndpointTestsHarness.SayAsync(client, "request 1 audiobook", listId: first.ListId);

        Assert.AreEqual("NotUnderstood", stale.Kind);
        StringAssert.Contains(stale.Text, "earlier list");
        Assert.AreEqual("NotUnderstood", staleRequest.Kind, "An old button cannot reach the request tools either.");
        Assert.AreEqual(asked, answers.Calls, "A stale press makes no lookup.");

        var current = await ConversationEndpointTestsHarness.SayAsync(client, "1", listId: second.ListId);
        Assert.AreEqual("AskFormat", current.Kind, "The button for the newest list still works.");
        Assert.AreEqual(second.ListId, current.ListId, "The format step stays on the list it came from.");

        // Typed text names no list, so it always addresses the newest one.
        var typed = await ConversationEndpointTestsHarness.SayAsync(client, "ebook");
        Assert.AreNotEqual("NotUnderstood", typed.Kind);
    }

    [TestMethod]
    public async Task ATurnWhoseAccountWasWithdrawnWhileItWasRunningIsNeverSentIntoTheMatrixRoom()
    {
        var answers = new ScriptedAnswers();
        var matrix = new QuestionMatrixClient();
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            services.RemoveAll<ConversationOptions>();
            services.AddSingleton(new ConversationOptions { Enabled = true, MatrixEnabled = true, WebEnabled = true });
            services.RemoveAll<ICatalogAnswerService>();
            services.AddSingleton<ICatalogAnswerService>(answers);
            services.RemoveAll<IMatrixClient>();
            services.AddSingleton<IMatrixClient>(matrix);
        });
        var reader = await ConversationHostTests.CreateLinkedUserAsync(factory);
        await SeedMatrixSettingsAsync(factory);

        // A control: the same question for an account that stays active is answered into the room.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MatrixInboundRouter>().HandleMessageAsync(
                new MatrixInboundMessage(reader.Room, reader.MatrixId, "reading order of Lantern Road", EventId: "$kept"), CancellationToken.None);
        }

        Assert.AreEqual(1, answers.Calls, "The control turn reached the answer service.");
        Assert.IsNotNull(matrix.LastReply, "The control turn was sent into the room.");
        StringAssert.Contains(matrix.LastReply, "The Lantern Road");

        matrix.Clear();
        answers.DuringAnswer = () => DisableAsync(factory, reader.UserId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MatrixInboundRouter>().HandleMessageAsync(
                new MatrixInboundMessage(reader.Room, reader.MatrixId, "reading order of Lantern Road", EventId: "$withdrawn"), CancellationToken.None);
        }

        Assert.IsNull(matrix.LastReply, "The account was withdrawn while the answer was being prepared, so nothing is sent.");
    }

    private static async Task SeedMatrixSettingsAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ICredentialProtector>();
        var settings = new MatrixSettings(DateTimeOffset.UtcNow);
        settings.SetSettings("https://matrix.example.test", "@bot:example.test", null, DateTimeOffset.UtcNow);
        settings.SetAccessToken(
            protector.Protect(CommunicationSecretPurposes.MatrixAccessToken, "test-token"), protector.FormatVersion, null, DateTimeOffset.UtcNow);
        settings.SetEnabled(true, null, DateTimeOffset.UtcNow);
        database.MatrixSettings.Add(settings);
        await database.SaveChangesAsync();
    }

    /// <summary>Disables the account from a scope of its own, as an administrator's action would.</summary>
    private static async Task DisableAsync(FamilyLibrarianAppFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId.ToString());
        user!.Status = UserStatus.Disabled;
        Assert.IsTrue((await users.UpdateAsync(user)).Succeeded);
    }

    [TestMethod]
    public async Task TheAccountStoreReadsAStatusChangedFromAnotherScopeEvenAfterItAlreadyReadItInThisOne()
    {
        await using var factory = Factory(new ScriptedAnswers());
        var reader = await ConversationHostTests.CreateLinkedUserAsync(factory);
        await using var original = factory.Services.CreateAsyncScope();
        var store = original.ServiceProvider.GetRequiredService<IUserAccountStore>();
        Assert.AreEqual(UserStatus.Active, (await store.FindAsync(reader.UserId, CancellationToken.None))!.Status);

        await DisableAsync(factory, reader.UserId);

        Assert.AreEqual(
            UserStatus.Disabled, (await store.FindAsync(reader.UserId, CancellationToken.None))!.Status,
            "A scope that read the account earlier must not keep seeing it as active.");
    }

    /// <summary>The production book tools with a hook while a source record is being resolved, so the world can change between the awaits.</summary>
    private sealed class HookedTools(IConversationTools inner, Func<Task>? afterResolve, Action onCreate) : IConversationTools
    {
        public Task<ConversationSearchResult> SearchBooksAsync(string text, int maxChoices, CancellationToken cancellationToken) =>
            inner.SearchBooksAsync(text, maxChoices, cancellationToken);

        public Task<string?> GetBookDetailsAsync(ConversationChoiceRef choice, CancellationToken cancellationToken) =>
            inner.GetBookDetailsAsync(choice, cancellationToken);

        public Task<IReadOnlyList<Application.Requests.BookRequestView>> GetMyRequestsAsync(CancellationToken cancellationToken) =>
            inner.GetMyRequestsAsync(cancellationToken);

        public Task<ConversationLibraryResult?> CheckLibraryAsync(string text, CancellationToken cancellationToken) =>
            inner.CheckLibraryAsync(text, cancellationToken);

        public Task<ConversationLibraryResult?> CheckLibraryForChoiceAsync(ConversationChoiceRef choice, CancellationToken cancellationToken) =>
            inner.CheckLibraryForChoiceAsync(choice, cancellationToken);

        public async Task<Guid?> ResolveWorkAsync(ConversationChoiceRef choice, CancellationToken cancellationToken)
        {
            if (afterResolve is not null) await afterResolve();
            return Guid.NewGuid();
        }

        public Task<Application.Catalog.SeriesLookupResult> FindSeriesBooksAsync(
            string seriesName, decimal? position, string? afterTitle, int maxBooks, CancellationToken cancellationToken) =>
            inner.FindSeriesBooksAsync(seriesName, position, afterTitle, maxBooks, cancellationToken);

        public Task<Application.Requests.CreateBookRequestResult> CreateBookRequestAsync(
            Guid workId, IReadOnlyList<Domain.Requests.RequestMediaType> formats, CancellationToken cancellationToken)
        {
            onCreate();
            return inner.CreateBookRequestAsync(workId, formats, cancellationToken);
        }
    }

    [TestMethod]
    public async Task AnAccountDisabledFromAnotherScopeWhileARequestIsBeingResolvedCreatesNoRequestOnRealPostgreSql()
    {
        var answers = new ScriptedAnswers();
        var createCalls = 0;
        Func<Task>? afterResolve = null;
        await using var factory = new FamilyLibrarianAppFactory(WebTestFixture.Require(fixture).ConnectionString, services =>
        {
            services.RemoveAll<ConversationOptions>();
            services.AddSingleton(new ConversationOptions { Enabled = true, MatrixEnabled = true, WebEnabled = true });
            services.RemoveAll<ICatalogAnswerService>();
            services.AddSingleton<ICatalogAnswerService>(answers);
            services.RemoveAll<IConversationTools>();
            services.AddScoped<IConversationTools>(provider =>
                new HookedTools(ActivatorUtilities.CreateInstance<ConversationBookTools>(provider), afterResolve is null ? null : () => afterResolve(), () => createCalls++));
        });
        var reader = await ConversationHostTests.CreateLinkedUserAsync(factory);
        using var client = await ConversationEndpointTestsHarness.ReaderAsync(factory, reader.Email);

        // A control: with the account active the request reaches the create tool.
        await ConversationEndpointTestsHarness.SayAsync(client, "reading order of Lantern Road");
        using (var control = await client.PostAsJsonAsync(
            "/api/v1/conversation/messages",
            new FamilyLibrarian.Contracts.Conversation.ConversationMessageRequest("request 1 audiobook", Guid.NewGuid().ToString("N"))))
        {
            Assert.IsTrue(control.IsSuccessStatusCode);
            Assert.AreEqual(1, createCalls, "The control request reached the create tool.");
        }

        afterResolve = () => DisableAsync(factory, reader.UserId);
        await ConversationEndpointTestsHarness.SayAsync(client, "reading order of Lantern Road");
        var before = await SnapshotAsync();
        var createdBefore = createCalls;
        using var withdrawn = await client.PostAsJsonAsync(
            "/api/v1/conversation/messages",
            new FamilyLibrarian.Contracts.Conversation.ConversationMessageRequest("request 1 audiobook", Guid.NewGuid().ToString("N")));

        Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, withdrawn.StatusCode, "The turn's own scope saw the withdrawal.");
        Assert.AreEqual(createdBefore, createCalls, "No request was created after the account was disabled.");
        var after = await SnapshotAsync();
        foreach (var table in DurableTables)
        {
            Assert.AreEqual(before[table], after[table], $"{table} changed after the account was withdrawn.");
        }
    }

    [TestMethod]
    public async Task AnInformationalAnswerLeavesEveryDurableTableButTheConversationBookkeepingUntouched()
    {
        var answers = new ScriptedAnswers();
        await using var factory = Factory(answers);
        using var client = await ConversationEndpointTestsHarness.ReaderAsync(factory);
        var before = await SnapshotAsync();

        await ConversationEndpointTestsHarness.SayAsync(client, "reading order of Lantern Road");
        await ConversationEndpointTestsHarness.SayAsync(client, "details 1");
        await ConversationEndpointTestsHarness.SayAsync(client, "is it available?");

        var after = await SnapshotAsync();

        foreach (var table in DurableTables)
        {
            Assert.AreEqual(before[table], after[table], $"{table} changed during a read-only answer.");
        }
    }
}

/// <summary>The endpoint plumbing the chat tests share, kept in one place so a new chat test does not duplicate sign-in code.</summary>
internal static class ConversationEndpointTestsHarness
{
    public static async Task<HttpClient> ReaderAsync(FamilyLibrarianAppFactory factory, string? email = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new FamilyLibrarian.Contracts.Authentication.LoginRequest { Email = email ?? WebTestFixture.UserEmail, Password = WebTestFixture.UserPassword });
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(client));
        return client;
    }

    public static async Task<FamilyLibrarian.Contracts.Conversation.ConversationReplyResponse> SayAsync(
        HttpClient client, string text, string? messageId = null, string? listId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var response = await client.PostAsJsonAsync(
            "/api/v1/conversation/messages",
            new FamilyLibrarian.Contracts.Conversation.ConversationMessageRequest(text, messageId ?? Guid.NewGuid().ToString("N"), listId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FamilyLibrarian.Contracts.Conversation.ConversationReplyResponse>())!;
    }
}
