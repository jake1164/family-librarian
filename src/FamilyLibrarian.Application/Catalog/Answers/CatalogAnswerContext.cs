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

    public int MaxEntriesPerRoot { get; init; } = 200;

    /// <summary>Exact budget for <see cref="CatalogAnswerContextStore.Measure(CatalogRetainedWindow)"/> of one retained window.</summary>
    public int MaxBytesPerRoot { get; init; } = 256 * 1024;
}

/// <summary>What a root retains between turns: the selection and the bounded entries that follow-ups are answered from.</summary>
/// <param name="Subject">The selected subject; null while the window holds only choices still to be made.</param>
/// <param name="SourceGaps">A consulted source failed, was unavailable or unsupported while the window was built, so it never proves absence.</param>
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
    DateTimeOffset ObservedAtUtc);

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
    private const int FixedOverheadBytes = 48;

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

    /// <summary>
    /// The exact retained-text cost of a window: the UTF-8 bytes of every string it holds (titles, labels, ids, roles, languages and
    /// codes) plus a fixed overhead per record, credit, position, choice and series. Not a serialized size; a stable, testable bound.
    /// </summary>
    public static int Measure(CatalogRetainedWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        long total = FixedOverheadBytes;
        total += Text(window.ScopeCode) + Text(window.ProviderId) + Text(window.ContinuationToken);
        if (window.Subject is { } subject) total += Text(subject.Label) + SubjectBytes(subject);
        foreach (var source in window.Sources) total += FixedOverheadBytes + Text(source.ProviderId) + Text(source.ReasonCode);
        foreach (var entry in window.Entries) total += MeasureEntry(entry);
        foreach (var series in window.Series)
        {
            total += FixedOverheadBytes + Text(series.Label) + Text(series.CanonicalState) + SourceBytes(series.Id) + (series.SavedId is null ? 0 : 16);
            total += Text(series.Relationship.DerivedScope);
        }

        foreach (var choice in window.Choices)
        {
            total += FixedOverheadBytes + Text(choice.Disambiguation) + Text(choice.ProviderId) + Text(choice.EvidenceNote) + Text(choice.Reference.Label) + SubjectBytes(choice.Reference);
        }

        return (int)Math.Min(total, int.MaxValue);
    }

    private static long MeasureEntry(CatalogAnswerEntry entry)
    {
        long total = FixedOverheadBytes + Text(entry.Title) + Text(entry.Language) + Text(entry.Format) + Text(entry.ProviderId) + Text(entry.SeriesLabel) + SourceBytes(entry.Source);
        total += entry.SavedWorkId is null ? 0 : 16;
        foreach (var credit in entry.Credits)
        {
            total += FixedOverheadBytes + Text(credit.Label) + Text(credit.Role) + SourceBytes(credit.AuthorSource) + (credit.SavedAuthorId is null ? 0 : 16);
        }

        foreach (var position in entry.Positions)
        {
            total += FixedOverheadBytes + Text(position.Label) + SourceBytes(position.Series);
        }

        return total;
    }

    private static long SubjectBytes(CatalogSubjectRef subject) => SourceBytes(subject.Native) + (subject.SavedId is null ? 0 : 16);

    private static long SourceBytes(SourceRef? source) => source is null ? 0 : Text(source.ProviderId) + Text(source.ExternalId);

    private static long Text(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    private CatalogRetainedWindow? Fit(CatalogRetainedWindow window)
    {
        var cap = Math.Min(window.Entries.Count, Math.Max(0, limits.MaxEntriesPerRoot));
        var truncated = window.Truncated || cap < window.Entries.Count;
        CatalogRetainedWindow Build(int count, bool cut) => window with
        {
            Entries = window.Entries.Take(count).ToArray(),
            Truncated = cut,
            Exhausted = window.Exhausted && !cut,
            MoreAvailable = window.MoreAvailable && !cut,
            ContinuationToken = cut ? null : window.ContinuationToken
        };

        var whole = Build(cap, truncated);
        if (Measure(whole) <= limits.MaxBytesPerRoot) return whole;
        if (Measure(Build(0, true)) > limits.MaxBytesPerRoot) return null;

        // The largest prefix that fits. Cost only grows with each entry, so a binary search finds it exactly.
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
