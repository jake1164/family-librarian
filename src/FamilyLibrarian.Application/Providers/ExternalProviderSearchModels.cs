namespace FamilyLibrarian.Application.Providers;

/// <summary>Protocol v2 §6/§11 — an open-string role (<c>author</c>/<c>editor</c>/<c>narrator</c>/<c>translator</c>/etc.); unrecognized values are tolerated, never rejected.</summary>
public sealed record BookAuthor(string Name, string? Role);

/// <summary><c>Position</c> is a flexible string ("6", "1.5", "prequel", ...), never a constrained numeric.</summary>
public sealed record BookSeries(string Name, string? Position);

/// <summary>An open-string identifier scheme (<c>isbn13</c>/<c>isbn10</c>/<c>asin</c>/<c>openlibrary-work</c>/etc.) — never a closed enum on either side of the wire.</summary>
public sealed record BookIdentifier(string Scheme, string Value);

/// <summary>Canonical-work evidence, independent of any specific edition or release (protocol v2 §6/§7).</summary>
public sealed record ExternalProviderWorkEvidence(
    string Title,
    string? Subtitle,
    IReadOnlyList<BookAuthor> Authors,
    IReadOnlyList<BookSeries> Series,
    IReadOnlyList<BookIdentifier> Identifiers)
{
    public static readonly ExternalProviderWorkEvidence Empty = new(string.Empty, null, [], [], []);
}

/// <summary>A specific published edition's evidence — language/publication/publisher/edition-scoped identifiers such as ISBN.</summary>
public sealed record ExternalProviderEditionEvidence(
    string? Language,
    int? PublicationYear,
    string? Publisher,
    IReadOnlyList<BookIdentifier> Identifiers)
{
    public static readonly ExternalProviderEditionEvidence Empty = new(null, null, null, []);
}

/// <summary>Release-level facts distinct from the canonical book — what lets FL reject a collection, a sample, or a mismatched abridgement without the provider making that judgment itself.</summary>
public sealed record ExternalProviderReleaseEvidence(
    string? Name,
    string? Format,
    long? SizeBytes,
    bool? IsCollection,
    int? PartCount,
    bool? IsSample,
    bool? IsAbridged,
    bool? IsUnabridged,
    IReadOnlyList<string> QualityTags,
    int? AgeDays,
    ExternalProviderDrmStatus DrmStatus = ExternalProviderDrmStatus.Unknown);

/// <summary>
/// Provider evidence about whether a release can be opened and converted.
/// Missing or unrecognized wire values are deliberately <see cref="Unknown"/>
/// and may never enable unattended acquisition.
/// </summary>
public enum ExternalProviderDrmStatus
{
    Unknown,
    None,
    Encrypted
}

/// <summary>Provider-side filtering hints (protocol v2 §14) — optional to honor; FL validates results independently regardless.</summary>
public sealed record ExternalProviderSearchConstraints(
    IReadOnlyList<string>? Languages = null,
    IReadOnlyList<string>? Formats = null,
    bool? ExcludeCollections = null);

/// <summary>Cursor-based pagination (protocol v2 §13) — optional for a provider to support.</summary>
public sealed record ExternalProviderSearchPagination(int? Limit = null, string? Cursor = null);
