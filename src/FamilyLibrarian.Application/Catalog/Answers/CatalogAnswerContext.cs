using System.Buffers;
using System.Text;
using System.Text.Json;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Answers;

public enum CatalogAnswerTransport
{
    Web = 1,
    Matrix = 2
}

/// <summary>
/// Who is asking, and where. A reference or continuation is bound to all three, so a number or token from another user, another
/// transport, another web session or another Matrix room can never select anything here. The scope key is a session id or a room id,
/// held only on the host; it is never put in a reference a client holds.
/// </summary>
public sealed record CatalogAnswerCaller
{
    public const int MaxScopeKeyLength = 256;

    public CatalogAnswerCaller(Guid ownerId, CatalogAnswerTransport transport, string scopeKey)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An answer needs an authenticated owner.", nameof(ownerId));
        }

        if (!Enum.IsDefined(transport))
        {
            throw new ArgumentOutOfRangeException(nameof(transport));
        }

        if (string.IsNullOrWhiteSpace(scopeKey) || scopeKey.Length > MaxScopeKeyLength)
        {
            throw new ArgumentException("A scope key (session or room) is required and is never truncated.", nameof(scopeKey));
        }

        OwnerId = ownerId;
        Transport = transport;
        ScopeKey = scopeKey;
    }

    public Guid OwnerId { get; }

    public CatalogAnswerTransport Transport { get; }

    public string ScopeKey { get; }
}

/// <summary>
/// The compact, versioned selection a conversation keeps between turns: the root of the retained context, the operation, the chosen
/// subject and anchor, and the ordering. It holds identifiers only (never evidence), is rejected rather than truncated when it does
/// not fit its bound, and an unknown version or malformed text is simply "no context" (the caller recovers by asking the user).
/// </summary>
public sealed record CatalogAnswerContextRef(
    Guid RootId,
    CatalogAnswerOperation Operation,
    CatalogSubjectRef? Subject,
    CatalogSubjectRef? Anchor,
    SeriesOrderingKind Order)
{
    public const int CurrentVersion = 1;

    /// <summary>Far below the 4,000-character session state, so a reference never crowds out the rest of the conversation.</summary>
    public const int MaxJsonLength = 1_600;

    /// <summary>The reference as JSON, or null when it would exceed <see cref="MaxJsonLength"/> (never cut: a cut id names a different thing).</summary>
    public string? ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", CurrentVersion);
            writer.WriteString("r", RootId.ToString("N"));
            writer.WriteNumber("o", (int)Operation);
            writer.WriteNumber("d", (int)Order);
            if (Subject is not null) WriteSubject(writer, "s", Subject);
            if (Anchor is not null) WriteSubject(writer, "a", Anchor);
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return json.Length <= MaxJsonLength ? json : null;
    }

    public static bool TryParse(string? json, out CatalogAnswerContextRef? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != CurrentVersion ||
                !root.TryGetProperty("r", out var rootId) || !Guid.TryParseExact(rootId.GetString(), "N", out var id) || id == Guid.Empty ||
                !root.TryGetProperty("o", out var operation) || !operation.TryGetInt32(out var operationValue) || !Enum.IsDefined((CatalogAnswerOperation)operationValue) ||
                !root.TryGetProperty("d", out var order) || !order.TryGetInt32(out var orderValue) || !Enum.IsDefined((SeriesOrderingKind)orderValue))
            {
                return false;
            }

            CatalogSubjectRef? subject = null;
            CatalogSubjectRef? anchor = null;
            if (root.TryGetProperty("s", out var subjectElement) && !TryReadSubject(subjectElement, (SeriesOrderingKind)orderValue, out subject)) return false;
            if (root.TryGetProperty("a", out var anchorElement) && !TryReadSubject(anchorElement, SeriesOrderingKind.Unspecified, out anchor)) return false;
            value = new CatalogAnswerContextRef(id, (CatalogAnswerOperation)operationValue, subject, anchor, (SeriesOrderingKind)orderValue);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            // Anything unreadable is "no context": the caller asks the user to restart, it never guesses.
            return false;
        }
    }

    private static void WriteSubject(Utf8JsonWriter writer, string name, CatalogSubjectRef subject)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("k", (int)subject.Kind);
        writer.WriteString("l", subject.Label);
        if (subject.SavedId is { } saved)
        {
            writer.WriteString("g", saved.ToString("N"));
        }

        if (subject.Native is { } native)
        {
            writer.WriteString("p", native.ProviderId);
            writer.WriteString("i", native.ExternalId);
            writer.WriteNumber("t", (int)native.Kind);
        }

        writer.WriteEndObject();
    }

    private static bool TryReadSubject(JsonElement element, SeriesOrderingKind order, out CatalogSubjectRef? subject)
    {
        subject = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("k", out var kind) || !kind.TryGetInt32(out var kindValue) || !Enum.IsDefined((CatalogSubjectKind)kindValue) ||
            !element.TryGetProperty("l", out var labelElement) || labelElement.GetString() is not { } label)
        {
            return false;
        }

        if ((CatalogSubjectKind)kindValue == CatalogSubjectKind.Title)
        {
            if (element.TryGetProperty("g", out _) || element.TryGetProperty("p", out _)) return false;
            subject = CatalogSubjectRef.Title(label);
            return true;
        }

        if (element.TryGetProperty("g", out var saved))
        {
            if (!Guid.TryParseExact(saved.GetString(), "N", out var id) || id == Guid.Empty || element.TryGetProperty("p", out _)) return false;
            subject = CatalogSubjectRef.Saved((CatalogSubjectKind)kindValue, label, id, order);
            return true;
        }

        if (element.TryGetProperty("p", out var provider) && element.TryGetProperty("i", out var external) &&
            element.TryGetProperty("t", out var nativeKind) && nativeKind.TryGetInt32(out var nativeKindValue) && Enum.IsDefined((ExternalReferenceEntityType)nativeKindValue) &&
            SourceRef.TryCreate(provider.GetString(), external.GetString(), (ExternalReferenceEntityType)nativeKindValue, out var reference))
        {
            subject = CatalogSubjectRef.FromSource((CatalogSubjectKind)kindValue, label, reference!, order);
            return true;
        }

        return false;
    }
}

/// <summary>Bounds for retained answer contexts. Defaults follow the Phase 3/4 ceilings: absolute 10-minute lifetime, never extended.</summary>
public sealed record CatalogAnswerContextLimits
{
    public static CatalogAnswerContextLimits Default { get; } = new();

    /// <summary>Absolute lifetime of a root from creation. A conversation that stays active longer does not extend it.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxRootsPerOwner { get; init; } = 10;

    public int MaxRootsTotal { get; init; } = 100;

    /// <summary>The bound on every retained record of one window together: books, series and choices count against one total.</summary>
    public int MaxRecordsPerRoot { get; init; } = 200;

    /// <summary>
    /// Exact budget, in UTF-8 bytes, of the canonical serialized form of one whole retained window (see
    /// <see cref="CatalogAnswerContextStore.Measure(CatalogRetainedWindow)"/>).
    /// </summary>
    public int MaxBytesPerRoot { get; init; } = 256 * 1024;
}

/// <summary>
/// What a retained window actually read. A follow-up is compatible with it only when the answer it asks for is derived from this
/// same read: an author's works page says nothing about the author's series, and neither says anything about a series' members.
/// </summary>
public enum CatalogReadKind
{
    /// <summary>The members of one selected series (a page, or the saved list).</summary>
    SeriesMembers = 1,

    /// <summary>The works of one selected author (a page, or the saved list).</summary>
    AuthorWorks = 2,

    /// <summary>The series reached from one selected author.</summary>
    AuthorSeries = 3,

    /// <summary>One ordinary title search.</summary>
    TitleSearch = 4,

    /// <summary>Only choices still to be made; nothing was read for a selected subject.</summary>
    Choices = 5
}

/// <summary>What a root retains between turns: the selection and the bounded entries that follow-ups are answered from.</summary>
/// <param name="Subject">The selected subject; null while the window holds only choices still to be made.</param>
/// <param name="SourceGaps">A consulted source failed, was unavailable or unsupported while the window was built, so it never proves absence.</param>
/// <param name="ReadKind">What the window actually read; a follow-up needing another read is not answered from it.</param>
public sealed record CatalogRetainedWindow(
    CatalogAnswerOperation Operation,
    CatalogSubjectRef? Subject,
    SeriesOrderingKind Order,
    IReadOnlyList<CatalogAnswerEntry> Entries,
    IReadOnlyList<CatalogAnswerSeries> Series,
    IReadOnlyList<CatalogAnswerChoice> Choices,
    bool Exhausted,
    bool MoreAvailable,
    bool Truncated,
    bool SourceGaps,
    IReadOnlyList<CatalogSourceReport> Sources,
    string ScopeCode,
    string? ProviderId,
    string? ContinuationToken,
    DateTimeOffset ObservedAtUtc,
    CatalogReadKind ReadKind)
{
    // Private snapshots (see CatalogAnswerGroup): what the store holds cannot be changed through the lists a caller supplied, nor
    // through the collections it returns.
    public IReadOnlyList<CatalogAnswerEntry> Entries { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of(Entries);

    public IReadOnlyList<CatalogAnswerSeries> Series { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of(Series);

    public IReadOnlyList<CatalogAnswerChoice> Choices { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of(Choices);

    public IReadOnlyList<CatalogSourceReport> Sources { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of(Sources);
}

public sealed record CatalogAnswerRootLease(Guid RootId, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Host-only, bounded, in-memory store of what a caller's own answers retained, so a short follow-up ("what comes next?") is answered
/// from the retained entries with no new provider call. Operational state: never persisted, never shared, lost on restart (which
/// then asks the user to restart rather than silently fetching again or choosing a namesake).
/// </summary>
/// <remarks>
/// Every read is bound to the caller's user, transport and scope; another caller's root is indistinguishable from an unknown or
/// expired one. A root's lifetime is absolute. The store is a singleton, so it holds no scoped service: the current time is passed in.
/// </remarks>
public sealed class CatalogAnswerContextStore
{

    private readonly Lock gate = new();
    private readonly CatalogAnswerContextLimits limits;
    private readonly Dictionary<Guid, Root> roots = [];
    private long sequence;

    public CatalogAnswerContextStore() : this(CatalogAnswerContextLimits.Default)
    {
    }

    public CatalogAnswerContextStore(CatalogAnswerContextLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        this.limits = limits;
    }

    public CatalogAnswerContextLimits Limits => limits;

    public int RootCount
    {
        get
        {
            lock (gate) return roots.Count;
        }
    }

    /// <summary>Starts a root for the caller, evicting the oldest within the owner and then globally. Null when no root can be held at all.</summary>
    public CatalogAnswerRootLease? CreateRoot(CatalogAnswerCaller caller, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (limits.MaxRootsPerOwner <= 0 || limits.MaxRootsTotal <= 0) return null;
        lock (gate)
        {
            PruneExpired(now);
            while (roots.Values.Count(root => root.OwnerId == caller.OwnerId) >= limits.MaxRootsPerOwner) roots.Remove(Oldest(caller.OwnerId).Id);
            while (roots.Count >= limits.MaxRootsTotal) roots.Remove(Oldest(null).Id);
            var root = new Root(Guid.NewGuid(), caller.OwnerId, caller.Transport, caller.ScopeKey, now + limits.Lifetime, ++sequence);
            roots[root.Id] = root;
            return new CatalogAnswerRootLease(root.Id, root.ExpiresAtUtc);
        }
    }

    /// <summary>The root's lease, only for the exact caller (user, transport and scope) and only while unexpired.</summary>
    public CatalogAnswerRootLease? TryGetRoot(CatalogAnswerCaller caller, Guid rootId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(caller);
        lock (gate)
        {
            return Find(caller, rootId, now) is { } root ? new CatalogAnswerRootLease(root.Id, root.ExpiresAtUtc) : null;
        }
    }

    public CatalogRetainedWindow? TryGetWindow(CatalogAnswerCaller caller, Guid rootId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(caller);
        lock (gate)
        {
            return Find(caller, rootId, now)?.Window;
        }
    }

    /// <summary>
    /// One option of the window's offered choices, by its position in the list the user was shown, only for the exact caller and an
    /// unexpired root. A number or reference from another room, session, user or stale list selects nothing (null).
    /// </summary>
    public CatalogAnswerChoice? TryGetChoice(CatalogAnswerCaller caller, Guid rootId, int index, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(caller);
        lock (gate)
        {
            return Find(caller, rootId, now)?.Window is { } window && index >= 0 && index < window.Choices.Count ? window.Choices[index] : null;
        }
    }

    /// <summary>
    /// Retains the window for the caller's root, replacing any earlier one. Entries are kept from the front until the exact byte budget
    /// or the entry bound is reached; the kept window then says it is truncated and offers no continuation (following it would skip
    /// what was cut). Returns null when the root is not the caller's or not even an empty window fits.
    /// </summary>
    public CatalogRetainedWindow? Retain(CatalogAnswerCaller caller, Guid rootId, CatalogRetainedWindow window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(window);
        lock (gate)
        {
            if (Find(caller, rootId, now) is not { } root) return null;
            var kept = Fit(window);
            if (kept is null) return null;
            root.Window = kept;
            return kept;
        }
    }

    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The one canonical size of a retained window: the UTF-8 bytes of its complete System.Text.Json serialization with fixed web
    /// options and the default encoder (so every envelope, flag, field name, number, escape sequence and non-ASCII character counts as
    /// it will really occupy). This is the figure <see cref="CatalogAnswerContextLimits.MaxBytesPerRoot"/> bounds, for the final,
    /// possibly cut window, not an estimate of its text.
    /// </summary>
    public static int Measure(CatalogRetainedWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return JsonSerializer.SerializeToUtf8Bytes(window, CanonicalJson).Length;
    }

    /// <summary>
    /// Keeps the largest truthful prefix of the window's records (books, then series, then choices) that is within the one record
    /// bound and whose canonical serialized size is within the byte bound. Any cut sets the truncated flag, clears exhaustion and
    /// the continuation (following it would skip what was cut). Null when even a record-free window does not fit.
    /// </summary>
    private CatalogRetainedWindow? Fit(CatalogRetainedWindow window)
    {
        var total = window.Entries.Count + window.Series.Count + window.Choices.Count;
        var cap = Math.Min(total, Math.Max(0, limits.MaxRecordsPerRoot));
        var truncated = window.Truncated || cap < total;
        CatalogRetainedWindow Build(int count, bool cut)
        {
            var entries = Math.Min(count, window.Entries.Count);
            var series = Math.Min(Math.Max(0, count - entries), window.Series.Count);
            var choices = Math.Min(Math.Max(0, count - entries - series), window.Choices.Count);
            return window with
            {
                Entries = window.Entries.Take(entries).ToArray(),
                Series = window.Series.Take(series).ToArray(),
                Choices = window.Choices.Take(choices).ToArray(),
                Truncated = cut,
                Exhausted = window.Exhausted && !cut,
                MoreAvailable = window.MoreAvailable && !cut,
                ContinuationToken = cut ? null : window.ContinuationToken
            };
        }

        var whole = Build(cap, truncated);
        if (Measure(whole) <= limits.MaxBytesPerRoot) return whole;
        if (Measure(Build(0, true)) > limits.MaxBytesPerRoot) return null;

        // The largest prefix that fits. Size only grows with each record, so a binary search finds it exactly.
        int low = 0, high = cap;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Measure(Build(middle, true)) <= limits.MaxBytesPerRoot) low = middle; else high = middle - 1;
        }

        return Build(low, true);
    }

    private Root? Find(CatalogAnswerCaller caller, Guid rootId, DateTimeOffset now)
    {
        if (!roots.TryGetValue(rootId, out var root)) return null;
        if (now >= root.ExpiresAtUtc)
        {
            roots.Remove(root.Id);
            return null;
        }

        // A root is never reachable by another user, another transport, or another session/room: all look like "unknown".
        return root.OwnerId == caller.OwnerId && root.Transport == caller.Transport &&
               string.Equals(root.ScopeKey, caller.ScopeKey, StringComparison.Ordinal)
            ? root
            : null;
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var expired in roots.Values.Where(root => now >= root.ExpiresAtUtc).ToArray()) roots.Remove(expired.Id);
    }

    private Root Oldest(Guid? ownerId) =>
        roots.Values.Where(root => ownerId is null || root.OwnerId == ownerId).MinBy(root => root.Sequence)!;

    private sealed class Root(Guid id, Guid ownerId, CatalogAnswerTransport transport, string scopeKey, DateTimeOffset expiresAtUtc, long sequence)
    {
        public Guid Id { get; } = id;

        public Guid OwnerId { get; } = ownerId;

        public CatalogAnswerTransport Transport { get; } = transport;

        public string ScopeKey { get; } = scopeKey;

        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

        public long Sequence { get; } = sequence;

        public CatalogRetainedWindow? Window { get; set; }
    }
}
