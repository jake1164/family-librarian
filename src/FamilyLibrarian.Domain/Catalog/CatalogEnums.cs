namespace FamilyLibrarian.Domain.Catalog;

public enum PublicationStatus
{
    Unknown = 0,
    Published = 1,
    Forthcoming = 2
}

public enum EditionFormat
{
    Unknown = 0,
    Hardcover = 1,
    Paperback = 2,
    Ebook = 3,
    Audiobook = 4,
    Other = 5
}

public enum SeriesStatus
{
    Unknown = 0,
    Active = 1,
    Completed = 2
}

public enum ExternalReferenceEntityType
{
    Work = 1,
    Edition = 2,
    Author = 3,
    Series = 4
}

/// <summary>How much of a publication date a source actually stated. A year-only value is stored as the first day of that year.</summary>
public enum DatePrecision
{
    Day = 1,
    Month = 2,
    Year = 3
}

/// <summary>What a Work-level publication date means. <see cref="Unknown"/> is a source whose semantics are unverified.</summary>
public enum DateBasis
{
    Unknown = 0,
    Original = 1,
    EditionDerived = 2
}

/// <summary>Informational provenance of how an Edition came to belong to its Work. Never a safety gate.</summary>
public enum EditionIdentityBasis
{
    Created = 1,
    Reference = 2,
    Isbn = 3,
    AdminConfirmed = 4
}

/// <summary>Why an author credit is bound to an Author row. <see cref="Provisional"/> never claims a real person.</summary>
public enum AuthorBindingBasis
{
    Reference = 1,
    Name = 2,
    Created = 3,
    Provisional = 4
}

/// <summary>Why a series membership is bound to a Series row. An ambiguous series is an unbound claim, never an entry.</summary>
public enum SeriesBindingBasis
{
    Reference = 1,
    Name = 2,
    Created = 3
}

/// <summary>Whether the sources agree about one series position. Absent (null) means a legacy value nobody attributed.</summary>
public enum SeriesPositionState
{
    SingleSource = 1,
    Agreed = 2,
    Conflicted = 3
}

/// <summary>The catalog facts that keep competing, attributed claims.</summary>
public enum MetadataClaimField
{
    SeriesPosition = 1,
    OriginalPublicationDate = 2,
    EditionPublicationDate = 3,
    Description = 4,
    CoverUrl = 5,
    PageCount = 6
}
