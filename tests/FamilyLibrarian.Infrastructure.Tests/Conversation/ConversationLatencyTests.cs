using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Infrastructure.Conversation;
using Microsoft.Extensions.Logging;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>
/// A hung catalog or library source must not leave a person waiting for an HTTP timeout: each slow lookup has a budget, running
/// out of it is reported as "didn't respond" (never as "not found" or "not in the library"), and where the time goes is logged.
/// </summary>
[TestClass]
public sealed class ConversationLatencyTests
{
    private static readonly ConversationTimeBudgets Tiny = new(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150));

    private sealed class User : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.NewGuid();

        public string? DisplayName => null;
    }

    private sealed class FakeSearch(Func<CancellationToken, Task<CatalogSearchOutcome>> search) : ICatalogSearchService
    {
        public int Calls { get; private set; }

        public Task<CatalogSearchOutcome> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            return search(cancellationToken);
        }

        public Task<ProviderSearchResult> SearchProviderAsync(
            IBookMetadataProvider provider, BookSearchQuery query, bool tolerateUnexpectedFailures, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public CatalogSearchOutcome Compose(IReadOnlyList<ProviderSearchResult> providerResults, BookSearchQuery query) => throw new NotSupportedException();
    }

    private sealed class FakeAvailability(Func<CancellationToken, Task<CandidateAvailabilityResult>> check) : ICandidateAvailabilityService
    {
        public int Calls { get; private set; }

        public Task<CandidateAvailabilityResult> GetAvailabilityAsync(BookIdentity identity, CancellationToken cancellationToken)
        {
            Calls++;
            return check(cancellationToken);
        }

        public IAsyncEnumerable<CandidateAvailabilityUpdate> GetAvailabilityUpdatesAsync(BookIdentity identity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Recorder : IConversationDiagnostics
    {
        public List<(string Step, string Outcome)> Steps { get; } = [];

        public void StepCompleted(string stepName, TimeSpan elapsed, string outcome) => Steps.Add((stepName, outcome));
    }

    private static readonly BookCandidate Dune = new(
        "fake", "Fake catalog", "dune-1", "Dune", ["Frank Herbert"], null, null, null, [], []);

    private static CatalogSearchOutcome Found() => new([Dune], [], 1, false, []);

    private static CatalogSearchOutcome Empty() => new([], [], 1, false, []);

    private static Task<T> NeverCompletes<T>() => new TaskCompletionSource<T>().Task;

    private static ConversationBookTools Tools(FakeSearch search, FakeAvailability availability, Recorder? recorder = null) =>
        new(new User(), search, null!, null!, null!, availability, null!, recorder, Tiny);

    [TestMethod]
    public async Task AHungSearchIsReportedAsSourcesNotRespondingNotAsAnEmptyCatalog()
    {
        var recorder = new Recorder();
        var tools = Tools(new FakeSearch(_ => NeverCompletes<CatalogSearchOutcome>()), new FakeAvailability(_ => NeverCompletes<CandidateAvailabilityResult>()), recorder);

        var result = await tools.SearchBooksAsync("dune", 5, CancellationToken.None);

        Assert.IsEmpty(result.Choices);
        Assert.IsTrue(result.SomeSourcesUnavailable, "Running out of time must read as unreachable sources.");
        CollectionAssert.AreEqual(new[] { (ConversationSteps.CatalogSearch, "timed-out") }, recorder.Steps);
    }

    [TestMethod]
    public async Task ASearchThatAnswersInTimeIsUnchanged()
    {
        var recorder = new Recorder();
        var tools = Tools(new FakeSearch(_ => Task.FromResult(Found())), new FakeAvailability(_ => NeverCompletes<CandidateAvailabilityResult>()), recorder);

        var result = await tools.SearchBooksAsync("dune", 5, CancellationToken.None);

        Assert.AreEqual("Dune", result.Choices.Single().Title);
        Assert.IsFalse(result.SomeSourcesUnavailable);
        CollectionAssert.AreEqual(new[] { (ConversationSteps.CatalogSearch, "ok") }, recorder.Steps);
    }

    [TestMethod]
    public async Task ALookupThatHonoursItsCancellationTokenIsStoppedAtTheBudgetToo()
    {
        var tools = Tools(
            new FakeSearch(async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Empty();
            }),
            new FakeAvailability(_ => NeverCompletes<CandidateAvailabilityResult>()));

        var result = await tools.SearchBooksAsync("dune", 5, CancellationToken.None);

        Assert.IsTrue(result.SomeSourcesUnavailable);
    }

    [TestMethod]
    public async Task TheCallersOwnCancellationStillPropagatesAndIsNeverMistakenForATimeout()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var tools = Tools(new FakeSearch(token => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => Empty(), TaskScheduler.Default)),
            new FakeAvailability(_ => NeverCompletes<CandidateAvailabilityResult>()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => tools.SearchBooksAsync("dune", 5, cancelled.Token));
    }

    [TestMethod]
    public async Task AHungAvailabilityLookupMeansTheLibraryCheckIsIncompleteNotNotInTheLibrary()
    {
        var recorder = new Recorder();
        var availability = new FakeAvailability(_ => NeverCompletes<CandidateAvailabilityResult>());
        var tools = Tools(new FakeSearch(_ => Task.FromResult(Found())), availability, recorder);

        var result = await tools.CheckLibraryAsync("dune", CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Incomplete);
        Assert.AreEqual("Dune", result.Title);
        Assert.AreEqual("Frank Herbert", result.Authors);
        Assert.IsEmpty(result.OwnedFormats);
        CollectionAssert.AreEqual(
            new[] { (ConversationSteps.CatalogSearch, "ok"), (ConversationSteps.LibraryAvailability, "timed-out") }, recorder.Steps);
    }

    [TestMethod]
    public async Task AHungSearchMeansTheLibraryCheckIsIncompleteAndTheAvailabilitySourcesAreNotAsked()
    {
        var availability = new FakeAvailability(_ => NeverCompletes<CandidateAvailabilityResult>());
        var tools = Tools(new FakeSearch(_ => NeverCompletes<CatalogSearchOutcome>()), availability);

        var result = await tools.CheckLibraryAsync("dune", CancellationToken.None);

        Assert.IsTrue(result!.Incomplete);
        Assert.AreEqual(0, availability.Calls);
    }

    [TestMethod]
    public async Task ATimelyLibraryCheckAndAGenuinelyEmptyCatalogAreNotMarkedIncomplete()
    {
        var fast = Tools(new FakeSearch(_ => Task.FromResult(Found())), new FakeAvailability(_ => Task.FromResult(new CandidateAvailabilityResult([], []))));
        var empty = Tools(new FakeSearch(_ => Task.FromResult(Empty())), new FakeAvailability(_ => NeverCompletes<CandidateAvailabilityResult>()));

        var answered = await fast.CheckLibraryAsync("dune", CancellationToken.None);
        var notInCatalog = await empty.CheckLibraryAsync("zzzz", CancellationToken.None);

        Assert.IsFalse(answered!.Incomplete);
        Assert.IsEmpty(answered.OwnedFormats);
        Assert.IsNull(notInCatalog, "An empty catalog answer is still 'not found', not 'incomplete'.");
    }

    // ---- what the person is told ---------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnIncompleteLibraryCheckSaysItCouldNotFinishAndNeverClaimsTheBookIsMissing()
    {
        var harness = new ConversationHarness();
        harness.Tools.NextLibraryResult = new ConversationLibraryResult("Dune", "Frank Herbert", [], true, Incomplete: true);

        var result = await harness.SayAsync("Do we have Dune?");

        StringAssert.Contains(result.Reply.Text, "couldn't finish checking the library");
        StringAssert.Contains(result.Reply.Text, "Dune");
        Assert.IsFalse(result.Reply.Text.Contains("Not yet", StringComparison.Ordinal));
        Assert.IsFalse(result.Reply.Text.Contains("isn't in the library", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnIncompleteCheckWithNoTitleKnownStillReadsSensibly()
    {
        var harness = new ConversationHarness();
        harness.Tools.NextLibraryResult = new ConversationLibraryResult("fourth wing", string.Empty, [], true, Incomplete: true);

        var result = await harness.SayAsync("Do we have fourth wing?");

        StringAssert.Contains(result.Reply.Text, "couldn't finish checking the library for");
        StringAssert.Contains(result.Reply.Text, "Try again in a minute");
    }

    // ---- where the time goes ------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task EveryTurnReportsItsTotalTimeAndTheInterpreterReportsItsOwn()
    {
        var recorder = new Recorder();
        var harness = new ConversationHarness
        {
            Diagnostics = recorder,
            Interpreter = new Interpreter(new BookIntentInterpretation(BookIntent.OutOfDomain, null, null))
        };

        await harness.SayAsync("help");
        await harness.SayAsync("that sad book about a lighthouse keeper in australia");

        CollectionAssert.AreEqual(
            new[] { (ConversationSteps.Turn, "Help"), (ConversationSteps.Interpreter, "OutOfDomain"), (ConversationSteps.Turn, "OutOfDomain") },
            recorder.Steps);
    }

    private sealed class Interpreter(BookIntentInterpretation answer) : IBookIntentInterpreter
    {
        public Task<BookIntentInterpretation?> InterpretAsync(Guid userId, string text, string? previousSearch, CancellationToken cancellationToken) =>
            Task.FromResult<BookIntentInterpretation?>(answer);
    }

    private sealed class CapturingLogger : ILogger<ConversationDiagnostics>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [TestMethod]
    public void TheLogCarriesTheStepOutcomeAndMillisecondsAndFlagsSlowStepsAsWarningsWithNoContent()
    {
        var logger = new CapturingLogger();
        var diagnostics = new ConversationDiagnostics(logger);

        diagnostics.StepCompleted(ConversationSteps.CatalogSearch, TimeSpan.FromMilliseconds(420), "ok");
        diagnostics.StepCompleted(ConversationSteps.LibraryAvailability, TimeSpan.FromSeconds(61), "timed-out");

        Assert.AreEqual(LogLevel.Information, logger.Entries[0].Level);
        StringAssert.Contains(logger.Entries[0].Message, "catalog.search ok in 420 ms");
        Assert.AreEqual(LogLevel.Warning, logger.Entries[1].Level);
        StringAssert.Contains(logger.Entries[1].Message, "library.availability timed-out in 61000 ms");
    }
}
