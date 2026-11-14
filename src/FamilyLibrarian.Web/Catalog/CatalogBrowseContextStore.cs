using System.Text.Json;
using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Web.Catalog;

/// <summary>Bounds for the temporary browse cache. Defaults are the reviewed Phase 1 limits, not coverage promises.</summary>
public sealed record CatalogBrowseContextLimits
{
    public static CatalogBrowseContextLimits Default { get; } = new();

    /// <summary>Absolute lifetime from creation. Never extended by reads or later appends.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxRecordsPerContext { get; init; } = 200;

    public int MaxCreditsPerRecord { get; init; } = 20;

    public int MaxMembershipsPerRecord { get; init; } = 20;

    public int MaxFormatsPerRecord { get; init; } = 10;

    /// <summary>Budget for the canonical serialized byte size of one context's complete retained data.</summary>
    public int MaxBytesPerContext { get; init; } = 256 * 1024;

    public int MaxContextsPerOwner { get; init; } = 10;

    public int MaxContextsTotal { get; init; } = 100;

    public int MaxLabelLength { get; init; } = 200;

    public int MaxTitleLength { get; init; } = 300;

    public int MaxFormatLength { get; init; } = 64;

    public int MaxVersionKindLength { get; init; } = 64;

    public int MaxVersionLabelLength { get; init; } = 128;

    public int MaxPositionLabelLength { get; init; } = 64;

    public int MaxProviderIdLength { get; init; } = 128;

    public int MaxProviderNameLength { get; init; } = 128;

    public int MaxExternalIdLength { get; init; } = 512;
}

/// <summary>One provider's raw result as a search saw it. Only what a context may retain is read from it.</summary>
public sealed record BrowseObservation(
    string ProviderId,
    string ProviderName,
    bool Succeeded,
    IReadOnlyList<BookCandidate> Candidates);

public sealed record BrowseContextLease(Guid ContextId, DateTimeOffset ExpiresAtUtc);

public sealed record BrowseRecordLookup(
    ObservedBookRecord Record,
    DateTimeOffset ExpiresAtUtc,
    bool ContextTruncated);

public sealed record BrowseStoreStatistics(
    int Contexts,
    int OwnerIndexEntries,
    int ScopeIndexEntries,
    int SubjectIndexEntries);

/// <summary>
/// Host-only, bounded, in-memory store of what a user's own searches and detail views already observed, so author and
/// series previews can be browsed without a provider call and after the search run is gone. It is operational state:
/// never persisted, never an EF entity, never shared between users, lost on restart.
/// </summary>
/// <remarks>
/// <para>
/// One context exists per <c>(owner, scope)</c>; a progressive search's scope is its run id, so repeated polling reuses
/// one context instead of allocating per tick. Newly observed records are <em>appended</em>; a record, and the
/// subject ids issued for its labels, are immutable once stored.
/// </para>
/// <para>
/// A subject is a temporary reference to a <em>source observation</em> (kind, provider, external book id, relationship
/// ordinal and the exact full label), never a person or series identity. Labels longer than the bound are not stored at
/// all (and the record is flagged) rather than truncated and then matched as if equal.
/// </para>
/// <para>
/// The byte budget is enforced against the canonical serialized form of <see cref="BrowseContextSnapshot"/>: the
/// retained records, their subjects, the failed-provider set and the truncation flag, serialized with fixed compact
/// options that keep default JSON escaping. Each admission adds the real serialized bytes of just the new record and
/// subjects (plus one separator byte each, an upper bound), so ordinary polling never allocates the whole context.
/// </para>
/// <para>
/// The store is a singleton, so it holds no scoped service: callers pass the current time on every call. One lock
/// guards every index (contexts, scope lookup, owner lists), so the indexes cannot drift apart and are bounded by the
/// context limits. Expiry is absolute and is enforced on reads as well as inserts.
/// </para>
/// </remarks>
public sealed class CatalogBrowseContextStore
{
    /// <summary>Compact, fixed options: default web naming and default escaping, no indentation.</summary>
    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web);

    /// <summary>Exact serialized bytes of an empty snapshot; every array element is then charged its own bytes plus one separator.</summary>
    private static readonly int SnapshotEnvelopeBytes = Measure(new BrowseContextSnapshot([], [], [], false));

    private readonly Lock gate = new();
    private readonly CatalogBrowseContextLimits limits;
    private readonly Dictionary<Guid, Context> contexts = [];
    private readonly Dictionary<(Guid Owner, string Scope), Guid> byScope = [];
    private readonly Dictionary<Guid, List<Guid>> byOwner = [];
    private long sequence;

    public CatalogBrowseContextStore() : this(CatalogBrowseContextLimits.Default)
    {
    }

    public CatalogBrowseContextStore(CatalogBrowseContextLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        this.limits = limits;
    }

    public CatalogBrowseContextLimits Limits => limits;

    /// <summary>
    /// Finds or creates the context for <paramref name="scopeKey"/> and appends the observations. Returns null when the
    /// cache cannot admit a context at all; the caller then simply issues no navigation.
    /// </summary>
    public BrowseContextLease? Observe(
        Guid ownerId, string scopeKey, IEnumerable<BrowseObservation> observations, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(scopeKey);
        ArgumentNullException.ThrowIfNull(observations);
        if (ownerId == Guid.Empty || limits.MaxContextsTotal <= 0 || limits.MaxContextsPerOwner <= 0) return null;

        lock (gate)
        {
            PruneExpired(now);
            var context = FindByScope(ownerId, scopeKey) ?? Create(ownerId, scopeKey, now);
            foreach (var observation in observations) Append(context, observation, now);
            return new BrowseContextLease(context.Id, context.ExpiresAtUtc);
        }
    }

    /// <summary>The stored observation for one provider record, if this owner's unexpired context holds it.</summary>
    public BrowseRecordLookup? TryGetRecord(
        Guid ownerId, Guid contextId, string providerId, string externalId, DateTimeOffset now)
    {
        lock (gate)
        {
            var context = FindForRead(ownerId, contextId, now);
            if (context is null || !context.RecordIndex.TryGetValue((providerId, externalId), out var index)) return null;
            return new BrowseRecordLookup(context.Records[index], context.ExpiresAtUtc, context.Truncated);
        }
    }

    /// <summary>
    /// A consistent copy of one subject and its provider's observed records, or null when the context is unknown,
    /// expired, evicted, another owner's, or the subject is not of the requested kind. Callers must not distinguish
    /// those cases.
    /// </summary>
    public ObservedSample? TryGetSample(
        Guid ownerId, Guid contextId, ObservedSubjectKind kind, Guid subjectId, DateTimeOffset now)
    {
        lock (gate)
        {
            var context = FindForRead(ownerId, contextId, now);
            if (context is null || !context.SubjectsById.TryGetValue(subjectId, out var subject) || subject.Kind != kind) return null;
            var records = context.Records
                .Where(record => string.Equals(record.ProviderId, subject.ProviderId, StringComparison.Ordinal))
                .ToArray();
            return new ObservedSample(
                subject, records, context.FailedProviders.Contains(subject.ProviderId), context.Truncated, context.ExpiresAtUtc);
        }
    }

    /// <summary>
    /// A consistent copy of everything one context retains, in a stable order, or null on the same conditions as
    /// <see cref="TryGetSample"/>. This is the canonical payload the byte budget bounds.
    /// </summary>
    public BrowseContextSnapshot? TryGetSnapshot(Guid ownerId, Guid contextId, DateTimeOffset now)
    {
        lock (gate)
        {
            var context = FindForRead(ownerId, contextId, now);
            if (context is null) return null;
            return new BrowseContextSnapshot(
                context.Records.ToArray(),
                context.SubjectsById.Values.OrderBy(subject => subject.SubjectId).ToArray(),
                context.FailedProviders.OrderBy(provider => provider, StringComparer.Ordinal).ToArray(),
                context.Truncated);
        }
    }

    public BrowseStoreStatistics Statistics()
    {
        lock (gate)
        {
            return new BrowseStoreStatistics(
                contexts.Count,
                byOwner.Values.Sum(list => list.Count),
                byScope.Count,
                contexts.Values.Sum(context => context.SubjectsById.Count));
        }
    }

    public int ContextCountFor(Guid ownerId)
    {
        lock (gate) return byOwner.TryGetValue(ownerId, out var list) ? list.Count : 0;
    }

    // ---- everything below runs under the lock ----

    private Context? FindByScope(Guid ownerId, string scopeKey) =>
        byScope.TryGetValue((ownerId, scopeKey), out var id) && contexts.TryGetValue(id, out var context) ? context : null;

    private Context? FindForRead(Guid ownerId, Guid contextId, DateTimeOffset now)
    {
        if (!contexts.TryGetValue(contextId, out var context)) return null;
        if (now >= context.ExpiresAtUtc)
        {
            Remove(context);
            return null;
        }

        return context.OwnerId == ownerId ? context : null;
    }

    private Context Create(Guid ownerId, string scopeKey, DateTimeOffset now)
    {
        // Oldest first, within the owner and then globally, so neither bound can be exceeded.
        if (byOwner.TryGetValue(ownerId, out var owned))
        {
            while (owned.Count >= limits.MaxContextsPerOwner) Remove(contexts[owned.OrderBy(id => contexts[id].Sequence).First()]);
        }

        while (contexts.Count >= limits.MaxContextsTotal) Remove(contexts.Values.MinBy(context => context.Sequence)!);

        var context = new Context(Guid.NewGuid(), ownerId, scopeKey, now + limits.Lifetime, ++sequence)
        {
            Bytes = SnapshotEnvelopeBytes
        };
        contexts[context.Id] = context;
        byScope[(ownerId, scopeKey)] = context.Id;
        if (!byOwner.TryGetValue(ownerId, out owned)) byOwner[ownerId] = owned = [];
        owned.Add(context.Id);
        return context;
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var expired in contexts.Values.Where(context => now >= context.ExpiresAtUtc).ToArray()) Remove(expired);
    }

    private void Remove(Context context)
    {
        contexts.Remove(context.Id);
        if (byScope.TryGetValue((context.OwnerId, context.ScopeKey), out var id) && id == context.Id)
            byScope.Remove((context.OwnerId, context.ScopeKey));
        if (byOwner.TryGetValue(context.OwnerId, out var owned))
        {
            owned.Remove(context.Id);
            if (owned.Count == 0) byOwner.Remove(context.OwnerId);
        }
    }

    private void Append(Context context, BrowseObservation observation, DateTimeOffset now)
    {
        if (!IsStorableId(observation.ProviderId, limits.MaxProviderIdLength))
        {
            context.Truncated = true;
            return;
        }

        // Mirror the search run: the latest result for a provider says whether it currently has failed. The failure
        // set is part of the serialized payload, so it is charged against the same budget it lives in.
        if (observation.Succeeded)
        {
            if (context.FailedProviders.Remove(observation.ProviderId)) context.Bytes -= Measure(observation.ProviderId) + 1;
        }
        else if (!context.FailedProviders.Contains(observation.ProviderId))
        {
            var cost = Measure(observation.ProviderId) + 1;
            if (context.Bytes + cost > limits.MaxBytesPerContext)
            {
                context.Truncated = true;
            }
            else
            {
                context.FailedProviders.Add(observation.ProviderId);
                context.Bytes += cost;
            }
        }

        foreach (var candidate in observation.Candidates)
        {
            if (!string.Equals(candidate.ProviderId, observation.ProviderId, StringComparison.Ordinal)) continue;
            if (!IsStorableId(candidate.ExternalId, limits.MaxExternalIdLength))
            {
                context.Truncated = true;
                continue;
            }

            // Immutable once stored: a repeated observation (every poll repeats them) changes nothing.
            if (context.RecordIndex.ContainsKey((candidate.ProviderId, candidate.ExternalId))) continue;
            if (context.Records.Count >= limits.MaxRecordsPerContext)
            {
                context.Truncated = true;
                break;
            }

            var record = Build(context, observation, candidate, now, out var newSubjects);
            // Real serialized bytes of just the addition, plus one separator byte per array element (an upper bound).
            var cost = Measure(record) + 1 + newSubjects.Sum(subject => Measure(subject) + 1);
            if (context.Bytes + cost > limits.MaxBytesPerContext)
            {
                // Rejected as a unit: the record and its subjects leave no index entry or accumulated size behind.
                context.Truncated = true;
                continue;
            }

            foreach (var subject in newSubjects)
            {
                context.SubjectsById[subject.SubjectId] = subject;
                context.SubjectsByKey[(subject.Kind, subject.ProviderId, subject.AnchorExternalId, subject.Ordinal, subject.Label)] = subject;
            }

            context.RecordIndex[(record.ProviderId, record.ExternalId)] = context.Records.Count;
            context.Records.Add(record);
            context.Bytes += cost;
        }
    }

    private ObservedBookRecord Build(
        Context context,
        BrowseObservation observation,
        BookCandidate candidate,
        DateTimeOffset now,
        out List<ObservedSubject> newSubjects)
    {
        var subjects = new List<ObservedSubject>();
        var pending = new Dictionary<(ObservedSubjectKind, string, string, int, string), ObservedSubject>();
        var providerName = Cut(observation.ProviderName, limits.MaxProviderNameLength);
        var title = Cut(candidate.Title, limits.MaxTitleLength);

        Guid SubjectFor(ObservedSubjectKind kind, int ordinal, string label)
        {
            var key = (kind, candidate.ProviderId, candidate.ExternalId, ordinal, label);
            if (context.SubjectsByKey.TryGetValue(key, out var existing)) return existing.SubjectId;
            if (pending.TryGetValue(key, out var queued)) return queued.SubjectId;
            var subject = new ObservedSubject(
                Guid.NewGuid(), kind, candidate.ProviderId, providerName, label, candidate.ExternalId, ordinal);
            pending[key] = subject;
            subjects.Add(subject);
            return subject.SubjectId;
        }

        var credits = new List<ObservedCredit>();
        var creditsTruncated = candidate.Authors.Count > limits.MaxCreditsPerRecord;
        for (var ordinal = 0; ordinal < Math.Min(candidate.Authors.Count, limits.MaxCreditsPerRecord); ordinal++)
        {
            var label = candidate.Authors[ordinal];
            if (!IsStorableLabel(label))
            {
                creditsTruncated = true;
                continue;
            }

            credits.Add(new ObservedCredit(ordinal, label, SubjectFor(ObservedSubjectKind.Author, ordinal, label)));
        }

        var memberships = new List<ObservedMembership>();
        var membershipsTruncated = candidate.Series.Count > limits.MaxMembershipsPerRecord;
        for (var ordinal = 0; ordinal < Math.Min(candidate.Series.Count, limits.MaxMembershipsPerRecord); ordinal++)
        {
            var series = candidate.Series[ordinal];
            if (!IsStorableLabel(series.Name))
            {
                membershipsTruncated = true;
                continue;
            }

            // An oversized position text is dropped rather than cut, so a shortened label never reads as the real one.
            var positionLabel = series.PositionLabel;
            if (positionLabel is not null && positionLabel.Length > limits.MaxPositionLabelLength)
            {
                positionLabel = null;
                membershipsTruncated = true;
            }

            memberships.Add(new ObservedMembership(
                ordinal, series.Name, positionLabel, series.PositionSort, series.IsPrimary,
                SubjectFor(ObservedSubjectKind.Series, ordinal, series.Name)));
        }

        var factsTruncated = false;
        var formats = new List<string>();
        foreach (var edition in candidate.Editions)
        {
            var format = edition.Format;
            if (string.IsNullOrWhiteSpace(format) || format.Length > limits.MaxFormatLength)
            {
                factsTruncated = true;
                continue;
            }

            if (formats.Contains(format, StringComparer.Ordinal)) continue;
            if (formats.Count >= limits.MaxFormatsPerRecord)
            {
                factsTruncated = true;
                continue;
            }

            formats.Add(format);
        }

        // The same assessment a normal candidate response uses; oversized supplied text is dropped, not cut.
        var version = BookCandidateVersion.Assess(candidate);
        var versionKind = version.Kind;
        var versionLabel = version.Label;
        if (versionKind.Length > limits.MaxVersionKindLength || versionLabel.Length > limits.MaxVersionLabelLength)
        {
            versionKind = null;
            versionLabel = null;
            factsTruncated = true;
        }

        newSubjects = subjects;
        return new ObservedBookRecord(
            candidate.ProviderId, providerName, candidate.ExternalId, title, candidate.PublicationDate,
            credits, memberships, now, creditsTruncated, membershipsTruncated,
            formats, versionKind, versionLabel, factsTruncated);
    }

    private bool IsStorableLabel(string? label) =>
        !string.IsNullOrWhiteSpace(label) && label.Length <= limits.MaxLabelLength;

    private static bool IsStorableId(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

    private static string Cut(string? value, int maximum)
    {
        value ??= string.Empty;
        return value.Length <= maximum ? value : value[..maximum];
    }

    /// <summary>Exact serialized bytes of one retained value under the canonical options.</summary>
    private static int Measure<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, CanonicalJson).Length;

    private sealed class Context(Guid id, Guid ownerId, string scopeKey, DateTimeOffset expiresAtUtc, long sequence)
    {
        public Guid Id { get; } = id;

        public Guid OwnerId { get; } = ownerId;

        public string ScopeKey { get; } = scopeKey;

        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

        public long Sequence { get; } = sequence;

        public List<ObservedBookRecord> Records { get; } = [];

        public Dictionary<(string Provider, string External), int> RecordIndex { get; } = [];

        public Dictionary<Guid, ObservedSubject> SubjectsById { get; } = [];

        public Dictionary<(ObservedSubjectKind Kind, string Provider, string External, int Ordinal, string Label), ObservedSubject> SubjectsByKey { get; } = [];

        public HashSet<string> FailedProviders { get; } = new(StringComparer.Ordinal);

        public bool Truncated { get; set; }

        public int Bytes { get; set; }
    }
}
