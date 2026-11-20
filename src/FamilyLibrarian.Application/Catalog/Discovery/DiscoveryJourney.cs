using System.Security.Cryptography;

namespace FamilyLibrarian.Application.Catalog.Discovery;

/// <summary>
/// The reviewed Phase 3 ceilings. Initial application defaults, not provider quota promises. Callers never supply these;
/// the host constructs a journey with them.
/// </summary>
public sealed record DiscoveryLimits
{
    public static DiscoveryLimits Default { get; } = new();

    /// <summary>Physical provider HTTP attempts for one logical operation, retries and ancillary lookups included.</summary>
    public int OperationMaxAttempts { get; init; } = 5;

    public int OperationMaxRetries { get; init; } = 1;

    public int OperationMaxPages { get; init; } = 5;

    public TimeSpan OperationMaxWork { get; init; } = TimeSpan.FromSeconds(15);

    public int JourneyMaxAttempts { get; init; } = 12;

    public TimeSpan JourneyMaxWork { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Absolute lifetime from creation. Continuations and windows never extend it.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxOperationsPerJourney { get; init; } = 12;

    public int MaxContinuationsPerJourney { get; init; } = 20;
}

public enum AttemptAdmission
{
    Admitted = 0,
    JourneyExpired = 1,
    DeadlineExceeded = 2,
    RetryNotAllowed = 3,
    OperationAttemptsExhausted = 4,
    JourneyAttemptsExhausted = 5
}

/// <summary>
/// The server-owned handle an adapter and its HTTP pipeline consult before every physical request. Admission is atomic with
/// the counters, so concurrent or replayed requests cannot each spend the same remaining allowance. This is not a DTO a
/// caller can fill in.
/// </summary>
public interface IDiscoveryBudget
{
    /// <summary>Reserves one physical attempt before dispatch. A retry also needs the operation's single retry.</summary>
    AttemptAdmission TryAdmitAttempt(bool isRetry);

    /// <summary>Reserves one provider page. False once the operation's page ceiling is reached.</summary>
    bool TryAdmitPage();

    void RecordItems(int count);

    /// <summary>A result served without a provider request. Reported, never charged an attempt.</summary>
    void RecordCacheHit();

    /// <summary>Provider-work time still allowed: the smaller of the operation's and the journey's remainder, including any running work scope.</summary>
    TimeSpan RemainingWork { get; }

    DiscoveryUsage Usage { get; }

    /// <summary>
    /// Starts counting provider-work time (queueing, pacing, retries, body parsing, ancillary lookups). Time between a
    /// user's requests is never counted. Only one scope may run per journey at a time.
    /// </summary>
    IDisposable BeginWork();
}

public enum ContinuationStatus
{
    Accepted = 0,
    Unknown = 1,
    Foreign = 2,
    Expired = 3,
    WrongKind = 4,
    CrossOperation = 5
}

/// <summary>Everything except <see cref="ContinuationStatus.Accepted"/> means no provider call may be made.</summary>
public sealed record ContinuationRedemption(ContinuationStatus Status, DiscoveryOperationBudget? Operation, DiscoveryCursor? Cursor);

public enum ContinuationIssueStatus
{
    Issued = 0,
    RepeatedCursor = 1,
    CapacityReached = 2,
    JourneyExpired = 3,

    /// <summary>The operation was begun by a different journey. Nothing is issued and nothing is charged.</summary>
    ForeignOperation = 4
}

public sealed record ContinuationIssue(ContinuationIssueStatus Status, string? Token);

/// <summary>
/// One related set of discovery operations for one owner: the shared ceilings, the absolute expiry, and the
/// owner-and-operation-bound continuations. Host-memory only and single-process; it makes no distributed accounting claim.
/// </summary>
public sealed class DiscoveryJourney
{
    private readonly Lock gate = new();
    private readonly TimeProvider clock;
    private readonly Dictionary<string, (DiscoveryOperationBudget Operation, DiscoveryCursor Cursor)> continuations = new(StringComparer.Ordinal);
    private readonly List<DiscoveryOperationBudget> operations = [];
    private int attempts;
    private TimeSpan work = TimeSpan.Zero;
    private DiscoveryOperationBudget? workingOperation;
    private long workStarted;

    public DiscoveryJourney(Guid ownerId, DiscoveryLimits limits, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(clock);
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("A journey needs an owner.", nameof(ownerId));
        }

        OwnerId = ownerId;
        Limits = limits;
        this.clock = clock;
        JourneyId = Guid.NewGuid();
        ExpiresAtUtc = clock.GetUtcNow() + limits.Lifetime;
    }

    public Guid OwnerId { get; }

    public Guid JourneyId { get; }

    public DiscoveryLimits Limits { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public bool IsExpired => clock.GetUtcNow() >= ExpiresAtUtc;

    public DiscoveryUsage Usage
    {
        get
        {
            lock (gate)
            {
                return new DiscoveryUsage(
                    attempts,
                    operations.Sum(operation => operation.RetryCount),
                    operations.Sum(operation => operation.PageCount),
                    operations.Sum(operation => operation.ItemCount),
                    operations.Sum(operation => operation.CacheHitCount),
                    work + LiveWork(null));
            }
        }
    }

    /// <summary>Starts one logical operation, or returns null when the journey has expired or holds its maximum operations.</summary>
    public DiscoveryOperationBudget? BeginOperation(
        DiscoveryOperation kind, string providerId, SourceRef? subject, string? ordering = null, string? filter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        lock (gate)
        {
            if (IsExpired || operations.Count >= Limits.MaxOperationsPerJourney)
            {
                return null;
            }

            var operation = new DiscoveryOperationBudget(this, kind, providerId, subject, ordering, filter);
            operations.Add(operation);
            return operation;
        }
    }

    /// <summary>
    /// Wraps a provider cursor in an opaque token bound to this owner, journey and logical operation (provider, subject,
    /// filter, ordering). The operation must have been begun by this very journey: a token minted here for another journey's
    /// operation would let this journey's owner redeem that journey's scope and budget. A cursor an operation already used is
    /// refused, so a provider that keeps returning the same page cannot loop.
    /// </summary>
    public ContinuationIssue IssueContinuation(DiscoveryOperationBudget operation, DiscoveryCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(cursor);
        lock (gate)
        {
            if (!operations.Contains(operation))
            {
                return new ContinuationIssue(ContinuationIssueStatus.ForeignOperation, null);
            }

            if (IsExpired)
            {
                return new ContinuationIssue(ContinuationIssueStatus.JourneyExpired, null);
            }

            if (!operation.SeenCursors.Add(cursor.Value))
            {
                return new ContinuationIssue(ContinuationIssueStatus.RepeatedCursor, null);
            }

            if (continuations.Count >= Limits.MaxContinuationsPerJourney)
            {
                return new ContinuationIssue(ContinuationIssueStatus.CapacityReached, null);
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            continuations[token] = (operation, cursor);
            return new ContinuationIssue(ContinuationIssueStatus.Issued, token);
        }
    }

    /// <summary>
    /// Resolves a continuation for the operation the caller expects. A token is not consumed: a replay is another request
    /// and spends the same shared allowance as any other.
    /// </summary>
    public ContinuationRedemption Redeem(
        string? token,
        Guid callerOwnerId,
        DiscoveryOperation expectedKind,
        string expectedProviderId,
        SourceRef? expectedSubject,
        string? expectedOrdering = null,
        string? expectedFilter = null)
    {
        lock (gate)
        {
            if (callerOwnerId != OwnerId)
            {
                return Rejected(ContinuationStatus.Foreign);
            }

            if (token is null || !continuations.TryGetValue(token, out var entry))
            {
                return Rejected(ContinuationStatus.Unknown);
            }

            if (IsExpired)
            {
                return Rejected(ContinuationStatus.Expired);
            }

            var operation = entry.Operation;
            if (operation.Kind != expectedKind)
            {
                return Rejected(ContinuationStatus.WrongKind);
            }

            if (!string.Equals(operation.ProviderId, expectedProviderId, StringComparison.Ordinal) ||
                operation.Subject != expectedSubject ||
                !string.Equals(operation.Filter, expectedFilter, StringComparison.Ordinal) ||
                !string.Equals(operation.Ordering, expectedOrdering, StringComparison.Ordinal))
            {
                return Rejected(ContinuationStatus.CrossOperation);
            }

            return new ContinuationRedemption(ContinuationStatus.Accepted, operation, entry.Cursor);
        }

        static ContinuationRedemption Rejected(ContinuationStatus status) => new(status, null, null);
    }

    // ---- called by DiscoveryOperationBudget under no lock of its own; every method here takes the journey lock ----

    internal AttemptAdmission AdmitAttempt(DiscoveryOperationBudget operation, bool isRetry)
    {
        lock (gate)
        {
            if (IsExpired)
            {
                return AttemptAdmission.JourneyExpired;
            }

            if (RemainingFor(operation) <= TimeSpan.Zero)
            {
                return AttemptAdmission.DeadlineExceeded;
            }

            if (isRetry && operation.RetryCount >= Limits.OperationMaxRetries)
            {
                return AttemptAdmission.RetryNotAllowed;
            }

            if (operation.AttemptCount >= Limits.OperationMaxAttempts)
            {
                return AttemptAdmission.OperationAttemptsExhausted;
            }

            if (attempts >= Limits.JourneyMaxAttempts)
            {
                return AttemptAdmission.JourneyAttemptsExhausted;
            }

            attempts++;
            operation.AttemptCount++;
            if (isRetry)
            {
                operation.RetryCount++;
            }

            return AttemptAdmission.Admitted;
        }
    }

    internal bool AdmitPage(DiscoveryOperationBudget operation)
    {
        lock (gate)
        {
            if (IsExpired || operation.PageCount >= Limits.OperationMaxPages)
            {
                return false;
            }

            operation.PageCount++;
            return true;
        }
    }

    internal void AddItems(DiscoveryOperationBudget operation, int count)
    {
        lock (gate)
        {
            operation.ItemCount += Math.Max(0, count);
        }
    }

    internal void AddCacheHit(DiscoveryOperationBudget operation)
    {
        lock (gate)
        {
            operation.CacheHitCount++;
        }
    }

    internal TimeSpan Remaining(DiscoveryOperationBudget operation)
    {
        lock (gate)
        {
            return RemainingFor(operation);
        }
    }

    internal DiscoveryUsage UsageOf(DiscoveryOperationBudget operation)
    {
        lock (gate)
        {
            return new DiscoveryUsage(
                operation.AttemptCount,
                operation.RetryCount,
                operation.PageCount,
                operation.ItemCount,
                operation.CacheHitCount,
                operation.Work + LiveWork(operation));
        }
    }

    internal IDisposable StartWork(DiscoveryOperationBudget operation)
    {
        lock (gate)
        {
            if (workingOperation is not null)
            {
                throw new InvalidOperationException("Only one provider-work scope may run per journey at a time.");
            }

            workingOperation = operation;
            workStarted = clock.GetTimestamp();
            return new WorkScope(this, operation);
        }
    }

    private void EndWork(DiscoveryOperationBudget operation)
    {
        lock (gate)
        {
            if (!ReferenceEquals(workingOperation, operation))
            {
                return;
            }

            var elapsed = clock.GetElapsedTime(workStarted);
            work += elapsed;
            operation.Work += elapsed;
            workingOperation = null;
        }
    }

    private TimeSpan LiveWork(DiscoveryOperationBudget? operation) =>
        workingOperation is not null && (operation is null || ReferenceEquals(workingOperation, operation))
            ? clock.GetElapsedTime(workStarted)
            : TimeSpan.Zero;

    private TimeSpan RemainingFor(DiscoveryOperationBudget operation)
    {
        var live = workingOperation is null ? TimeSpan.Zero : clock.GetElapsedTime(workStarted);
        var operationLive = ReferenceEquals(workingOperation, operation) ? live : TimeSpan.Zero;
        var operationLeft = Limits.OperationMaxWork - operation.Work - operationLive;
        var journeyLeft = Limits.JourneyMaxWork - work - live;
        var left = operationLeft < journeyLeft ? operationLeft : journeyLeft;
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    private sealed class WorkScope(DiscoveryJourney journey, DiscoveryOperationBudget operation) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                journey.EndWork(operation);
            }
        }
    }
}

/// <summary>One logical operation's share of its journey. Counters live under the journey's lock.</summary>
public sealed class DiscoveryOperationBudget : IDiscoveryBudget
{
    private readonly DiscoveryJourney journey;

    internal DiscoveryOperationBudget(
        DiscoveryJourney journey, DiscoveryOperation kind, string providerId, SourceRef? subject, string? ordering, string? filter)
    {
        this.journey = journey;
        Kind = kind;
        ProviderId = providerId;
        Subject = subject;
        Ordering = ordering;
        Filter = filter;
        OperationId = Guid.NewGuid();
    }

    public Guid OperationId { get; }

    public DiscoveryOperation Kind { get; }

    public string ProviderId { get; }

    public SourceRef? Subject { get; }

    public string? Ordering { get; }

    /// <summary>The approved filter of this operation's scope (see <see cref="DiscoveryScope.Filter"/>); part of what a continuation is bound to.</summary>
    public string? Filter { get; }

    public TimeSpan RemainingWork => journey.Remaining(this);

    public DiscoveryUsage Usage => journey.UsageOf(this);

    internal int AttemptCount { get; set; }

    internal int RetryCount { get; set; }

    internal int PageCount { get; set; }

    internal int ItemCount { get; set; }

    internal int CacheHitCount { get; set; }

    internal TimeSpan Work { get; set; }

    internal HashSet<string> SeenCursors { get; } = new(StringComparer.Ordinal);

    public AttemptAdmission TryAdmitAttempt(bool isRetry) => journey.AdmitAttempt(this, isRetry);

    public bool TryAdmitPage() => journey.AdmitPage(this);

    public void RecordItems(int count) => journey.AddItems(this, count);

    public void RecordCacheHit() => journey.AddCacheHit(this);

    public IDisposable BeginWork() => journey.StartWork(this);
}
