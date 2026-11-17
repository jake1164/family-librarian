using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Discovery;

/// <summary>
/// A provider's own identity for one entity: provider + native id + entity kind. This is the only thing that may fill a
/// source id. A label, a search hit, or a synthetic group key is never a <see cref="SourceRef"/>. Over-long ids are
/// rejected, never truncated, because a cut id would address a different entity.
/// </summary>
public sealed record SourceRef
{
    public const int MaxProviderIdLength = 128;
    public const int MaxExternalIdLength = 512;

    public SourceRef(string providerId, string externalId, ExternalReferenceEntityType kind)
    {
        if (!TryValidate(providerId, externalId, kind, out var problem))
        {
            throw new ArgumentException(problem);
        }

        ProviderId = providerId;
        ExternalId = externalId;
        Kind = kind;
    }

    public string ProviderId { get; }

    public string ExternalId { get; }

    public ExternalReferenceEntityType Kind { get; }

    /// <summary>Builds a reference from provider-supplied text; false (never an exception) when the text is not a usable id.</summary>
    public static bool TryCreate(string? providerId, string? externalId, ExternalReferenceEntityType kind, out SourceRef? value)
    {
        if (TryValidate(providerId, externalId, kind, out _))
        {
            value = new SourceRef(providerId!, externalId!, kind);
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryValidate(string? providerId, string? externalId, ExternalReferenceEntityType kind, out string problem)
    {
        problem = string.Empty;
        if (!Usable(providerId, MaxProviderIdLength))
        {
            problem = "Provider id is missing, padded or too long.";
        }
        else if (!Usable(externalId, MaxExternalIdLength))
        {
            problem = "External id is missing, padded or too long.";
        }
        else if (!Enum.IsDefined(kind))
        {
            problem = "Entity kind is not defined.";
        }

        return problem.Length == 0;
    }

    private static bool Usable(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value == value.Trim();
}

/// <summary>How a relationship between two entities is known.</summary>
public enum RelationshipBasis
{
    /// <summary>The provider states the relationship itself (a series membership row, an author credit).</summary>
    Native = 1,

    /// <summary>Computed by this application from other records within a stated scope (for example author to series from that author's works).</summary>
    Derived = 2
}

/// <summary>
/// How an edge is known, kept apart from whether its endpoints are native. A derived author-to-series edge can point at a
/// genuinely native <see cref="SeriesRef"/>; the <em>edge</em> is derived, the target id is not.
/// </summary>
public sealed record RelationshipEvidence
{
    private RelationshipEvidence(RelationshipBasis basis, string? derivedScope)
    {
        Basis = basis;
        DerivedScope = derivedScope;
    }

    public static RelationshipEvidence Native { get; } = new(RelationshipBasis.Native, null);

    public RelationshipBasis Basis { get; }

    /// <summary>What the derivation covered (a stable code such as <c>author-works-first-page</c>); null for a native edge.</summary>
    public string? DerivedScope { get; }

    public static RelationshipEvidence Derived(string scope)
    {
        DiscoveryCodes.ThrowIfNotCode(scope);
        return new RelationshipEvidence(RelationshipBasis.Derived, scope);
    }
}

/// <summary>Whether a source's canonical-series statement may be preferred when previewing. Read-only; never a merge.</summary>
public enum CanonicalLinkState
{
    NotStated = 0,

    /// <summary>The source says the series has a canonical series but its meaning, or the target, is not verified.</summary>
    StatedUnverified = 1,

    /// <summary>A same-provider, one-hop target with no further canonical statement. May be preferred in a preview.</summary>
    Verified = 2,

    /// <summary>Self, cyclic, chained, cross-provider, wrong-kind or missing target. Surfaced as a warning and a choice.</summary>
    Invalid = 3
}

public enum CanonicalTargetLookup
{
    NotAttempted = 0,
    Missing = 1,
    Found = 2
}

/// <summary>
/// A source's statement that one series duplicates another, with the verification outcome. Both ids are retained. It never
/// authorises a durable alias, an observation write, a merge or a rebinding.
/// </summary>
public sealed record CanonicalSeriesLink(SourceRef Original, SourceRef? Target, CanonicalLinkState State, string? ReasonCode, string? TargetLabel = null)
{
    /// <summary>
    /// Decides how far a stated canonical target may be trusted. <paramref name="semanticsVerified"/> is whether the
    /// provider's current documentation and the returned record establish what the field means; when it is false the
    /// statement is kept but never preferred.
    /// </summary>
    /// <param name="targetsOwnCanonical">The canonical target the <em>target</em> series itself states, when the target was fetched.</param>
    public static CanonicalSeriesLink Evaluate(
        SourceRef original,
        SourceRef? statedTarget,
        bool semanticsVerified,
        CanonicalTargetLookup lookup,
        SourceRef? targetsOwnCanonical,
        string? targetLabel = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (statedTarget is null)
        {
            return new CanonicalSeriesLink(original, null, CanonicalLinkState.NotStated, null);
        }

        if (statedTarget == original)
        {
            return Invalid("canonical-self");
        }

        if (statedTarget.Kind != ExternalReferenceEntityType.Series ||
            !string.Equals(statedTarget.ProviderId, original.ProviderId, StringComparison.Ordinal))
        {
            return Invalid("canonical-scope-mismatch");
        }

        if (!semanticsVerified)
        {
            return new CanonicalSeriesLink(original, statedTarget, CanonicalLinkState.StatedUnverified, "canonical-semantics-unverified");
        }

        switch (lookup)
        {
            case CanonicalTargetLookup.NotAttempted:
                return new CanonicalSeriesLink(original, statedTarget, CanonicalLinkState.StatedUnverified, "canonical-target-not-checked");
            case CanonicalTargetLookup.Missing:
                return Invalid("canonical-target-missing");
        }

        if (targetsOwnCanonical is not null)
        {
            return Invalid(targetsOwnCanonical == original ? "canonical-cycle" : "canonical-chain");
        }

        return new CanonicalSeriesLink(original, statedTarget, CanonicalLinkState.Verified, null, targetLabel);

        CanonicalSeriesLink Invalid(string reason) => new(original, statedTarget, CanonicalLinkState.Invalid, reason);
    }

    /// <summary>The series a preview may show first. The original is returned unless the target is verified.</summary>
    public SourceRef PreferredForPreview => State == CanonicalLinkState.Verified && Target is not null ? Target : Original;
}

/// <summary>
/// A series as a provider reported it. <see cref="Id"/> null means a label-only, unbound observation; such a series can
/// never be sent to a native-id lookup. Counts and completion are the source's statements, not proof of completeness.
/// </summary>
public sealed record SeriesRef(
    string Label,
    SourceRef? Id,
    bool? IsCompleted = null,
    int? PrimaryBooksCount = null,
    int? BooksCount = null,
    CanonicalSeriesLink? Canonical = null)
{
    public SourceRef? Id { get; } = Id is null || Id.Kind == ExternalReferenceEntityType.Series
        ? Id
        : throw new ArgumentException("A series reference must have the Series kind.", nameof(Id));

    public bool IsBound => Id is not null;
}

/// <summary>An author as a provider reported it. Counts are evidence to show a person; they are never used to choose.</summary>
public sealed record AuthorRef(string Name, SourceRef Id, string? Disambiguation = null, int? WorkCount = null)
{
    public SourceRef Id { get; } = Id.Kind == ExternalReferenceEntityType.Author
        ? Id
        : throw new ArgumentException("An author reference must have the Author kind.", nameof(Id));
}

/// <summary>One credit exactly as the source listed it. The ordinal and own id keep two same-named people apart.</summary>
public sealed record CreditRef(int Ordinal, string Label, string? Role, SourceRef? AuthorId)
{
    public SourceRef? AuthorId { get; } = AuthorId is null || AuthorId.Kind == ExternalReferenceEntityType.Author
        ? AuthorId
        : throw new ArgumentException("A credit id must have the Author kind.", nameof(AuthorId));
}

/// <summary>A book already known to the caller that vouches for an author candidate: a source work/edition, or an ISBN-13.</summary>
public sealed record AnchorBook
{
    private AnchorBook(SourceRef? source, string? isbn13)
    {
        Source = source;
        Isbn13 = isbn13;
    }

    public SourceRef? Source { get; }

    public string? Isbn13 { get; }

    public static AnchorBook FromSource(SourceRef source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind is not (ExternalReferenceEntityType.Work or ExternalReferenceEntityType.Edition))
        {
            throw new ArgumentException("An anchor book must be a Work or Edition.", nameof(source));
        }

        return new AnchorBook(source, null);
    }

    public static AnchorBook FromIsbn13(string isbn13)
    {
        if (isbn13 is not { Length: 13 } || !isbn13.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("An ISBN-13 must be exactly 13 digits.", nameof(isbn13));
        }

        return new AnchorBook(null, isbn13);
    }
}

public enum AuthorAnchorKind
{
    /// <summary>A name typed or displayed with nothing vouching for it. Can only produce choices.</summary>
    NameOnly = 0,

    /// <summary>A provider author id, for example from a credit on a book the same provider returned.</summary>
    SourceId = 1,

    /// <summary>A name plus at least one book that must appear among the candidate's works.</summary>
    NameWithBooks = 2
}

/// <summary>What the caller can offer to establish which person an author is. Name alone is representable, but it cannot select.</summary>
public sealed record AuthorAnchor
{
    private AuthorAnchor(AuthorAnchorKind kind, string? name, SourceRef? id, IReadOnlyList<AnchorBook> books)
    {
        Kind = kind;
        Name = name;
        Id = id;
        Books = books;
    }

    public AuthorAnchorKind Kind { get; }

    public string? Name { get; }

    public SourceRef? Id { get; }

    public IReadOnlyList<AnchorBook> Books { get; }

    public static AuthorAnchor FromSourceId(SourceRef authorId)
    {
        ArgumentNullException.ThrowIfNull(authorId);
        if (authorId.Kind != ExternalReferenceEntityType.Author)
        {
            throw new ArgumentException("An author anchor id must have the Author kind.", nameof(authorId));
        }

        return new AuthorAnchor(AuthorAnchorKind.SourceId, null, authorId, []);
    }

    public static AuthorAnchor FromNameAndBooks(string name, IReadOnlyList<AnchorBook> books)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var kept = DiscoverySnapshots.Of(books);
        if (kept.Count == 0 || kept.Any(book => book is null))
        {
            throw new ArgumentException("At least one anchor book is required, and none may be missing.", nameof(books));
        }

        return new AuthorAnchor(AuthorAnchorKind.NameWithBooks, name.Trim(), null, kept);
    }

    public static AuthorAnchor FromNameOnly(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new AuthorAnchor(AuthorAnchorKind.NameOnly, name.Trim(), null, []);
    }
}
