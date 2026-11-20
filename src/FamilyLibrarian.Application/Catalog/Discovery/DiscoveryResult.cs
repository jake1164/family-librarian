using System.Text.RegularExpressions;

namespace FamilyLibrarian.Application.Catalog.Discovery;

/// <summary>Stable lower-case codes. Provider error text, URLs and headers never travel in a result; only these do.</summary>
public static partial class DiscoveryCodes
{
    public const int MaxLength = 64;

    // Notes and stop reasons used across adapters. Adapters may add codes of the same shape.
    public const string AnchorInsufficient = "anchor-insufficient";
    public const string PartialFieldError = "partial-field-error";
    public const string NoNativeSeriesId = "no-native-series-id";
    public const string RepeatedCursor = "repeated-cursor";
    public const string DuplicateDropped = "duplicate-dropped";
    public const string ProviderMutatedBetweenPages = "provider-mutated-between-pages";
    public const string PageCeiling = "page-ceiling";
    public const string AttemptCeiling = "attempt-ceiling";
    public const string DeadlineReached = "deadline-reached";
    public const string RetryAfterTooLong = "retry-after-too-long";
    public const string QueryCapacity = "query-capacity";
    public const string JourneyExpired = "journey-expired";
    public const string ItemMissingId = "item-missing-id";
    public const string SeriesCanonicalStated = "series-canonical-stated";
    public const string CanonicalTargetNotChecked = "canonical-target-not-checked";
    public const string PrimaryAuthorOnly = "primary-author-only";
    public const string CursorInvalid = "cursor-invalid";
    public const string SubjectInvalid = "subject-invalid";
    public const string SetupMissing = "setup-missing";
    public const string AuthorizationDenied = "authorization-denied";
    public const string ProviderError = "provider-error";
    public const string Throttled = "throttled";
    public const string ListTruncated = "list-truncated";

    public static bool IsCode(string? value) =>
        value is { Length: > 0 and <= MaxLength } && CodePattern().IsMatch(value);

    public static void ThrowIfNotCode(string? value)
    {
        if (!IsCode(value))
        {
            throw new ArgumentException("Expected a stable lower-case code, not free text.", nameof(value));
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

public enum DiscoveryOperation
{
    FindSeriesForBook = 1,
    GetSeriesMembers = 2,
    ResolveAuthor = 3,
    GetAuthorWorks = 4,
    GetAuthorSeries = 5,

    /// <summary>
    /// One ordinary book search read under a journey's budget (see <see cref="IBudgetedBookMetadataProvider"/>). A seed read, not a
    /// discovery capability: it never appears in a <see cref="DiscoveryCapabilities"/> table and is never mislabelled as a series call.
    /// </summary>
    SearchBooks = 6,

    /// <summary>One ordinary book detail read under a journey's budget. A seed read, not a discovery capability.</summary>
    GetBookDetails = 7,

    /// <summary>
    /// One read of a Work's edition titles under a journey's budget (see <see cref="IWorkEditionTitlesProvider"/>). An expansion,
    /// not a discovery capability: it never appears in a <see cref="DiscoveryCapabilities"/> table.
    /// </summary>
    GetWorkEditions = 8
}

public static class DiscoveryOperations
{
    /// <summary>True for the five author/series discovery operations an integration declares capabilities for; false for the seed/detail reads.</summary>
    public static bool IsDiscoveryCapability(this DiscoveryOperation operation) =>
        operation is >= DiscoveryOperation.FindSeriesForBook and <= DiscoveryOperation.GetAuthorSeries;
}

public enum CapabilityState
{
    Supported = 1,

    /// <summary>The documented API has no such operation (a cited limit, not a runtime condition).</summary>
    Unsupported = 2,

    /// <summary>Not yet verified for this API, access tier or corpus. Not a negative result.</summary>
    Unverified = 3
}

public sealed record DiscoveryCapability(CapabilityState State, string ReasonCode)
{
    public string ReasonCode { get; } = DiscoveryCodesChecked(ReasonCode);

    public static DiscoveryCapability Supported { get; } = new(CapabilityState.Supported, "supported");

    public static DiscoveryCapability Unsupported(string reason) => new(CapabilityState.Unsupported, reason);

    public static DiscoveryCapability Unverified(string reason) => new(CapabilityState.Unverified, reason);

    private static string DiscoveryCodesChecked(string reason)
    {
        DiscoveryCodes.ThrowIfNotCode(reason);
        return reason;
    }
}

/// <summary>
/// What an integration implements, kept apart from whether it can run right now. An operation not declared is
/// <see cref="CapabilityState.Unverified"/>, never silently supported or unsupported.
/// </summary>
public sealed class DiscoveryCapabilities
{
    private readonly Dictionary<DiscoveryOperation, DiscoveryCapability> declared;

    public DiscoveryCapabilities(IEnumerable<KeyValuePair<DiscoveryOperation, DiscoveryCapability>> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        declared = declarations.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    public DiscoveryCapability Get(DiscoveryOperation operation) =>
        declared.TryGetValue(operation, out var capability) ? capability : DiscoveryCapability.Unverified("not-declared");
}

public enum DiscoveryOutcome
{
    /// <summary>At least one item was returned from a successful check.</summary>
    Ok = 1,

    /// <summary>The stated scope was checked successfully and held nothing. Never global absence.</summary>
    Empty = 2,

    /// <summary>Several candidates fit and none was selected. Items are choices.</summary>
    Ambiguous = 3,

    /// <summary>Some valid items were obtained before a failure; see <see cref="DiscoveryResult{T}.Notes"/>.</summary>
    Partial = 4,

    /// <summary>The operation does not exist for this integration or API. No provider call was made.</summary>
    Unsupported = 5,

    /// <summary>Not yet verified. No provider call was made, or the call could not settle it.</summary>
    Unverified = 6,

    /// <summary>Runtime condition: disabled, setup missing or authorization denied. Not a capability statement.</summary>
    Unavailable = 7,

    Failed = 8,

    /// <summary>The provider-work deadline passed. Items observed so far are kept.</summary>
    TimedOut = 9,

    /// <summary>The provider asked for a wait that does not fit the remaining budget. Items observed so far are kept.</summary>
    Throttled = 10,

    /// <summary>
    /// The provider answered with source records, but none could be used (no usable id or title). Not <see cref="Empty"/>: the
    /// records existed. <see cref="DiscoveryResult{T}.Completion"/> and <see cref="DiscoveryResult{T}.Next"/> say whether the
    /// scope continues, and the notes say why the records were dropped.
    /// </summary>
    NoUsableItems = 11
}

/// <summary>How far the checked scope was read. A continuation cursor is separate: it means more exists, not that budget allows it.</summary>
public enum DiscoveryCompletion
{
    /// <summary>The check did not run or did not finish enough to say anything.</summary>
    NotChecked = 0,

    /// <summary>The provider had no further pages for this scope. Not a statement that the bibliography is complete.</summary>
    ExhaustedInCheckedScope = 1,

    /// <summary>A normal next page exists and a continuation was issued for it.</summary>
    MoreAvailable = 2,

    /// <summary>An attempt, page or time ceiling stopped the read and no continuation is offered.</summary>
    BudgetTruncated = 3,

    /// <summary>Items were cut by a per-operation item bound.</summary>
    ItemTruncated = 4,

    /// <summary>The temporary preview context could not retain every item.</summary>
    ContextCapacity = 5,

    ProviderFailedPartial = 6
}

/// <summary>What was actually asked, so an empty answer is never read wider than the question.</summary>
public sealed record DiscoveryScope(DiscoveryOperation Operation, SourceRef? Subject, string? Filter = null, string? Ordering = null);

/// <summary>Provider-side position of the next page. Opaque to callers; the host wraps it in a scoped, owner-bound token.</summary>
public sealed record DiscoveryCursor
{
    public const int MaxLength = 1024;

    public DiscoveryCursor(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength)
        {
            throw new ArgumentException("A cursor is required and is never truncated.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
}

/// <summary>Actual physical use, as dispatched. Cache hits are reported separately and cost no attempt.</summary>
public sealed record DiscoveryUsage(int Attempts, int Retries, int Pages, int Items, int CacheHits, TimeSpan Elapsed)
{
    public static DiscoveryUsage None { get; } = new(0, 0, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// One answer to a discovery operation. Built only through the factories, which enforce the state combinations, so an
/// empty-after-error, an exhausted-but-truncated or a cursor-without-continuation result cannot be constructed.
/// </summary>
public sealed class DiscoveryResult<T>
{
    internal DiscoveryResult(
        DiscoveryOutcome outcome,
        IReadOnlyList<T> items,
        DiscoveryScope scope,
        DiscoveryCompletion completion,
        DiscoveryCursor? next,
        DateTimeOffset observedAtUtc,
        DiscoveryUsage usage,
        IReadOnlyList<string> notes)
    {
        // Copies first, then validates the copies: a caller that keeps its own list can neither empty an Ok result nor append
        // unvalidated text to the notes after the check.
        var keptNotes = DiscoverySnapshots.Of(notes);
        foreach (var note in keptNotes)
        {
            DiscoveryCodes.ThrowIfNotCode(note);
        }

        Outcome = outcome;
        Items = DiscoverySnapshots.Of(items);
        Scope = scope;
        Completion = completion;
        Next = next;
        ObservedAtUtc = observedAtUtc;
        Usage = usage;
        Notes = keptNotes;
    }

    public DiscoveryOutcome Outcome { get; }

    public IReadOnlyList<T> Items { get; }

    public DiscoveryScope Scope { get; }

    public DiscoveryCompletion Completion { get; }

    /// <summary>Present only with <see cref="DiscoveryCompletion.MoreAvailable"/>.</summary>
    public DiscoveryCursor? Next { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public DiscoveryUsage Usage { get; }

    public IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// Keeps at most <paramref name="capacity"/> items for a bounded preview context. Cut items are reported as
    /// <see cref="DiscoveryCompletion.ContextCapacity"/>, the continuation is withheld (the dropped items would be skipped),
    /// and the result is never left claiming exhaustion.
    /// </summary>
    public DiscoveryResult<T> Retain(int capacity)
    {
        // A context that can hold nothing is "navigation unavailable", decided by the host; it is not a smaller result.
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (Items.Count <= capacity)
        {
            return this;
        }

        var kept = Items.Take(capacity).ToArray();
        // A failure that already cut the read stays the more important statement than the capacity cut.
        var completion = Completion == DiscoveryCompletion.ProviderFailedPartial
            ? DiscoveryCompletion.ProviderFailedPartial
            : DiscoveryCompletion.ContextCapacity;
        return new(Outcome, kept, Scope, completion, null, ObservedAtUtc, Usage, Notes);
    }
}

/// <summary>Factories for <see cref="DiscoveryResult{T}"/>; a non-generic home so callers infer the item type.</summary>
public static class DiscoveryResult
{
    /// <summary>Items were returned and the scope was read as far as the completion says.</summary>
    public static DiscoveryResult<T> Found<T>(
        IReadOnlyList<T> items,
        DiscoveryScope scope,
        DiscoveryCompletion completion,
        DateTimeOffset observedAtUtc,
        DiscoveryUsage usage,
        DiscoveryCursor? next = null,
        IReadOnlyList<string>? notes = null)
    {
        var kept = DiscoverySnapshots.Of(items);
        if (kept.Count == 0)
        {
            throw new ArgumentException("Use Empty for a successful check that found nothing.", nameof(items));
        }

        RequireReadCompletion(completion, next);
        return new DiscoveryResult<T>(DiscoveryOutcome.Ok, kept, scope, completion, next, observedAtUtc, usage, notes ?? []);
    }

    /// <summary>The scope was checked successfully to its end and held nothing.</summary>
    public static DiscoveryResult<T> Empty<T>(
        DiscoveryScope scope, DateTimeOffset observedAtUtc, DiscoveryUsage usage, IReadOnlyList<string>? notes = null) =>
        new DiscoveryResult<T>(DiscoveryOutcome.Empty, [], scope, DiscoveryCompletion.ExhaustedInCheckedScope, null, observedAtUtc, usage, notes ?? []);

    /// <summary>Several candidates fit; none is selected. An empty choice list is <see cref="Empty"/>, not this.</summary>
    public static DiscoveryResult<T> Choices<T>(
        IReadOnlyList<T> choices,
        DiscoveryScope scope,
        DateTimeOffset observedAtUtc,
        DiscoveryUsage usage,
        IReadOnlyList<string>? notes = null)
    {
        var kept = DiscoverySnapshots.Of(choices);
        if (kept.Count == 0)
        {
            throw new ArgumentException("Use Empty when there is nothing to choose from.", nameof(choices));
        }

        return new DiscoveryResult<T>(DiscoveryOutcome.Ambiguous, kept, scope, DiscoveryCompletion.ExhaustedInCheckedScope, null, observedAtUtc, usage, notes ?? []);
    }

    /// <summary>Valid items observed before a failure, with at least one stable reason code.</summary>
    public static DiscoveryResult<T> PartialSuccess<T>(
        IReadOnlyList<T> items,
        DiscoveryScope scope,
        DateTimeOffset observedAtUtc,
        DiscoveryUsage usage,
        IReadOnlyList<string> notes)
    {
        var kept = DiscoverySnapshots.Of(items);
        var keptNotes = DiscoverySnapshots.Of(notes);
        if (kept.Count == 0 || keptNotes.Count == 0)
        {
            throw new ArgumentException("A partial result needs retained items and a reason.", nameof(items));
        }

        return new DiscoveryResult<T>(DiscoveryOutcome.Partial, kept, scope, DiscoveryCompletion.ProviderFailedPartial, null, observedAtUtc, usage, keptNotes);
    }

    /// <summary>
    /// The provider answered with source records and none was usable (for example every row lacked an id). Never read as
    /// <see cref="Empty{T}"/>: the records exist, they were just not usable. Completion says whether the scope goes on
    /// (<see cref="DiscoveryCompletion.MoreAvailable"/> with its cursor) or the provider has nothing further, and the notes
    /// say why the records were dropped.
    /// </summary>
    public static DiscoveryResult<T> NoUsableItems<T>(
        DiscoveryScope scope,
        DiscoveryCompletion completion,
        DiscoveryCursor? next,
        DateTimeOffset observedAtUtc,
        DiscoveryUsage usage,
        IReadOnlyList<string> notes)
    {
        var keptNotes = DiscoverySnapshots.Of(notes);
        if (keptNotes.Count == 0)
        {
            throw new ArgumentException("Dropped records need a reason code.", nameof(notes));
        }

        RequireReadCompletion(completion, next);
        return new DiscoveryResult<T>(DiscoveryOutcome.NoUsableItems, [], scope, completion, next, observedAtUtc, usage, keptNotes);
    }

    /// <summary>A result with no items: unsupported, unverified, unavailable, failed, timed out or throttled. Never read as Empty.</summary>
    public static DiscoveryResult<T> NotAnswered<T>(
        DiscoveryOutcome outcome,
        DiscoveryScope scope,
        DateTimeOffset observedAtUtc,
        DiscoveryUsage usage,
        IReadOnlyList<string> notes)
    {
        if (outcome is DiscoveryOutcome.Ok or DiscoveryOutcome.Empty or DiscoveryOutcome.Ambiguous or DiscoveryOutcome.Partial
            or DiscoveryOutcome.NoUsableItems)
        {
            throw new ArgumentException("This outcome carries an answer; use its own factory.", nameof(outcome));
        }

        var keptNotes = DiscoverySnapshots.Of(notes);
        if (keptNotes.Count == 0)
        {
            throw new ArgumentException("An unanswered result needs a reason code.", nameof(notes));
        }

        return new DiscoveryResult<T>(outcome, [], scope, DiscoveryCompletion.NotChecked, null, observedAtUtc, usage, keptNotes);
    }

    /// <summary>A timeout or throttle that still holds observed items.</summary>
    public static DiscoveryResult<T> Interrupted<T>(
        DiscoveryOutcome outcome,
        IReadOnlyList<T> items,
        DiscoveryScope scope,
        DateTimeOffset observedAtUtc,
        DiscoveryUsage usage,
        IReadOnlyList<string> notes)
    {
        var kept = DiscoverySnapshots.Of(items);
        var keptNotes = DiscoverySnapshots.Of(notes);
        if (outcome is not (DiscoveryOutcome.TimedOut or DiscoveryOutcome.Throttled or DiscoveryOutcome.Failed) || kept.Count == 0 || keptNotes.Count == 0)
        {
            throw new ArgumentException("Interrupted needs a timeout, throttle or failure, items and a reason.", nameof(outcome));
        }

        return new DiscoveryResult<T>(outcome, kept, scope, DiscoveryCompletion.ProviderFailedPartial, null, observedAtUtc, usage, keptNotes);
    }

    private static void RequireReadCompletion(DiscoveryCompletion completion, DiscoveryCursor? next)
    {
        switch (completion)
        {
            case DiscoveryCompletion.MoreAvailable when next is null:
                throw new ArgumentException("MoreAvailable requires a continuation cursor.", nameof(next));
            case DiscoveryCompletion.MoreAvailable:
                return;
            case DiscoveryCompletion.ExhaustedInCheckedScope or DiscoveryCompletion.BudgetTruncated
                or DiscoveryCompletion.ItemTruncated or DiscoveryCompletion.ContextCapacity:
                if (next is not null)
                {
                    throw new ArgumentException("Only MoreAvailable carries a continuation cursor.", nameof(next));
                }

                return;
            default:
                throw new ArgumentException("Found results are exhausted, more-available or truncated, not unchecked or failed.", nameof(completion));
        }
    }
}
